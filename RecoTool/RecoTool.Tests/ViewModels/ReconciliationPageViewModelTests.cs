using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.UI;
using RecoTool.Tests.Infrastructure.Time;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    /// <summary>
    /// Tests pour <see cref="ReconciliationPageViewModel"/>.
    /// Désormais entièrement mockable grâce à <see cref="IUserFilterService"/>
    /// et <see cref="IUserTodoListService"/> — plus de connection strings fakes
    /// qui faisaient échouer les opérations DB en silence.
    /// </summary>
    public class ReconciliationPageViewModelTests
    {
        private static (
            ReconciliationPageViewModel vm,
            Mock<IOfflineFirstService> ofs,
            Mock<IReconciliationService> reco,
            Mock<IUserFilterService> filters,
            Mock<IUserTodoListService> todos,
            Mock<IDialogService> dlg)
        BuildSut()
        {
            var ofs = new Mock<IOfflineFirstService>();
            var reco = new Mock<IReconciliationService>();
            var filters = new Mock<IUserFilterService>();
            var todos = new Mock<IUserTodoListService>();
            var dlg = new Mock<IDialogService>();

            ofs.Setup(x => x.CurrentCountryId).Returns("FR");
            filters.Setup(x => x.ListUserFilterNames()).Returns(new List<string>());
            todos.Setup(x => x.ListAsync(It.IsAny<string>())).ReturnsAsync(new List<TodoListItem>());

            var vm = new ReconciliationPageViewModel(
                ofs.Object, reco.Object, filters.Object, todos.Object, dlg.Object, new FakeClock());
            return (vm, ofs, reco, filters, todos, dlg);
        }

        // ===== Ctor =====

        [Fact]
        public void Ctor_NullArgs_Throws()
        {
            var ofs = Mock.Of<IOfflineFirstService>();
            var reco = Mock.Of<IReconciliationService>();
            var filters = Mock.Of<IUserFilterService>();
            var todos = Mock.Of<IUserTodoListService>();
            var dlg = Mock.Of<IDialogService>();
            var clk = new FakeClock();

            ((Action)(() => new ReconciliationPageViewModel(null, reco, filters, todos, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationPageViewModel(ofs, null, filters, todos, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationPageViewModel(ofs, reco, null, todos, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationPageViewModel(ofs, reco, filters, null, dlg, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationPageViewModel(ofs, reco, filters, todos, null, clk))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ReconciliationPageViewModel(ofs, reco, filters, todos, dlg, null))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_InitializesObservableCollections()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.SavedFilterNames.Should().NotBeNull();
            vm.TodoItems.Should().NotBeNull();
            vm.Accounts.Should().Equal("All", "Pivot", "Receivable");
            vm.Statuses.Should().Equal("Live", "Archived", "All");
            vm.ViewTypes.Should().Equal("Both", "Pivot only", "Receivable only");
        }

        // ===== State =====

        [Fact]
        public void CanInteract_FalseWhenLoading()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.CanInteract.Should().BeTrue();
            vm.IsLoading = true;
            vm.CanInteract.Should().BeFalse();
        }

        [Fact]
        public void CanInteract_FalseWhenGlobalLockActive()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.IsGlobalLockActive = true;
            vm.CanInteract.Should().BeFalse();
        }

        // ===== Selected Todo =====

        [Fact]
        public void SettingSelectedTodoItem_UpdatesEditTodoNameAndIsTodoSelected()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.IsTodoSelected.Should().BeFalse();
            var item = new TodoListItem { TDL_id = 1, TDL_Name = "Test" };
            vm.SelectedTodoItem = item;
            vm.IsTodoSelected.Should().BeTrue();
            vm.EditTodoName.Should().Be("Test");
        }

        [Fact]
        public void ClearingSelectedTodoItem_ClearsEditName()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.SelectedTodoItem = new TodoListItem { TDL_Name = "X" };
            vm.SelectedTodoItem = null;
            vm.EditTodoName.Should().BeNull();
            vm.IsTodoSelected.Should().BeFalse();
        }

        // ===== Commands =====

        [Fact]
        public void AddViewCommand_RaisesEvent()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            int hits = 0;
            vm.AddViewRequested += (_, __) => hits++;
            vm.AddViewCommand.Execute(null);
            hits.Should().Be(1);
        }

        [Fact]
        public void AddViewCommand_DisabledWhenLoading()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.IsLoading = true;
            vm.AddViewCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void OpenInvoiceFinderCommand_RaisesEvent()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            int hits = 0;
            vm.InvoiceFinderRequested += (_, __) => hits++;
            vm.OpenInvoiceFinderCommand.Execute(null);
            hits.Should().Be(1);
        }

        [Fact]
        public void SaveTodoCommand_DisabledWhenNameEmpty()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.SaveTodoCommand.CanExecute(null).Should().BeFalse();
            vm.EditTodoName = "x";
            vm.SaveTodoCommand.CanExecute(null).Should().BeTrue();
        }

        [Fact]
        public void DeleteTodoCommand_DisabledForUnsavedItem()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            vm.DeleteTodoCommand.CanExecute(null).Should().BeFalse();
            vm.SelectedTodoItem = new TodoListItem { TDL_id = 0 };
            vm.DeleteTodoCommand.CanExecute(null).Should().BeFalse();
            vm.SelectedTodoItem = new TodoListItem { TDL_id = 5 };
            vm.DeleteTodoCommand.CanExecute(null).Should().BeTrue();
        }

        // ===== Refresh =====

        [Fact]
        public async Task Refresh_RaisesStartedAndCompleted_AndResetsLoading()
        {
            var (vm, _, _, _, _, _) = BuildSut();
            int started = 0, completed = 0;
            vm.RefreshStarted += (_, __) => started++;
            vm.RefreshCompleted += (_, __) => completed++;

            await vm.RefreshAsync();

            started.Should().Be(1);
            completed.Should().Be(1);
            vm.IsLoading.Should().BeFalse();
        }

        [Fact]
        public async Task Refresh_PopulatesSavedFilterNamesAndTodoItems()
        {
            var (vm, _, _, filters, todos, _) = BuildSut();
            filters.Setup(x => x.ListUserFilterNames()).Returns(new List<string> { "F1", "F2" });
            todos.Setup(x => x.ListAsync("FR")).ReturnsAsync(new List<TodoListItem>
            {
                new TodoListItem { TDL_id = 1, TDL_Name = "B" },
                new TodoListItem { TDL_id = 2, TDL_Name = "A" }
            });

            await vm.RefreshAsync();

            vm.SavedFilterNames.Should().Equal("F1", "F2");
            vm.TodoItems.Should().HaveCount(2);
            vm.TodoItems[0].TDL_Name.Should().Be("A", "tri par TDL_Name");
            vm.TodoItems[1].TDL_Name.Should().Be("B");
        }

        // ===== Save / Delete TodoList =====

        [Fact]
        public async Task SaveTodoCommand_UpsertsAndReloads()
        {
            var (vm, _, _, _, todos, _) = BuildSut();
            todos.Setup(x => x.UpsertAsync(It.IsAny<TodoListItem>())).ReturnsAsync(42);

            vm.EditTodoName = "New Todo";
            await ((AsyncRelayCommand)vm.SaveTodoCommand).ExecuteAsync(null);

            todos.Verify(x => x.UpsertAsync(It.Is<TodoListItem>(t => t.TDL_Name == "New Todo")), Times.Once);
            todos.Verify(x => x.ListAsync(It.IsAny<string>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task DeleteTodoCommand_OnConfirm_DeletesAndReloads()
        {
            var (vm, _, _, _, todos, dlg) = BuildSut();
            dlg.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            todos.Setup(x => x.DeleteAsync(7)).ReturnsAsync(true);

            vm.SelectedTodoItem = new TodoListItem { TDL_id = 7, TDL_Name = "X" };
            await ((AsyncRelayCommand)vm.DeleteTodoCommand).ExecuteAsync(null);

            todos.Verify(x => x.DeleteAsync(7), Times.Once);
            todos.Verify(x => x.ListAsync(It.IsAny<string>()), Times.AtLeastOnce);
            vm.SelectedTodoItem.Should().BeNull();
        }

        [Fact]
        public async Task DeleteTodoCommand_OnCancel_DoesNotDelete()
        {
            var (vm, _, _, _, todos, dlg) = BuildSut();
            dlg.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

            vm.SelectedTodoItem = new TodoListItem { TDL_id = 7 };
            await ((AsyncRelayCommand)vm.DeleteTodoCommand).ExecuteAsync(null);

            todos.Verify(x => x.DeleteAsync(It.IsAny<int>()), Times.Never);
        }
    }
}
