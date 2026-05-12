using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.UI;
using RecoTool.Tests.Domain.Repositories;
using RecoTool.Tests.Infrastructure.Time;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    /// <summary>
    /// Tests pour <see cref="HomePageViewModel"/> — squelette du dashboard.
    /// Couvre constructeur, refresh, calcul KPIs, commandes, événements.
    /// </summary>
    public class HomePageViewModelTests
    {
        private static (HomePageViewModel vm, Mock<IOfflineFirstService> ofs, Mock<IReconciliationService> reco, Mock<IDialogService> dlg) BuildSut(
            Country currentCountry = null)
        {
            var ofs = new Mock<IOfflineFirstService>();
            var reco = new Mock<IReconciliationService>();
            var dlg = new Mock<IDialogService>();
            ofs.Setup(x => x.CurrentCountry).Returns(currentCountry);
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            reco.Setup(x => x.GetAmbreDataAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new List<DataAmbre>());

            var vm = new HomePageViewModel(ofs.Object, reco.Object, dlg.Object, new FakeClock());
            return (vm, ofs, reco, dlg);
        }

        // ===== Ctor =====

        [Fact]
        public void Ctor_NullArgs_Throws()
        {
            var ofs = Mock.Of<IOfflineFirstService>();
            var reco = Mock.Of<IReconciliationService>();
            var dlg = Mock.Of<IDialogService>();
            var clk = new FakeClock();

            ((Action)(() => new HomePageViewModel(null, reco, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new HomePageViewModel(ofs, null, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new HomePageViewModel(ofs, reco, null, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new HomePageViewModel(ofs, reco, dlg, null))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_PrefillsCurrentCountryFromOFS()
        {
            var country = new Country { CNT_Id = "FR", CNT_Name = "France" };
            var (vm, _, _, _) = BuildSut(country);
            vm.CurrentCountry.Should().BeSameAs(country);
            vm.CurrentCountryId.Should().Be("FR");
            vm.CurrentCountryName.Should().Be("France");
        }

        [Fact]
        public void CurrentCountryName_FallsBackToIdWhenNameMissing()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "XX" });
            vm.CurrentCountryName.Should().Be("XX");
        }

        [Fact]
        public void CurrentCountryName_NoneWhenNoCountry()
        {
            var (vm, _, _, _) = BuildSut();
            vm.CurrentCountryName.Should().Be("(none)");
        }

        // ===== RefreshAsync =====

        [Fact]
        public async Task Refresh_PopulatesCountriesAndKpis()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P_FR", CNT_AmbreReceivable = "R_FR" };
            var (vm, ofs, reco, _) = BuildSut(country);

            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>
            {
                country,
                new Country { CNT_Id = "DE", CNT_Name = "Germany" }
            });
            reco.Setup(x => x.GetAmbreDataAsync("FR", false)).ReturnsAsync(new List<DataAmbre>
            {
                new DataAmbre { ID = "1", Account_ID = "P_FR", SignedAmount = 100m },
                new DataAmbre { ID = "2", Account_ID = "P_FR", SignedAmount = 50m },
                new DataAmbre { ID = "3", Account_ID = "R_FR", SignedAmount = -120m }
            });

            await ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);

            vm.AvailableCountries.Should().HaveCount(2);
            vm.TotalLiveCount.Should().Be(3);
            vm.PivotAccountsCount.Should().Be(2);
            vm.ReceivableAccountsCount.Should().Be(1);
            vm.TotalPivotAmount.Should().Be(150m);
            vm.TotalReceivableAmount.Should().Be(-120m);
            vm.LastUpdateTime.Should().NotBeNull();
            vm.IsLoading.Should().BeFalse();
        }

        [Fact]
        public async Task Refresh_OnException_ShowsErrorAndResetsState()
        {
            var (vm, ofs, _, dlg) = BuildSut(new Country { CNT_Id = "FR" });
            ofs.Setup(x => x.GetCountries()).ThrowsAsync(new InvalidOperationException("network down"));

            await ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);

            vm.IsLoading.Should().BeFalse();
            vm.StatusMessage.Should().Be("Error");
            dlg.Verify(x => x.ShowErrorAsync("Dashboard refresh", "network down"), Times.Once);
        }

        [Fact]
        public async Task Refresh_RaisesStartedAndCompleted()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "FR" });
            int started = 0, completed = 0;
            vm.RefreshStarted += (_, __) => started++;
            vm.RefreshCompleted += (_, __) => completed++;

            await ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);

            started.Should().Be(1);
            completed.Should().Be(1);
        }

        [Fact]
        public async Task Refresh_SetsAndResetsIsLoading()
        {
            var (vm, _, reco, _) = BuildSut(new Country { CNT_Id = "FR" });
            var tcs = new TaskCompletionSource<List<DataAmbre>>();
            reco.Setup(x => x.GetAmbreDataAsync(It.IsAny<string>(), It.IsAny<bool>())).Returns(tcs.Task);

            vm.IsLoading.Should().BeFalse();
            var t = ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);
            // Small delay to let LoadCountries finish before checking IsLoading
            await Task.Delay(50);
            vm.IsLoading.Should().BeTrue();

            tcs.SetResult(new List<DataAmbre>());
            await t;
            vm.IsLoading.Should().BeFalse();
        }

        [Fact]
        public async Task Refresh_NoCurrentCountry_LoadsCountriesButNoKpis()
        {
            var (vm, _, reco, _) = BuildSut();
            await ((AsyncRelayCommand)vm.RefreshCommand).ExecuteAsync(null);
            reco.Verify(x => x.GetAmbreDataAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        }

        // ===== LoadKpisAsync =====

        [Fact]
        public async Task LoadKpis_EmptyCountryId_NoOp()
        {
            var (vm, _, reco, _) = BuildSut();
            await vm.LoadKpisAsync(null);
            await vm.LoadKpisAsync("");
            reco.Verify(x => x.GetAmbreDataAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task LoadKpis_NullData_TolerateAndZero()
        {
            var (vm, _, reco, _) = BuildSut(new Country { CNT_Id = "FR" });
            reco.Setup(x => x.GetAmbreDataAsync("FR", false)).ReturnsAsync((List<DataAmbre>)null);

            await vm.LoadKpisAsync("FR");
            vm.TotalLiveCount.Should().Be(0);
            vm.PivotAccountsCount.Should().Be(0);
        }

        [Fact]
        public async Task LoadKpis_CaseInsensitiveAccountMatching()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "PIVOT_FR", CNT_AmbreReceivable = "RECV_FR" };
            var (vm, _, reco, _) = BuildSut(country);
            reco.Setup(x => x.GetAmbreDataAsync("FR", false)).ReturnsAsync(new List<DataAmbre>
            {
                new DataAmbre { Account_ID = "pivot_fr", SignedAmount = 100m },
                new DataAmbre { Account_ID = "RECV_FR", SignedAmount = -100m }
            });

            await vm.LoadKpisAsync("FR");
            vm.PivotAccountsCount.Should().Be(1);
            vm.ReceivableAccountsCount.Should().Be(1);
        }

        /// <summary>
        /// Repository-pattern migration POC: when an <see cref="RecoTool.Domain.Repositories.IDataAmbreRepository"/>
        /// is injected, <see cref="HomePageViewModel.LoadKpisAsync"/> must prefer it over the legacy
        /// <see cref="IReconciliationService.GetAmbreDataAsync"/> path. The mocked <c>reco</c> still
        /// returns an empty list, so any KPI counts reflecting seeded repo rows prove the repo was used.
        /// </summary>
        [Fact]
        public async Task LoadKpis_UsesAmbreRepositoryWhenInjected()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P_FR", CNT_AmbreReceivable = "R_FR" };
            var ofs = new Mock<IOfflineFirstService>();
            var reco = new Mock<IReconciliationService>();
            var dlg = new Mock<IDialogService>();
            ofs.Setup(x => x.CurrentCountry).Returns(country);
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            // Legacy path returns empty — if KPI counts are >0 below, the repo path was used.
            reco.Setup(x => x.GetAmbreDataAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new List<DataAmbre>());

            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                new DataAmbre { ID = "1", Account_ID = "P_FR", SignedAmount = 100m },
                new DataAmbre { ID = "2", Account_ID = "P_FR", SignedAmount = 25m },
                new DataAmbre { ID = "3", Account_ID = "R_FR", SignedAmount = -80m },
                new DataAmbre { ID = "4", Account_ID = "R_FR", SignedAmount = -40m },
                new DataAmbre { ID = "5", Account_ID = "R_FR", SignedAmount = -5m }
            });

            var vm = new HomePageViewModel(ofs.Object, reco.Object, dlg.Object, new FakeClock(), repo);

            await vm.LoadKpisAsync("FR");

            vm.TotalLiveCount.Should().Be(5);
            vm.PivotAccountsCount.Should().Be(2);
            vm.ReceivableAccountsCount.Should().Be(3);
            vm.TotalPivotAmount.Should().Be(125m);
            vm.TotalReceivableAmount.Should().Be(-125m);

            // Confirm the legacy path was NOT consulted — repo took priority.
            reco.Verify(x => x.GetAmbreDataAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        }

        // ===== Commands navigation =====

        [Fact]
        public void ImportAmbreCommand_RaisesEventWhenCountryAvailable()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "FR" });
            int hits = 0;
            vm.ImportRequested += (_, __) => hits++;
            vm.ImportAmbreCommand.Execute(null);
            hits.Should().Be(1);
        }

        [Fact]
        public void ImportAmbreCommand_DisabledWhenNoCountry()
        {
            var (vm, _, _, _) = BuildSut();
            vm.ImportAmbreCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void OpenReportsCommand_RaisesEvent()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "FR" });
            int hits = 0;
            vm.ReportsRequested += (_, __) => hits++;
            vm.OpenReportsCommand.Execute(null);
            hits.Should().Be(1);
        }

        [Fact]
        public void ExportDailyKpiCommand_RaisesEvent()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "FR" });
            int hits = 0;
            vm.ExportKpiRequested += (_, __) => hits++;
            vm.ExportDailyKpiCommand.Execute(null);
            hits.Should().Be(1);
        }

        [Fact]
        public void OpenTodoCardCommand_PassesParameter()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "FR" });
            TodoCardCount captured = null;
            vm.TodoCardOpened += (_, c) => captured = c;

            var card = new TodoCardCount { Title = "Missing", Count = 5, FilterPreset = "filter1" };
            vm.OpenTodoCardCommand.Execute(card);
            captured.Should().BeSameAs(card);
        }

        [Fact]
        public void OpenTodoCardCommand_DisabledWithoutCardParam()
        {
            var (vm, _, _, _) = BuildSut(new Country { CNT_Id = "FR" });
            vm.OpenTodoCardCommand.CanExecute(null).Should().BeFalse();
            vm.OpenTodoCardCommand.CanExecute("not a card").Should().BeFalse();
            vm.OpenTodoCardCommand.CanExecute(new TodoCardCount()).Should().BeTrue();
        }

        // ===== INPC =====

        [Fact]
        public void SettingProperty_RaisesPropertyChanged()
        {
            var (vm, _, _, _) = BuildSut();
            var notified = new List<string>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.MissingInvoicesCount = 7;
            vm.IsLoading = true;
            vm.MatchedPercentage = 75.5;

            notified.Should().Contain(nameof(HomePageViewModel.MissingInvoicesCount))
                    .And.Contain(nameof(HomePageViewModel.IsLoading))
                    .And.Contain(nameof(HomePageViewModel.MatchedPercentage));
        }

        [Fact]
        public void SettingCurrentCountry_AlsoRaisesNamedNotifications()
        {
            var (vm, _, _, _) = BuildSut();
            var notified = new List<string>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.CurrentCountry = new Country { CNT_Id = "DE", CNT_Name = "Germany" };
            notified.Should().Contain(nameof(HomePageViewModel.CurrentCountry))
                    .And.Contain(nameof(HomePageViewModel.CurrentCountryName));
        }
    }
}
