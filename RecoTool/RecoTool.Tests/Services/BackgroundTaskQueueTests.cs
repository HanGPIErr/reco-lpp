using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests pour <see cref="BackgroundTaskQueue"/> (singleton sérialisant des Func&lt;Task&gt;).
    /// Couvre la garde sur l'argument null, l'exécution effective, la sérialisation
    /// (FIFO, exécution non concurrente) et la résilience face à une tâche en exception.
    /// </summary>
    public class BackgroundTaskQueueTests
    {
        [Fact]
        public void Enqueue_Null_Throws()
        {
            Action act = () => BackgroundTaskQueue.Instance.Enqueue(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public async Task Enqueue_RunsWorkItem()
        {
            var tcs = new TaskCompletionSource<bool>();
            BackgroundTaskQueue.Instance.Enqueue(() =>
            {
                tcs.TrySetResult(true);
                return Task.CompletedTask;
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            completed.Should().BeSameAs(tcs.Task, "le worker doit exécuter le delegate sous 2s");
            tcs.Task.Result.Should().BeTrue();
        }

        [Fact]
        public async Task Enqueue_PreservesFifoOrder_Serially()
        {
            var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var done = new TaskCompletionSource<bool>();

            BackgroundTaskQueue.Instance.Enqueue(async () => { await Task.Delay(20); order.Enqueue(1); });
            BackgroundTaskQueue.Instance.Enqueue(async () => { await Task.Delay(20); order.Enqueue(2); });
            BackgroundTaskQueue.Instance.Enqueue(() =>
            {
                order.Enqueue(3);
                done.TrySetResult(true);
                return Task.CompletedTask;
            });

            var completed = await Task.WhenAny(done.Task, Task.Delay(3000));
            completed.Should().BeSameAs(done.Task);

            order.Should().Equal(new[] { 1, 2, 3 });
        }

        [Fact]
        public async Task Enqueue_TaskThrowing_DoesNotKillTheWorker()
        {
            // Une tâche en erreur ne doit pas empêcher la suivante d'exécuter.
            BackgroundTaskQueue.Instance.Enqueue(() =>
            {
                throw new InvalidOperationException("intentional");
            });

            var tcs = new TaskCompletionSource<bool>();
            BackgroundTaskQueue.Instance.Enqueue(() =>
            {
                tcs.TrySetResult(true);
                return Task.CompletedTask;
            });

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            completed.Should().BeSameAs(tcs.Task);
            tcs.Task.Result.Should().BeTrue();
        }

        [Fact]
        public void Instance_IsSingleton()
        {
            var a = BackgroundTaskQueue.Instance;
            var b = BackgroundTaskQueue.Instance;
            a.Should().BeSameAs(b);
        }
    }
}
