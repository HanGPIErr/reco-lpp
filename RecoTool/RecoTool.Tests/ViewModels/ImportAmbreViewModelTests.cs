using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.UI;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    /// <summary>
    /// Tests pour <see cref="ImportAmbreViewModel"/>. Désormais entièrement mockable
    /// via <see cref="IAmbreImportService"/> — plus de
    /// <see cref="System.Runtime.Serialization.FormatterServices.GetUninitializedObject"/>.
    /// </summary>
    public class ImportAmbreViewModelTests
    {
        private static (
            ImportAmbreViewModel vm,
            Mock<IAmbreImportService> import,
            Mock<IOfflineFirstService> ofs,
            Mock<IDialogService> dlg)
        BuildSut(string countryId = "FR")
        {
            var import = new Mock<IAmbreImportService>();
            var ofs = new Mock<IOfflineFirstService>();
            var dlg = new Mock<IDialogService>();
            ofs.Setup(x => x.CurrentCountryId).Returns(countryId);
            var vm = new ImportAmbreViewModel(import.Object, ofs.Object, dlg.Object);
            return (vm, import, ofs, dlg);
        }

        // ===== Ctor =====

        [Fact]
        public void Ctor_NullArgs_Throws()
        {
            var import = Mock.Of<IAmbreImportService>();
            var ofs = Mock.Of<IOfflineFirstService>();
            var dlg = Mock.Of<IDialogService>();

            ((Action)(() => new ImportAmbreViewModel(null, ofs, dlg))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ImportAmbreViewModel(import, null, dlg))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new ImportAmbreViewModel(import, ofs, null))).Should().Throw<ArgumentNullException>();
        }

        // ===== File selection =====

        [Fact]
        public void HasFile_FalseWhenNoSelection_TrueAfterFile1()
        {
            var (vm, _, _, _) = BuildSut();
            vm.HasFile.Should().BeFalse();
            vm.SelectedFilesDisplay.Should().Be("(none)");

            vm.SelectedFilePath1 = @"C:\path\book.xlsx";
            vm.HasFile.Should().BeTrue();
            vm.IsMultiFile.Should().BeFalse();
            vm.SelectedFilesDisplay.Should().Contain("book.xlsx");
        }

        [Fact]
        public void IsMultiFile_RequiresBothPaths()
        {
            var (vm, _, _, _) = BuildSut();
            vm.SelectedFilePath1 = @"C:\a.xlsx";
            vm.IsMultiFile.Should().BeFalse();
            vm.SelectedFilePath2 = @"C:\b.xlsx";
            vm.IsMultiFile.Should().BeTrue();
            vm.SelectedFilesDisplay.Should().Contain("a.xlsx").And.Contain("b.xlsx");
        }

        [Fact]
        public async Task BrowseFile1_OnCancel_DoesNotChangeSelection()
        {
            var (vm, _, _, dlg) = BuildSut();
            dlg.Setup(x => x.OpenFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string)null);

            await ((AsyncRelayCommand)vm.BrowseFile1Command).ExecuteAsync(null);
            vm.SelectedFilePath1.Should().BeNull();
        }

        [Fact]
        public async Task BrowseFile1_OnSelect_StoresPath()
        {
            var (vm, _, _, dlg) = BuildSut();
            dlg.Setup(x => x.OpenFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(@"C:\selected.xlsx");

            await ((AsyncRelayCommand)vm.BrowseFile1Command).ExecuteAsync(null);
            vm.SelectedFilePath1.Should().Be(@"C:\selected.xlsx");
        }

        // ===== Command gating =====

        [Fact]
        public void ImportCommand_DisabledWhenNoFile()
        {
            var (vm, _, _, _) = BuildSut();
            vm.ImportCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void ImportCommand_DisabledWhenBusy()
        {
            var (vm, _, _, _) = BuildSut();
            vm.SelectedFilePath1 = @"C:\a.xlsx";
            vm.IsImporting = true;
            vm.ImportCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void CancelCommand_DisabledWhenIdle()
        {
            var (vm, _, _, _) = BuildSut();
            vm.CancelCommand.CanExecute(null).Should().BeFalse();
            vm.IsImporting = true;
            vm.CancelCommand.CanExecute(null).Should().BeTrue();
        }

        // ===== State transitions =====

        [Fact]
        public void IsBusy_CombinesValidatingAndImporting()
        {
            var (vm, _, _, _) = BuildSut();
            vm.IsBusy.Should().BeFalse();
            vm.IsValidating = true;
            vm.IsBusy.Should().BeTrue();
            vm.IsValidating = false;
            vm.IsImporting = true;
            vm.IsBusy.Should().BeTrue();
        }

        // ===== INPC =====

        [Fact]
        public void SettingFile1_AlsoRaisesHasFileAndDisplayNotifications()
        {
            var (vm, _, _, _) = BuildSut();
            var notified = new List<string>();
            vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

            vm.SelectedFilePath1 = @"C:\x.xlsx";

            notified.Should().Contain(nameof(ImportAmbreViewModel.SelectedFilePath1))
                    .And.Contain(nameof(ImportAmbreViewModel.HasFile))
                    .And.Contain(nameof(ImportAmbreViewModel.SelectedFilesDisplay));
        }

        // ===== Import path (now testable thanks to IAmbreImportService) =====

        [Fact]
        public async Task ImportCommand_SingleFile_CallsImportAmbreFile()
        {
            var (vm, import, _, _) = BuildSut();
            var fakeResult = new ImportResult { CountryId = "FR", StartTime = DateTime.UtcNow };
            import.Setup(x => x.ImportAmbreFile(
                    It.IsAny<string>(), "FR", It.IsAny<Action<string, int>>()))
                .ReturnsAsync(fakeResult);

            vm.SelectedFilePath1 = @"C:\one.xlsx";
            await ((AsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);

            import.Verify(x => x.ImportAmbreFile(@"C:\one.xlsx", "FR", It.IsAny<Action<string, int>>()), Times.Once);
            import.Verify(x => x.ImportAmbreFiles(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Action<string, int>>()), Times.Never);
            vm.IsImporting.Should().BeFalse();
            vm.LastResult.Should().BeSameAs(fakeResult);
        }

        [Fact]
        public async Task ImportCommand_TwoFiles_CallsImportAmbreFiles()
        {
            var (vm, import, _, _) = BuildSut();
            var fakeResult = new ImportResult { CountryId = "FR", StartTime = DateTime.UtcNow };
            import.Setup(x => x.ImportAmbreFiles(
                    It.IsAny<string[]>(), "FR", It.IsAny<Action<string, int>>()))
                .ReturnsAsync(fakeResult);

            vm.SelectedFilePath1 = @"C:\a.xlsx";
            vm.SelectedFilePath2 = @"C:\b.xlsx";
            await ((AsyncRelayCommand)vm.ImportCommand).ExecuteAsync(null);

            import.Verify(x => x.ImportAmbreFiles(
                It.Is<string[]>(arr => arr.Length == 2 && arr[0] == @"C:\a.xlsx" && arr[1] == @"C:\b.xlsx"),
                "FR",
                It.IsAny<Action<string, int>>()), Times.Once);
            import.Verify(x => x.ImportAmbreFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Action<string, int>>()), Times.Never);
        }
    }
}
