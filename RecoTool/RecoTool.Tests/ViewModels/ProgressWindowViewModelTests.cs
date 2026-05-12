using System;
using FluentAssertions;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    public class ProgressWindowViewModelTests
    {
        [Fact]
        public void Ctor_Defaults_AreSane()
        {
            var vm = new ProgressWindowViewModel();
            vm.Title.Should().Be("Operation in progress…");
            vm.IsIndeterminate.Should().BeTrue();
            vm.ProgressPercent.Should().Be(0);
            vm.CanCancel.Should().BeTrue();
            vm.CancelCommand.CanExecute(null).Should().BeTrue();
        }

        [Fact]
        public void Ctor_WithTitle_Stored()
        {
            var vm = new ProgressWindowViewModel("Importing AMBRE…");
            vm.Title.Should().Be("Importing AMBRE…");
        }

        [Fact]
        public void Ctor_NotCancellable_DisablesCommand()
        {
            var vm = new ProgressWindowViewModel(canCancel: false);
            vm.CancelCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void ProgressPercent_ClampsToZeroToHundred()
        {
            var vm = new ProgressWindowViewModel();
            vm.ProgressPercent = -10;
            vm.ProgressPercent.Should().Be(0);
            vm.ProgressPercent = 150;
            vm.ProgressPercent.Should().Be(100);
            vm.ProgressPercent = 42;
            vm.ProgressPercent.Should().Be(42);
        }

        [Fact]
        public void ProgressPercent_TransitionsFromIndeterminateToDeterminate()
        {
            var vm = new ProgressWindowViewModel();
            vm.IsIndeterminate.Should().BeTrue();
            vm.ProgressPercent = 25;
            vm.IsIndeterminate.Should().BeFalse();
        }

        [Fact]
        public void Report_UpdatesMessageAndPercent()
        {
            var vm = new ProgressWindowViewModel();
            vm.Report("Loading rows…", 33);
            vm.Message.Should().Be("Loading rows…");
            vm.ProgressPercent.Should().Be(33);
        }

        [Fact]
        public void Complete_RaisesCloseRequestedWithFlag()
        {
            var vm = new ProgressWindowViewModel();
            bool? captured = null;
            vm.CloseRequested += (_, s) => captured = s;
            vm.Complete(true);
            captured.Should().BeTrue();
        }

        [Fact]
        public void Cancel_FiresEventAndCancelsToken()
        {
            var vm = new ProgressWindowViewModel();
            int hits = 0;
            vm.CancelRequested += (_, _2) => hits++;

            vm.CancellationToken.IsCancellationRequested.Should().BeFalse();
            vm.CancelCommand.Execute(null);
            hits.Should().Be(1);
            vm.CancellationToken.IsCancellationRequested.Should().BeTrue();
        }
    }
}
