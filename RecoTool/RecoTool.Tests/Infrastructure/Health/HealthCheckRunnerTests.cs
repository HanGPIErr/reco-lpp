using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecoTool.Infrastructure.Health;
using Xunit;

namespace RecoTool.Tests.Infrastructure.Health
{
    /// <summary>
    /// Tests for <see cref="HealthCheckRunner"/>. We cover orchestration only —
    /// the concrete checks are tested separately (they need OleDb / network mocks).
    ///
    /// <para>
    /// All checks here are stubs : <see cref="StubCheck"/> for predictable
    /// outcomes, <see cref="HangingCheck"/> for timeout coverage, and
    /// <see cref="ThrowingCheck"/> to make sure a buggy check never propagates.
    /// </para>
    /// </summary>
    public class HealthCheckRunnerTests
    {
        private static ILogger<HealthCheckRunner> NullLogger() =>
            NullLoggerFactory.Instance.CreateLogger<HealthCheckRunner>();

        // ===== ctor =====

        [Fact]
        public void Ctor_NullChecks_Throws()
        {
            Action act = () => new HealthCheckRunner(null, NullLogger());
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_NullLogger_Throws()
        {
            Action act = () => new HealthCheckRunner(Array.Empty<IStartupHealthCheck>(), null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_FiltersOutNullCheckEntries()
        {
            var checks = new IStartupHealthCheck[]
            {
                new StubCheck("ok", () => HealthCheckResult.Healthy()),
                null
            };
            // Should not throw, should not crash later when we run.
            var sut = new HealthCheckRunner(checks, NullLogger());
            sut.Should().NotBeNull();
        }

        // ===== Empty set =====

        [Fact]
        public async Task RunAllAsync_NoChecks_ReturnsEmpty()
        {
            var sut = new HealthCheckRunner(Array.Empty<IStartupHealthCheck>(), NullLogger());

            var results = await sut.RunAllAsync();

            results.Should().NotBeNull();
            results.Should().BeEmpty();
        }

        // ===== Healthy / unhealthy mix =====

        [Fact]
        public async Task RunAllAsync_AggregatesAllResults_HealthyAndUnhealthy()
        {
            var sut = new HealthCheckRunner(new IStartupHealthCheck[]
            {
                new StubCheck("a", () => HealthCheckResult.Healthy("a-ok")),
                new StubCheck("b", () => HealthCheckResult.Unhealthy("b-down")),
                new StubCheck("c", () => HealthCheckResult.Healthy("c-ok")),
            }, NullLogger());

            var results = await sut.RunAllAsync();

            results.Should().HaveCount(3);
            results.Select(r => r.Name).Should().BeEquivalentTo(new[] { "a", "b", "c" });

            var healthy = results.Where(r => r.Result.IsHealthy).Select(r => r.Name).ToList();
            healthy.Should().BeEquivalentTo(new[] { "a", "c" });

            var unhealthy = results.Single(r => !r.Result.IsHealthy);
            unhealthy.Name.Should().Be("b");
            unhealthy.Result.Message.Should().Be("b-down");
        }

        // ===== Parallel execution =====

        [Fact]
        public async Task RunAllAsync_RunsChecksInParallel()
        {
            // 3 checks that each wait 200 ms. If run serially this would take ~600 ms;
            // in parallel it should complete in ~250–350 ms. We assert <500 ms to
            // be robust under CI load while still catching a serial regression.
            var delay = TimeSpan.FromMilliseconds(200);
            var sut = new HealthCheckRunner(new IStartupHealthCheck[]
            {
                new DelayCheck("c1", delay),
                new DelayCheck("c2", delay),
                new DelayCheck("c3", delay),
            }, NullLogger());

            var sw = Stopwatch.StartNew();
            var results = await sut.RunAllAsync();
            sw.Stop();

            results.Should().HaveCount(3);
            results.Should().OnlyContain(r => r.Result.IsHealthy);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
                "the runner must execute checks in parallel — serial would take ~3×200ms");
        }

        // ===== Throwing check =====

        [Fact]
        public async Task RunAllAsync_CheckThatThrows_IsReportedAsUnhealthy_DoesNotPropagate()
        {
            var sut = new HealthCheckRunner(new IStartupHealthCheck[]
            {
                new StubCheck("ok", () => HealthCheckResult.Healthy()),
                new ThrowingCheck("kaboom", new InvalidOperationException("nope")),
            }, NullLogger());

            // Must not throw.
            var results = await sut.RunAllAsync();

            results.Should().HaveCount(2);
            var bad = results.Single(r => r.Name == "kaboom");
            bad.Result.IsHealthy.Should().BeFalse();
            bad.Result.Exception.Should().BeOfType<InvalidOperationException>();
            bad.Result.Message.Should().Contain("nope");
        }

        // ===== Null result =====

        [Fact]
        public async Task RunAllAsync_CheckReturnsNull_IsReportedAsUnhealthy()
        {
            var sut = new HealthCheckRunner(new IStartupHealthCheck[]
            {
                new StubCheck("nullish", () => null),
            }, NullLogger());

            var results = await sut.RunAllAsync();

            results.Should().ContainSingle();
            results[0].Result.IsHealthy.Should().BeFalse();
        }

        // ===== Cancellation / timeout =====

        [Fact]
        public async Task RunAllAsync_RespectsCallerCancellation()
        {
            using (var cts = new CancellationTokenSource())
            {
                var sut = new HealthCheckRunner(new IStartupHealthCheck[]
                {
                    new HangingCheck("hang"),
                }, NullLogger());

                cts.CancelAfter(TimeSpan.FromMilliseconds(100));
                var sw = Stopwatch.StartNew();
                var results = await sut.RunAllAsync(cts.Token);
                sw.Stop();

                results.Should().ContainSingle();
                results[0].Result.IsHealthy.Should().BeFalse("a hanging check + cancellation must surface a failure");
                sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                    "cancellation must short-circuit the wait");
            }
        }

        [Fact]
        public async Task RunAllAsync_HangingCheck_DoesNotBlockHealthyChecks()
        {
            // Mix: one fast healthy check + one hanging check + caller cancels after
            // a short delay. We verify that the healthy result is captured and the
            // hanging one is reported as unhealthy/timeout.
            using (var cts = new CancellationTokenSource())
            {
                var sut = new HealthCheckRunner(new IStartupHealthCheck[]
                {
                    new StubCheck("fast", () => HealthCheckResult.Healthy()),
                    new HangingCheck("slow"),
                }, NullLogger());

                cts.CancelAfter(TimeSpan.FromMilliseconds(150));
                var results = await sut.RunAllAsync(cts.Token);

                results.Should().HaveCount(2);
                results.Single(r => r.Name == "fast").Result.IsHealthy.Should().BeTrue();
                results.Single(r => r.Name == "slow").Result.IsHealthy.Should().BeFalse();
            }
        }

        // ===== Result invariants =====

        [Fact]
        public void HealthCheckResult_Factories_BuildExpectedInstances()
        {
            var ok = HealthCheckResult.Healthy("yep");
            ok.IsHealthy.Should().BeTrue();
            ok.Message.Should().Be("yep");
            ok.Exception.Should().BeNull();

            var ex = new Exception("boom");
            var bad = HealthCheckResult.Unhealthy("oops", ex);
            bad.IsHealthy.Should().BeFalse();
            bad.Message.Should().Be("oops");
            bad.Exception.Should().Be(ex);
        }

        [Fact]
        public void HealthCheckResult_Healthy_NullMessage_FallsBackToOk()
        {
            HealthCheckResult.Healthy(null).Message.Should().Be("OK");
        }

        // ===== Stubs =====

        private sealed class StubCheck : IStartupHealthCheck
        {
            private readonly Func<HealthCheckResult> _fn;
            public StubCheck(string name, Func<HealthCheckResult> fn)
            {
                Name = name;
                _fn = fn;
            }
            public string Name { get; }
            public Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
                => Task.FromResult(_fn());
        }

        private sealed class DelayCheck : IStartupHealthCheck
        {
            private readonly TimeSpan _delay;
            public DelayCheck(string name, TimeSpan delay)
            {
                Name = name;
                _delay = delay;
            }
            public string Name { get; }
            public async Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
            {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
                return HealthCheckResult.Healthy();
            }
        }

        private sealed class ThrowingCheck : IStartupHealthCheck
        {
            private readonly Exception _ex;
            public ThrowingCheck(string name, Exception ex)
            {
                Name = name;
                _ex = ex;
            }
            public string Name { get; }
            public Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
                => throw _ex;
        }

        private sealed class HangingCheck : IStartupHealthCheck
        {
            public HangingCheck(string name) { Name = name; }
            public string Name { get; }
            public async Task<HealthCheckResult> CheckAsync(CancellationToken ct = default)
            {
                // Wait indefinitely until cancelled.
                var tcs = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
        }
    }
}
