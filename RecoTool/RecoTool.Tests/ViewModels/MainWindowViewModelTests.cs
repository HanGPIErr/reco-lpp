using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Infrastructure.Time;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.UI;
using RecoTool.Tests.Infrastructure.Time;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    /// <summary>
    /// Tests for <see cref="MainWindowViewModel"/>. Demonstrates how a WPF VM
    /// can be fully unit-tested without a dispatcher or any Window instance.
    /// </summary>
    public class MainWindowViewModelTests
    {
        private static (MainWindowViewModel vm, Mock<IOfflineFirstService> ofs, Mock<IDialogService> dialog, FakeClock clock) BuildSut(
            DateTime? clockNow = null)
        {
            var ofs = new Mock<IOfflineFirstService>();
            var dlg = new Mock<IDialogService>();
            var clock = clockNow.HasValue ? new FakeClock(clockNow.Value) : new FakeClock();
            var vm = new MainWindowViewModel(ofs.Object, dlg.Object, clock);
            return (vm, ofs, dlg, clock);
        }

        // ===== Ctor =====

        [Fact]
        public void Ctor_NullDeps_Throws()
        {
            var ofs = Mock.Of<IOfflineFirstService>();
            var dlg = Mock.Of<IDialogService>();
            var clk = new FakeClock();

            ((Action)(() => new MainWindowViewModel(null, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new MainWindowViewModel(ofs, null, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new MainWindowViewModel(ofs, dlg, null))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_PrefillsCurrentCountryFromOFS()
        {
            var ofs = new Mock<IOfflineFirstService>();
            var country = new Country { CNT_Id = "FR", CNT_Name = "France" };
            ofs.Setup(x => x.CurrentCountry).Returns(country);

            var vm = new MainWindowViewModel(ofs.Object, Mock.Of<IDialogService>(), new FakeClock());
            vm.CurrentCountry.Should().BeSameAs(country);
            vm.Title.Should().Contain("France");
        }

        [Fact]
        public void Title_NoCountry_UsesAppName()
        {
            var (vm, _, _, _) = BuildSut();
            vm.Title.Should().Be("RecoTool");
        }

        [Fact]
        public void Title_FallsBackToCountryId_WhenNameMissing()
        {
            var (vm, _, _, _) = BuildSut();
            vm.CurrentCountry = new Country { CNT_Id = "XX" };
            vm.Title.Should().Contain("XX");
        }

        // ===== LoadCountriesCommand =====

        [Fact]
        public async Task LoadCountries_PopulatesObservableCollectionSorted()
        {
            var (vm, ofs, _, _) = BuildSut();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>
            {
                new Country { CNT_Id = "DE", CNT_Name = "Germany" },
                new Country { CNT_Id = "FR", CNT_Name = "France" },
                new Country { CNT_Id = "AT", CNT_Name = "Austria" }
            });

            await ((AsyncRelayCommand)vm.LoadCountriesCommand).ExecuteAsync(null);

            vm.Countries.Should().HaveCount(3);
            vm.Countries[0].CNT_Id.Should().Be("AT", "Austria triée en premier");
            vm.Countries[2].CNT_Id.Should().Be("DE");
        }

        [Fact]
        public async Task LoadCountries_SetsAndResetsIsBusy()
        {
            var (vm, ofs, _, _) = BuildSut();
            var tcs = new TaskCompletionSource<List<Country>>();
            ofs.Setup(x => x.GetCountries()).Returns(tcs.Task);

            vm.IsBusy.Should().BeFalse();
            var pending = ((AsyncRelayCommand)vm.LoadCountriesCommand).ExecuteAsync(null);
            vm.IsBusy.Should().BeTrue();

            tcs.SetResult(new List<Country>());
            await pending;

            vm.IsBusy.Should().BeFalse();
        }

        [Fact]
        public async Task LoadCountries_OnException_ShowsErrorAndResetsIsBusy()
        {
            var (vm, ofs, dlg, _) = BuildSut();
            ofs.Setup(x => x.GetCountries()).ThrowsAsync(new InvalidOperationException("db down"));

            await ((AsyncRelayCommand)vm.LoadCountriesCommand).ExecuteAsync(null);

            vm.IsBusy.Should().BeFalse();
            dlg.Verify(x => x.ShowErrorAsync("Loading countries", "db down"), Times.Once);
        }

        [Fact]
        public async Task LoadCountries_NullList_TolerateAndPopulateEmpty()
        {
            var (vm, ofs, _, _) = BuildSut();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync((List<Country>)null);

            await ((AsyncRelayCommand)vm.LoadCountriesCommand).ExecuteAsync(null);
            vm.Countries.Should().BeEmpty();
        }

        // ===== SwitchCountryCommand =====

        [Fact]
        public async Task SwitchCountry_UpdatesCurrentAndStampsLastSync()
        {
            var clockNow = new DateTime(2024, 5, 1, 9, 0, 0);
            var (vm, _, _, clock) = BuildSut(clockNow);
            var target = new Country { CNT_Id = "FR" };

            await ((AsyncRelayCommand)vm.SwitchCountryCommand).ExecuteAsync(target);

            vm.CurrentCountry.Should().BeSameAs(target);
            vm.LastSyncAt.Should().Be(clockNow);
            vm.SyncStatusText.Should().Be("Idle");
        }

        [Fact]
        public async Task SwitchCountry_SameCountry_NoOp()
        {
            var (vm, _, _, _) = BuildSut();
            var country = new Country { CNT_Id = "FR" };
            vm.CurrentCountry = country;
            vm.LastSyncAt = null;

            await ((AsyncRelayCommand)vm.SwitchCountryCommand).ExecuteAsync(country);

            vm.LastSyncAt.Should().BeNull("aucune opération si la cible est identique");
        }

        [Fact]
        public async Task SwitchCountry_NullParam_NoOp()
        {
            var (vm, _, _, _) = BuildSut();
            await ((AsyncRelayCommand)vm.SwitchCountryCommand).ExecuteAsync(null);
            vm.CurrentCountry.Should().BeNull();
        }

        // ===== RefreshCommand =====

        [Fact]
        public async Task Refresh_NoCountry_NoOp()
        {
            var (vm, _, _, _) = BuildSut();
            await ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);
            vm.LastSyncAt.Should().BeNull();
        }

        [Fact]
        public async Task Refresh_SetsAndResetsIsSyncing_StampsLastSync()
        {
            var clockNow = new DateTime(2024, 5, 1, 12, 0, 0);
            var (vm, _, _, clock) = BuildSut(clockNow);
            vm.CurrentCountry = new Country { CNT_Id = "FR" };

            await ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);

            vm.IsSyncing.Should().BeFalse();
            vm.LastSyncAt.Should().Be(clockNow);
            vm.SyncStatusText.Should().Be("Idle");
        }

        // ===== Navigation events =====

        [Fact]
        public void ImportCommand_RaisesImportRequested()
        {
            var (vm, _, _, _) = BuildSut();
            vm.CurrentCountry = new Country { CNT_Id = "FR" };

            int hits = 0;
            vm.ImportRequested += (_, __) => hits++;
            vm.ImportAmbreCommand.Execute(null);

            hits.Should().Be(1);
        }

        [Fact]
        public void OpenReconciliationCommand_RaisesEvent()
        {
            var (vm, _, _, _) = BuildSut();
            vm.CurrentCountry = new Country { CNT_Id = "FR" };

            int hits = 0;
            vm.OpenReconciliationRequested += (_, __) => hits++;
            vm.OpenReconciliationCommand.Execute(null);

            hits.Should().Be(1);
        }

        [Fact]
        public void ExitCommand_RaisesEvent()
        {
            var (vm, _, _, _) = BuildSut();
            int hits = 0;
            vm.ExitRequested += (_, __) => hits++;
            vm.ExitCommand.Execute(null);
            hits.Should().Be(1);
        }

        // ===== INotifyPropertyChanged =====

        [Fact]
        public void SettingProperty_RaisesPropertyChanged()
        {
            var (vm, _, _, _) = BuildSut();
            var notified = new List<string>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.IsBusy = true;
            vm.SyncStatusText = "Working";

            notified.Should().Contain(nameof(MainWindowViewModel.IsBusy))
                    .And.Contain(nameof(MainWindowViewModel.SyncStatusText));
        }

        [Fact]
        public void SettingCurrentCountry_AlsoRaisesTitleNotification()
        {
            var (vm, _, _, _) = BuildSut();
            var notified = new List<string>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.CurrentCountry = new Country { CNT_Id = "FR", CNT_Name = "France" };
            notified.Should().Contain(nameof(MainWindowViewModel.CurrentCountry))
                    .And.Contain(nameof(MainWindowViewModel.Title));
        }
    }
}
