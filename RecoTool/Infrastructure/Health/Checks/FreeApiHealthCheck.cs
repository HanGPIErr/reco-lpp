using System.Threading;
using System.Threading.Tasks;
using RecoTool.Services.External;

namespace RecoTool.Infrastructure.Health.Checks
{
    /// <summary>
    /// Verifies the Free API client is authenticated. **Passive** check: it ONLY reads
    /// <see cref="IFreeApiClient.IsAuthenticated"/> — it never triggers an authentication
    /// attempt itself.
    ///
    /// <para>
    /// Why passive? The Free API auth shows a MODAL DIALOG on the user's screen. If the
    /// health check called <c>AuthenticateAsync</c> and the legacy startup path
    /// (<c>App.OnStartup</c>) also calls it, the user would see the dialog TWICE — once
    /// from the health check, once from the legacy path. The legacy path is responsible
    /// for triggering the actual auth attempt; this check only observes the outcome.
    /// </para>
    ///
    /// <para>
    /// If no <see cref="IFreeApiClient"/> is registered in DI (test bench / offline
    /// build), the check is skipped and reported Healthy — the constructor accepts a
    /// nullable client so DI can pass <c>null</c> when nothing is registered.
    /// </para>
    /// </summary>
    public sealed class FreeApiHealthCheck : IStartupHealthCheck
    {
        private readonly IFreeApiClient _client;

        public FreeApiHealthCheck(IFreeApiClient client)
        {
            // Nullable on purpose — see class summary.
            _client = client;
        }

        public string Name => "Free API";

        public Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            if (_client == null)
            {
                return Task.FromResult(HealthCheckResult.Healthy("No Free API client registered — skipped."));
            }

            bool authenticated;
            try
            {
                authenticated = _client.IsAuthenticated;
            }
            catch
            {
                // Buggy implementation: treat as "not authenticated" without dialog.
                authenticated = false;
            }

            if (authenticated)
            {
                return Task.FromResult(HealthCheckResult.Healthy("Free API authenticated."));
            }

            // Passive: do NOT call AuthenticateAsync. The legacy App.OnStartup path will
            // attempt that with the appropriate UI flow. We just report "not authenticated"
            // so the diagnostic panel (UAT mode) can surface the state to the user.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Free API not authenticated. API-enriched columns will be empty until the user authenticates."));
        }
    }
}
