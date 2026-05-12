using System;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Services.Sync;

namespace RecoTool.Infrastructure.Health.Checks
{
    /// <summary>
    /// Probes the configured network share via <see cref="INetworkPathProvider.IsNetworkAvailable"/>.
    /// The provider's implementation already short-circuits to a quick file-share probe,
    /// so the check is cheap.
    ///
    /// <para>
    /// Failure here is the single most common runtime issue (VPN dropped, corporate
    /// share migration, wrong credentials) — surfacing it at startup lets the user
    /// fix it before launching an import.
    /// </para>
    /// </summary>
    public sealed class NetworkShareHealthCheck : IStartupHealthCheck
    {
        private readonly INetworkPathProvider _network;

        public NetworkShareHealthCheck(INetworkPathProvider network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        public string Name => "Network share";

        public Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var available = _network.IsNetworkAvailable();
                if (available)
                {
                    return Task.FromResult(HealthCheckResult.Healthy("Network share reachable."));
                }

                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Network share is not reachable. The app will run in offline mode " +
                    "and sync operations will be unavailable until the connection is restored."));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Network share probe was cancelled (timeout)."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Error probing network share: " + ex.Message, ex));
            }
        }
    }
}
