using System;
using System.Data.OleDb;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Services;

namespace RecoTool.Infrastructure.Health.Checks
{
    /// <summary>
    /// Verifies that the local Access database for the current country exists and can
    /// be opened with an OleDb connection. This catches the most common offline-mode
    /// failure (corrupt or missing local .accdb) before the user clicks Reconciliation
    /// or Import and gets a stack trace.
    ///
    /// <para>
    /// If there is no current country yet (fresh install / user not logged in), the
    /// check is considered Healthy — there's nothing to validate at this stage.
    /// </para>
    /// </summary>
    public sealed class LocalDatabaseHealthCheck : IStartupHealthCheck
    {
        private readonly IOfflineFirstService _offline;

        public LocalDatabaseHealthCheck(IOfflineFirstService offline)
        {
            _offline = offline ?? throw new ArgumentNullException(nameof(offline));
        }

        public string Name => "Local database";

        public async Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            string countryId;
            string dbPath;
            string connStr;
            try
            {
                countryId = _offline.CurrentCountryId;
                if (string.IsNullOrWhiteSpace(countryId))
                {
                    return HealthCheckResult.Healthy("No current country selected — skipped.");
                }

                dbPath = _offline.GetLocalAmbreDatabasePath(countryId);
                connStr = _offline.GetAmbreConnectionString(countryId);
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Cannot resolve local database path.", ex);
            }

            if (string.IsNullOrWhiteSpace(dbPath))
            {
                return HealthCheckResult.Unhealthy(
                    $"No local database path resolved for country '{countryId}'.");
            }

            if (!File.Exists(dbPath))
            {
                return HealthCheckResult.Unhealthy(
                    $"Local database file not found: {dbPath}");
            }

            if (string.IsNullOrWhiteSpace(connStr))
            {
                return HealthCheckResult.Unhealthy(
                    $"No connection string resolved for country '{countryId}'.");
            }

            try
            {
                using (var conn = new OleDbConnection(connStr))
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    // Just opening is enough — we don't run a query because the file lock
                    // semantics of Access can break legitimate concurrent users.
                }
                return HealthCheckResult.Healthy($"Local database OK ({Path.GetFileName(dbPath)}).");
            }
            catch (OperationCanceledException)
            {
                return HealthCheckResult.Unhealthy("Local database probe was cancelled (timeout).");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy(
                    $"Cannot open local database '{Path.GetFileName(dbPath)}': {ex.Message}", ex);
            }
        }
    }
}
