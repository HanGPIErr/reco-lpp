using System;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Infrastructure.Health
{
    /// <summary>
    /// A single, narrow probe executed at application startup. Used by
    /// <see cref="HealthCheckRunner"/> to surface infrastructure problems
    /// (network share unreachable, local DB missing, Free API auth broken, ...)
    /// BEFORE the user discovers them by clicking on a hidden code path.
    ///
    /// <para>
    /// Implementations must be cheap and bounded — the runner enforces an overall
    /// short timeout (~10 s) but each individual check should aim for a few seconds
    /// at most so that even a degraded environment can boot in reasonable time.
    /// Implementations must NOT throw — catch all exceptions internally and
    /// translate them into <see cref="HealthCheckResult.Unhealthy(string, Exception)"/>.
    /// </para>
    /// </summary>
    public interface IStartupHealthCheck
    {
        /// <summary>
        /// Human-readable name shown to the user when a failure dialog is rendered
        /// (e.g. "Local database", "Network share", "Free API").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Executes the probe. Implementations should honour <paramref name="ct"/>
        /// so the runner-level timeout can short-circuit a slow check.
        /// </summary>
        Task<HealthCheckResult> CheckAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Immutable outcome of a single <see cref="IStartupHealthCheck"/>.
    /// Build via the static factories so the invariants (Exception non-null implies
    /// Unhealthy) are always upheld.
    /// </summary>
    public sealed class HealthCheckResult
    {
        public bool IsHealthy { get; }
        public string Message { get; }
        public Exception Exception { get; }

        private HealthCheckResult(bool healthy, string message, Exception exception)
        {
            IsHealthy = healthy;
            Message = message ?? string.Empty;
            Exception = exception;
        }

        /// <summary>Success result — no message required, no exception attached.</summary>
        public static HealthCheckResult Healthy(string message = null)
            => new HealthCheckResult(true, message ?? "OK", null);

        /// <summary>
        /// Failure result. Message is shown to the user; exception (optional) is
        /// logged via <see cref="HealthCheckRunner"/>.
        /// </summary>
        public static HealthCheckResult Unhealthy(string message, Exception exception = null)
            => new HealthCheckResult(false, message ?? "Unknown failure", exception);
    }
}
