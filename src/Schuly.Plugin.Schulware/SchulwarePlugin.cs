using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using System.Text.Json;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware
{
    public class SchulwarePlugin : ISchulyPlugin
    {
        public const string PluginName = "Schulware Integration";

        /// <summary>Reported version — derived from the assembly (csproj &lt;Version&gt;) so it never drifts.</summary>
        public static readonly string PluginVersion =
            typeof(SchulwarePlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            // Hard requirement: a Schuly.Plugin.Schulware.yml must sit in the
            // backend's plugins-config directory with at least SchulwareApi.BaseUrl
            // populated. Without it we have nowhere to talk to, so refuse to load
            // rather than fail silently at sync time.
            var baseUrl = context.Configuration["SchulwareApi:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException(
                    "Schulware plugin: missing config. Drop a Schuly.Plugin.Schulware.yml " +
                    "into the backend's plugins-config directory with at least " +
                    "SchulwareApi.BaseUrl set. See the source repo for the example schema.");

            services.AddDbContext<SchulwareDbContext>(options => options.UseNpgsql(context.ConnectionString));

            services.AddSingleton<SchulwareSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<SchulwareSyncTask>());
            services.AddScoped<TokenRefreshService>();
            services.AddScoped<GradesSyncService>();
            services.AddScoped<AbsencesSyncService>();
            services.AddScoped<AgendaSyncService>();
            services.AddScoped<DocumentsSyncService>();
            services.AddScoped<VacationsSyncService>();
            services.AddScoped<SchoolProvisioningService>();

            // The account auth secrets live in this plugin's isolated, in-memory
            // vault (keyed by the plugin name by the host) instead of the database.
            services.AddScoped(sp => new AccountSecretStore(
                sp.GetRequiredKeyedService<IPluginVault>(PluginName)));

            services.AddScoped<IPluginLogin, SchulwareLogin>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            await PreserveLegacySecretsAsync(
                db,
                scope.ServiceProvider.GetRequiredKeyedService<IPluginVault>(PluginName),
                cancellationToken);
            await db.Database.MigrateAsync(cancellationToken);
        }

        private static async Task PreserveLegacySecretsAsync(
            SchulwareDbContext db,
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

                string[] legacyColumns =
                [
                    "MobileAccessToken", "MobileRefreshToken", "WebSessionId",
                    "WebSessionUserId", "WebSessionTransId", "ContextStateJson", "UserAgent"
                ];
                var selectedColumns = legacyColumns
                    .Select(column => columns.Contains(column)
                        ? $"\"{column}\""
                        : $"NULL AS \"{column}\"");

                await using var readAccounts = connection.CreateCommand();
                readAccounts.CommandText = $"SELECT \"Id\", {string.Join(", ", selectedColumns)} FROM \"Accounts\"";
                await using var accounts = await readAccounts.ExecuteReaderAsync(cancellationToken);
                while (await accounts.ReadAsync(cancellationToken))
                {
                    var id = accounts.GetGuid(0);
                    var secrets = new LegacySecrets(
                        ReadString(accounts, "MobileAccessToken"),
                        ReadString(accounts, "MobileRefreshToken"),
                        ReadString(accounts, "WebSessionId"),
                        ReadString(accounts, "WebSessionUserId"),
                        ReadString(accounts, "WebSessionTransId"),
                        ReadString(accounts, "ContextStateJson"),
                        ReadString(accounts, "UserAgent"));

                    if (!secrets.HasValues)
                        continue;

                    var key = $"account:{id}";
                    if (vault.Contains(key))
                        continue; // A prior startup already copied and verified this account.

                    var json = JsonSerializer.Serialize(secrets);
                    vault.Set(key, json);
                    if (!vault.TryGet(key, out var storedJson) || storedJson != json)
                        throw new InvalidOperationException($"Could not verify vault migration for Schulware account {id}.");
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

        private sealed record LegacySecrets(
            string? AccessToken,
            string? RefreshToken,
            string? WebSessionId,
            string? WebSessionUserId,
            string? WebSessionTransId,
            string? SessionCookiesJson,
            string? UserAgent)
        {
            public bool HasValues => AccessToken is not null || RefreshToken is not null ||
                WebSessionId is not null || WebSessionUserId is not null ||
                WebSessionTransId is not null || SessionCookiesJson is not null || UserAgent is not null;
        }
    }
}
