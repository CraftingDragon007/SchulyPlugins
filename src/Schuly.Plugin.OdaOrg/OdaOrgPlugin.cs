using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using System.Text.Json;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Services;

namespace Schuly.Plugin.OdaOrg
{
    public class OdaOrgPlugin : ISchulyPlugin
    {
        public const string PluginName = "OdaOrg Integration";

        /// <summary>Reported version — derived from the assembly (csproj &lt;Version&gt;) so it never drifts.</summary>
        public static readonly string PluginVersion =
            typeof(OdaOrgPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            services.AddDbContext<OdaOrgDbContext>(options => options.UseNpgsql(context.ConnectionString));

            services.AddSingleton<OdaOrgSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<OdaOrgSyncTask>());

            services.AddScoped<OdaOrgScraper>();
            services.AddScoped<ProvisioningService>();
            services.AddScoped<GradesSyncService>();
            services.AddScoped<AgendaSyncService>();

            // Credentials live in this plugin's isolated, in-memory vault (keyed by
            // the plugin name by the host) instead of the database.
            services.AddScoped(sp => new OdaOrgSecretStore(
                sp.GetRequiredKeyedService<IPluginVault>(PluginName)));

            services.AddScoped<IPluginLogin, OdaOrgLogin>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>();
            await PreserveLegacySecretsAsync(
                db,
                scope.ServiceProvider.GetRequiredKeyedService<IPluginVault>(PluginName),
                cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
        }

        private static async Task PreserveLegacySecretsAsync(
            OdaOrgDbContext db,
            IPluginVault vault,
            CancellationToken cancellationToken)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var connection = db.Database.GetDbConnection();
                var columns = new HashSet<string>(StringComparer.Ordinal);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        SELECT column_name
                        FROM information_schema.columns
                        WHERE table_schema = current_schema() AND table_name = 'Accounts'
                        """;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        columns.Add(reader.GetString(0));
                }

                if (!columns.Contains("Id"))
                    return; // Fresh plugin database; EF creates the table below.

                var username = columns.Contains("Username") ? "\"Username\"" : "NULL AS \"Username\"";
                var password = columns.Contains("Password") ? "\"Password\"" : "NULL AS \"Password\"";
                await using var readAccounts = connection.CreateCommand();
                readAccounts.CommandText = $"SELECT \"Id\", {username}, {password} FROM \"Accounts\"";
                await using var accounts = await readAccounts.ExecuteReaderAsync(cancellationToken);
                while (await accounts.ReadAsync(cancellationToken))
                {
                    var id = accounts.GetGuid(0);
                    var secrets = new LegacyCredentials(
                        ReadString(accounts, "Username"),
                        ReadString(accounts, "Password"));
                    if (!secrets.HasValues)
                        continue;

                    var key = $"account:{id}";
                    if (vault.Contains(key))
                        continue; // A prior startup already copied and verified this account.

                    var json = JsonSerializer.Serialize(secrets);
                    vault.Set(key, json);
                    if (!vault.TryGet(key, out var storedJson) || storedJson != json)
                        throw new InvalidOperationException($"Could not verify vault migration for OdAOrg account {id}.");
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        private static string? ReadString(DbDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private sealed record LegacyCredentials(string? Username, string? Password)
        {
            public bool HasValues => Username is not null || Password is not null;
        }
    }
}
