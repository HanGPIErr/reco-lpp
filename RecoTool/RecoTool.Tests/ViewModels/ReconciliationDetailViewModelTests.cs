using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using RecoTool.Services.UI;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    public class ReconciliationDetailViewModelTests
    {
        private static (ReconciliationDetailViewModel vm, Mock<IReconciliationService> reco, Mock<IOfflineFirstService> ofs, Mock<IDialogService> dlg) BuildSut(
            string rowId = "R1")
        {
            var row = new ReconciliationViewData { ID = rowId, Reconciliation_Num = "RECO-" + rowId };
            var reco = new Mock<IReconciliationService>();
            var ofs = new Mock<IOfflineFirstService>();
            var dlg = new Mock<IDialogService>();
            ofs.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 1, USR_Category = "Action", USR_FieldName = "N/A" },
                new UserField { USR_ID = 5, USR_Category = "Action", USR_FieldName = "Match" },
                new UserField { USR_ID = 23, USR_Category = "KPI", USR_FieldName = "Not TFSC" },
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "PAID" }
            });
            reco.Setup(x => x.GetOrCreateReconciliationAsync(rowId))
                .ReturnsAsync(() => new Reconciliation { ID = rowId, Action = 5, KPI = 18 });
            reco.Setup(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()))
                .ReturnsAsync(true);

            var vm = new ReconciliationDetailViewModel(row, reco.Object, ofs.Object, dlg.Object);
            return (vm, reco, ofs, dlg);
        }

        [Fact]
        public void Ctor_NullArgs_Throws()
        {
            var row = new ReconciliationViewData { ID = "X" };
            var reco = Mock.Of<IReconciliationService>();
            var ofs = Mock.Of<IOfflineFirstService>();
            var dlg = Mock.Of<IDialogService>();
            ((Action)(() => new ReconciliationDetailViewModel(null, reco, ofs, dlg))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationDetailViewModel(row, null, ofs, dlg))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationDetailViewModel(row, reco, null, dlg))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationDetailViewModel(row, reco, ofs, null))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_PopulatesOptionsFromOfflineUserFields()
        {
            var (vm, _, _, _) = BuildSut();
            vm.ActionOptions.Should().HaveCount(2);
            vm.KPIOptions.Should().HaveCount(2);
        }

        [Fact]
        public async Task Load_HydratesFromExistingReconciliation()
        {
            var (vm, _, _, _) = BuildSut();
            await vm.LoadAsync();
            vm.SelectedActionId.Should().Be(5);
            vm.SelectedKPIId.Should().Be(18);
            vm.HasUnsavedChanges.Should().BeFalse();
        }

        [Fact]
        public async Task EditingProperty_MarksDirty()
        {
            var (vm, _, _, _) = BuildSut();
            await vm.LoadAsync();
            vm.HasUnsavedChanges.Should().BeFalse();
            vm.SelectedActionId = 1;
            vm.HasUnsavedChanges.Should().BeTrue();
        }

        [Fact]
        public async Task SaveCommand_CallsServiceAndClearsDirty()
        {
            var (vm, reco, _, _) = BuildSut();
            await vm.LoadAsync();
            vm.Comments = "edited";
            vm.HasUnsavedChanges.Should().BeTrue();

            await vm.SaveAsync();

            vm.HasUnsavedChanges.Should().BeFalse();
            reco.Verify(x => x.SaveReconciliationsAsync(
                It.Is<IEnumerable<Reconciliation>>(seq => seq.Count() == 1), It.IsAny<bool>()),
                Times.Once);
        }

        [Fact]
        public async Task NotInDwings_AppliesNAAndNotTFSCFlags()
        {
            var (vm, _, _, _) = BuildSut();
            await vm.LoadAsync();

            await vm.MarkNotInDwingsAsync();

            vm.SelectedActionId.Should().Be(1, "Action N/A");
            vm.SelectedKPIId.Should().Be(23, "KPI Not TFSC");
            vm.Comments.Should().Contain("NOT IN DWINGS");
        }

        [Fact]
        public async Task Unlink_RequiresConfirm()
        {
            var (vm, _, _, dlg) = BuildSut();
            await vm.LoadAsync();
            // Manually link
            vm.Reconciliation.DWINGS_InvoiceID = "BGI1";
            vm.HasUnsavedChanges = false;

            dlg.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

            await vm.UnlinkAsync();
            // Cancelled → link still there
            vm.Reconciliation.DWINGS_InvoiceID.Should().Be("BGI1");
        }

        [Fact]
        public async Task Unlink_OnConfirm_ClearsAndSaves()
        {
            var (vm, reco, _, dlg) = BuildSut();
            await vm.LoadAsync();
            vm.Reconciliation.DWINGS_InvoiceID = "BGI1";
            vm.Reconciliation.DWINGS_GuaranteeID = "G1";

            dlg.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

            await vm.UnlinkAsync();

            vm.Reconciliation.DWINGS_InvoiceID.Should().BeNull();
            vm.Reconciliation.DWINGS_GuaranteeID.Should().BeNull();
            reco.Verify(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()),
                Times.Once);
        }

        [Fact]
        public void CancelCommand_RaisesCancelEvent()
        {
            var (vm, _, _, _) = BuildSut();
            int hits = 0;
            vm.CancelRequested += (_, __) => hits++;
            vm.CancelCommand.Execute(null);
            hits.Should().Be(1);
        }
    }
}
