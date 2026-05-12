using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.Rules;
using RecoTool.Services.UI;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    public class RuleEditorViewModelTests
    {
        private static (RuleEditorViewModel vm, Mock<IDialogService> dlg) BuildSut(TruthRule seed = null)
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 5, USR_Category = "Action", USR_FieldName = "Match" },
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "Paid" }
            });
            var dlg = new Mock<IDialogService>();
            var vm = new RuleEditorViewModel(seed, ofs.Object, dlg.Object);
            return (vm, dlg);
        }

        [Fact]
        public void Ctor_NullOfsOrDialog_Throws()
        {
            ((Action)(() => new RuleEditorViewModel(null, null, Mock.Of<IDialogService>()))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new RuleEditorViewModel(null, Mock.Of<IOfflineFirstService>(), null))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_NullSeed_CreatesDefaultEnabledRule()
        {
            var (vm, _) = BuildSut(seed: null);
            vm.EditedRule.Should().NotBeNull();
            vm.EditedRule.Enabled.Should().BeTrue();
            vm.ResultRule.Should().BeNull();
        }

        [Fact]
        public void Ctor_SeedIsClonedNotReferenced()
        {
            var src = new TruthRule { RuleId = "R1", Priority = 50 };
            var (vm, _) = BuildSut(src);
            vm.EditedRule.Should().NotBeSameAs(src);
            vm.EditedRule.RuleId.Should().Be("R1");
            vm.EditedRule.Priority.Should().Be(50);
        }

        [Fact]
        public void Ctor_PopulatesReferentialOptionsFromOffline()
        {
            var (vm, _) = BuildSut();
            vm.ActionOptions.Should().HaveCount(1);
            vm.KpiOptions.Should().HaveCount(1);
        }

        [Fact]
        public void SaveCommand_DisabledWhenRuleIdEmpty()
        {
            var (vm, _) = BuildSut(new TruthRule { RuleId = null });
            vm.SaveCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public void SaveCommand_StoresResultRuleAndRaisesClose()
        {
            var (vm, _) = BuildSut(new TruthRule { RuleId = "R1" });
            bool? savedFlag = null;
            vm.CloseRequested += (_, s) => savedFlag = s;

            vm.SaveCommand.Execute(null);

            vm.ResultRule.Should().NotBeNull();
            vm.ResultRule.RuleId.Should().Be("R1");
            vm.IsSaved.Should().BeTrue();
            savedFlag.Should().BeTrue();
        }

        [Fact]
        public void SaveCommand_WithoutRuleId_SetsValidationError()
        {
            var (vm, _) = BuildSut(new TruthRule { RuleId = "" });
            // Bypass CanSave manually invoking the method via reflection or set RuleId after ctor.
            // Here we set EditedRule.RuleId to empty and try.
            vm.EditedRule.RuleId = "";
            // Save command WILL be disabled, but if we forcefully invoke handler via reflection :
            var method = typeof(RuleEditorViewModel).GetMethod("Save",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method.Invoke(vm, null);
            vm.ValidationError.Should().Contain("RuleId");
        }

        [Fact]
        public void CancelCommand_ClearsResultAndRaisesClose()
        {
            var (vm, _) = BuildSut(new TruthRule { RuleId = "R1" });
            bool? savedFlag = null;
            vm.CloseRequested += (_, s) => savedFlag = s;

            vm.CancelCommand.Execute(null);

            vm.ResultRule.Should().BeNull();
            vm.IsSaved.Should().BeFalse();
            savedFlag.Should().BeFalse();
        }

        [Fact]
        public void RunNow_BoundFieldRoundTrips()
        {
            var (vm, _) = BuildSut();
            vm.RunNow.Should().BeFalse();
            vm.RunNow = true;
            vm.RunNow.Should().BeTrue();
        }
    }
}
