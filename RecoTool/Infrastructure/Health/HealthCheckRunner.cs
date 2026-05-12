using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RecoTool.Infrastructure.Health
{
    /// <summary>
    /// Runs every registered <see cref="IStartupHealthCheck"/> in parallel, with an overall
    /// timeout, and returns all results.
    ///
    /// <para>
    /// Design notes:
    /// <list type="bullet">
    ///   <item>Parallel execution: each check runs on its own Task — the slowest one
    ///         (capped at <see cref="DefaultTimeout"/>) sets the wall-clock for startup.</item>
    ///   <item>No throwing: a check that explodes is reported as Unhealthy with the
    ///         exception attached; the runner never bubbles the exception up.</item>
    ///   <item>Timeout: when a check exceeds the per-run timeout, it is reported as
    ///         Unhealthy with a clear message. The runner returns synchronously once
    ///         the timeout elapses, regardless of whether the background task
    ///         eventually finishes.</item>
    ///   <item>Logging: every result is logged through <see cref="ILogger"/> — healthy
    ///         as Information, unhealthy as Warning (with the exception, if any).</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class HealthCheckRunner
    {
        /// <summary>Overall timeout when the caller does not supply a CancellationToken with one.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        private readonly IReadOnlyList<IStartupHealthCheck> _checks;
        private readonly ILogger<HealthCheckRunner> _logger;

        public HealthCheckRunner(IEnumerable<IStartupHealthCheck> checks, ILogger<HealthCheckRunner> logger)
        {
            if (checks == null) throw new ArgumentNullException(nameof(checks));
            _checks = checks.Where(c => c != null).ToList();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Runs every check in parallel. Returns one tuple per registered check, in the
        /// same order they were registered. Never throws — errors are surfaced via
        /// <see cref="HealthCheckResult.Unhealthy(string, Exception)"/>.
        /// </summary>
        public async Task<IReadOnlyList<(string Name, HealthCheckResult Result)>> RunAllAsync(
            CancellationToken ct = default)
        {
            if (_checks.Count == 0)
            {
                _logger.LogInformation("No startup health checks registered — skipping.");
                return Array.Empty<(string, HealthCheckResult)>();
            }

            // Per-run timeout: linked source so the caller's ct still cancels us, but
            // we also stop at DefaultTimeout in case the caller passed CancellationToken.None.
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(DefaultTimeout);

                var tasks = _checks.Select(c => RunOneAsync(c, timeoutCts.Token)).ToArray();

                // Task.WhenAll won't throw because RunOneAsync swallows exceptions.
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);

                foreach (var (name, result) in results)
                {
                    if (result.IsHealthy)
                    {
                        _logger.LogInformation("Health check OK: {Check} — {Message}", name, result.Message);
                    }
                    else
                    {
                        if (result.Exception != null)
                            _logger.LogWarning(result.Exception, "Health check FAILED: {Check} — {Message}", name, result.Message);
                        else
                            _logger.LogWarning("Health check FAILED: {Check} — {Message}", name, result.Message);
                    }
                }

                return results;
            }
        }

        private static async Task<(string Name, HealthCheckResult Result)> RunOneAsync(
            IStartupHealthCheck check, CancellationToken ct)
        {
            var name = SafeName(check);
            try
            {
                var task = check.CheckAsync(ct);
                // If the check was well-behaved and honoured ct, this is a no-op.
                // If it ignored ct, we still bound our wait via WhenAny + cancellation token.
                var completed = await Task.WhenAny(task, CancellationDelay(ct)).ConfigureAwait(false);
                if (completed != task)
                {
                    return (name, HealthCheckResult.Unhealthy(
                        $"Timed out after {HealthCheckRunner.DefaultTimeout.TotalSeconds:0}s"));
                }

                var result = await task.ConfigureAwait(false)
                             ?? HealthCheckResult.Unhealthy("Check returned null result");
                return (name, result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (name, HealthCheckResult.Unhealthy(
                    $"Timed out after {HealthCheckRunner.DefaultTimeout.TotalSeconds:0}s"));
            }
            catch (Exception ex)
            {
                return (name, HealthCheckResult.Unhealthy("Unexpected error: " + ex.Message, ex));
            }
        }

        private static string SafeName(IStartupHealthCheck check)
        {
            try { return string.IsNullOrWhiteSpace(check?.Name) ? check?.GetType().Name : check.Name; }
            catch { return check?.GetType().Name ?? "unknown"; }
        }

        /// <summary>
        /// Returns a Task that completes when <paramref name="ct"/> is cancelled.
        /// Used as the "loser" side of a WhenAny race so we never wait longer than
        /// the runner timeout, even if a check ignores cancellation.
        /// </summary>
        private static Task CancellationDelay(CancellationToken ct)
        {
            if (!ct.CanBeCanceled) return Task.Delay(Timeout.Infinite);
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetResult(null));
            return tcs.Task;
        }
    }
}
