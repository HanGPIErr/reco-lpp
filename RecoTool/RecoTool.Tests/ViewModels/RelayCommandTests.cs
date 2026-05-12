using System;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    /// <summary>
    /// Tests for <see cref="RelayCommand"/> and <see cref="AsyncRelayCommand"/>.
    /// </summary>
    public class RelayCommandTests
    {
        [Fact]
        public void RelayCommand_NullExecute_Throws()
        {
            Action a = () => new RelayCommand((Action)null);
            a.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void RelayCommand_Execute_InvokesAction()
        {
            int hits = 0;
            var cmd = new RelayCommand(() => hits++);
            cmd.Execute(null);
            hits.Should().Be(1);
        }

        [Fact]
        public void RelayCommand_NoCanExecute_AlwaysCanExecute()
        {
            var cmd = new RelayCommand(() => { });
            cmd.CanExecute(null).Should().BeTrue();
        }

        [Fact]
        public void RelayCommand_CanExecuteFalse_BlocksExecuteFromUI()
        {
            // Note : RelayCommand.Execute n'inspecte pas CanExecute (par contrat WPF).
            // Le test vérifie uniquement la sortie de CanExecute.
            var cmd = new RelayCommand(() => { }, () => false);
            cmd.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void RelayCommand_WithParameter_PassesIt()
        {
            object received = null;
            var cmd = new RelayCommand(p => received = p);
            cmd.Execute("hello");
            received.Should().Be("hello");
        }

        // ===== AsyncRelayCommand =====

        [Fact]
        public async Task AsyncRelayCommand_ExecuteAsync_InvokesFuncAndCompletes()
        {
            int hits = 0;
            var cmd = new AsyncRelayCommand(async () => { await Task.Delay(10); hits++; });
            await cmd.ExecuteAsync(null);
            hits.Should().Be(1);
        }

        [Fact]
        public async Task AsyncRelayCommand_WhileExecuting_CanExecuteFalse()
        {
            var tcs = new TaskCompletionSource<bool>();
            var cmd = new AsyncRelayCommand(async () => await tcs.Task);

            cmd.CanExecute(null).Should().BeTrue();
            var pending = cmd.ExecuteAsync(null);
            cmd.CanExecute(null).Should().BeFalse("la commande est en cours d'exécution");
            cmd.IsExecuting.Should().BeTrue();

            tcs.SetResult(true);
            await pending;

            cmd.CanExecute(null).Should().BeTrue("après complétion, peut être ré-exécutée");
            cmd.IsExecuting.Should().BeFalse();
        }

        [Fact]
        public async Task AsyncRelayCommand_DoubleExecute_SecondIsNoOp()
        {
            int hits = 0;
            var tcs = new TaskCompletionSource<bool>();
            var cmd = new AsyncRelayCommand(async () => { await tcs.Task; hits++; });

            var t1 = cmd.ExecuteAsync(null);
            var t2 = cmd.ExecuteAsync(null);    // bloqué — déjà en cours

            tcs.SetResult(true);
            await Task.WhenAll(t1, t2);

            hits.Should().Be(1, "le second appel doit être no-op (bouton désactivé)");
        }

        [Fact]
        public async Task AsyncRelayCommand_ResetsIsExecuting_OnException()
        {
            var cmd = new AsyncRelayCommand(() => throw new InvalidOperationException("boom"));
            try { await cmd.ExecuteAsync(null); } catch { /* expected */ }
            cmd.IsExecuting.Should().BeFalse();
            cmd.CanExecute(null).Should().BeTrue();
        }
    }
}
