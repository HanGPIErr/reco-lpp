using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Infrastructure.DataAccess;
using Xunit;

namespace RecoTool.Tests.Infrastructure.DataAccess
{
    /// <summary>
    /// Tests for <see cref="OleDbAsyncExecutor"/>.
    ///
    /// <para>
    /// <b>Coverage limitation.</b> <see cref="System.Data.OleDb.OleDbConnection"/> is sealed and cannot be
    /// mocked, and we cannot reliably open a real Access connection in CI. The tests therefore focus on the
    /// <see cref="OleDbAsyncExecutor.RunAsync(System.Action{System.Data.OleDb.OleDbConnection}, System.Data.OleDb.OleDbConnection, System.Threading.CancellationToken)"/>
    /// overload using a <c>null</c> connection — the delegate is not required to use it. This exercises the
    /// thread-pool dispatch, cancellation, exception propagation, and return-value plumbing. The
    /// <c>RunWithConnectionAsync</c> / <c>RunInTransactionAsync</c> overloads share the same dispatch path and
    /// are best covered by integration tests against a real Access database.
    /// </para>
    /// </summary>
    public class OleDbAsyncExecutorTests
    {
        // ── RunAsync (Action) ────────────────────────────────────────────────

        [Fact]
        public async Task RunAsync_Action_NullDelegate_Throws()
        {
            Func<Task> act = () => OleDbAsyncExecutor.RunAsync((Action<System.Data.OleDb.OleDbConnection>)null, null);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task RunAsync_Action_RunsOnThreadPoolWorker()
        {
            // Assert the SEMANTIC contract — work runs on a thread-pool worker — not the
            // thread-id distinction, which is unreliable when the caller itself is already
            // a thread-pool thread (as is the case under xUnit) and the pool happens to
            // schedule the continuation on the same thread.
            bool workerIsThreadPool = false;
            int workerThreadId = -1;

            await OleDbAsyncExecutor.RunAsync(_ =>
            {
                workerIsThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                workerThreadId = Thread.CurrentThread.ManagedThreadId;
            }, null);

            workerThreadId.Should().NotBe(-1, "the delegate must have run");
            workerIsThreadPool.Should().BeTrue("the work must be offloaded to the thread pool");
        }

        [Fact]
        public async Task RunAsync_Action_PropagatesException()
        {
            Func<Task> act = () => OleDbAsyncExecutor.RunAsync(
                _ => throw new InvalidOperationException("boom"),
                null);

            // Awaiting unwraps AggregateException to the inner exception.
            var ex = await act.Should().ThrowAsync<InvalidOperationException>();
            ex.Which.Message.Should().Be("boom");
        }

        [Fact]
        public async Task RunAsync_Action_PreCancelledToken_Throws()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            bool ran = false;

            Func<Task> act = () => OleDbAsyncExecutor.RunAsync(_ => ran = true, null, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            ran.Should().BeFalse("a cancelled token must prevent the delegate from running");
        }

        // ── RunAsync<T> (Func) ───────────────────────────────────────────────

        [Fact]
        public async Task RunAsyncGeneric_NullDelegate_Throws()
        {
            Func<Task> act = () => OleDbAsyncExecutor.RunAsync<int>((Func<System.Data.OleDb.OleDbConnection, int>)null, null);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task RunAsyncGeneric_RunsOnThreadPoolWorker()
        {
            // Same rationale as the Action overload — assert thread-pool semantics, not
            // strict thread-id distinction.
            var workerIsThreadPool = await OleDbAsyncExecutor.RunAsync(
                _ => Thread.CurrentThread.IsThreadPoolThread, null);

            workerIsThreadPool.Should().BeTrue("the work must run on a thread pool worker");
        }

        [Fact]
        public async Task RunAsyncGeneric_PropagatesReturnValue()
        {
            var result = await OleDbAsyncExecutor.RunAsync(_ => 42, null);
            result.Should().Be(42);
        }

        [Fact]
        public async Task RunAsyncGeneric_PropagatesReferenceReturnValue()
        {
            var payload = new object();
            var result = await OleDbAsyncExecutor.RunAsync(_ => payload, null);
            result.Should().BeSameAs(payload);
        }

        [Fact]
        public async Task RunAsyncGeneric_PropagatesException()
        {
            Func<Task<string>> act = () => OleDbAsyncExecutor.RunAsync<string>(
                _ => throw new InvalidOperationException("nope"),
                null);

            var ex = await act.Should().ThrowAsync<InvalidOperationException>();
            ex.Which.Message.Should().Be("nope");
        }

        [Fact]
        public async Task RunAsyncGeneric_PreCancelledToken_Throws()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();
            bool ran = false;

            Func<Task<int>> act = () => OleDbAsyncExecutor.RunAsync<int>(_ => { ran = true; return 1; }, null, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            ran.Should().BeFalse();
        }

        // ── RunWithConnectionAsync / RunInTransactionAsync (argument guards) ─

        [Fact]
        public async Task RunWithConnectionAsync_NullConnectionString_Throws()
        {
            Func<Task<int>> act = () => OleDbAsyncExecutor.RunWithConnectionAsync<int>(null, _ => 0);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task RunWithConnectionAsync_NullDelegate_Throws()
        {
            Func<Task<int>> act = () => OleDbAsyncExecutor.RunWithConnectionAsync<int>("Provider=fake;", null);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task RunInTransactionAsync_NullConnectionString_Throws()
        {
            Func<Task<int>> act = () => OleDbAsyncExecutor.RunInTransactionAsync<int>(null, (_, __) => 0);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task RunInTransactionAsync_NullDelegate_Throws()
        {
            Func<Task<int>> act = () => OleDbAsyncExecutor.RunInTransactionAsync<int>("Provider=fake;", null);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }
    }
}
