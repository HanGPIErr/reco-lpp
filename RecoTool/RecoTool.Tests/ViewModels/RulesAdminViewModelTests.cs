using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Services.Rules;
using RecoTool.Services.UI;
using RecoTool.ViewModels;
using Xunit;

namespace RecoTool.Tests.ViewModels
{
    /// <summary>
    /// Tests pour <see cref="RulesAdminViewModel"/>. Désormais mockable via
    /// <see cref="IRulesAdmin"/> — plus besoin d'un faux OFS pour wrapper
    /// <see cref="TruthTableRepository"/>.
    /// </summary>
    public class RulesAdminViewModelTests
    {
        private static (RulesAdminViewModel vm, Mock<IRulesAdmin> repo, Mock<IDialogService> dlg) BuildSut()
        {
            var repo = new Mock<IRulesAdmin>();
            repo.Setup(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TruthRule>());
            var dlg = new Mock<IDialogService>();
            return (new RulesAdminViewModel(repo.Object, dlg.Object), repo, dlg);
        }

        [Fact]
        public void Ctor_NullArgs_Throws()
        {
            ((Action)(() => new RulesAdminViewModel(null, Mock.Of<IDialogService>()))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new RulesAdminViewModel(Mock.Of<IRulesAdmin>(), null))).Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_InitializesObservableCollections()
        {
            var (vm, _, _) = BuildSut();
            vm.Rules.Should().NotBeNull();
            vm.Scopes.Should().Contain(RuleScope.Edit);
            vm.RuleModes.Should().Contain(RuleMode.Apply);
            vm.AccountSides.Should().Equal("*", "P", "R");
        }

        // ===== Load =====

        [Fact]
        public async Task ReloadRulesAsync_LoadsAndPopulates()
        {
            var (vm, repo, _) = BuildSut();
            repo.Setup(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TruthRule>
                {
                    new TruthRule { RuleId = "R-A", Priority = 10 },
                    new TruthRule { RuleId = "R-B", Priority = 5 },
                });

            await vm.ReloadRulesAsync();

            vm.Rules.Should().HaveCount(2);
            vm.Rules[0].RuleId.Should().Be("R-B", "tri par Priority croissant");
            vm.Rules[1].RuleId.Should().Be("R-A");
            vm.StatusMessage.Should().Contain("2");
        }

        [Fact]
        public async Task ReloadRulesAsync_OnException_ShowsDialog()
        {
            var (vm, repo, dlg) = BuildSut();
            repo.Setup(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db down"));

            await vm.ReloadRulesAsync();

            vm.StatusMessage.Should().Be("Error");
            dlg.Verify(x => x.ShowErrorAsync("Load rules", "db down"), Times.Once);
        }

        // ===== Commands =====

        [Fact]
        public void AddCommand_RaisesEditRequested_WithFreshRule()
        {
            var (vm, _, _) = BuildSut();
            TruthRule captured = null;
            vm.EditRuleRequested += (_, r) => captured = r;
            vm.AddCommand.Execute(null);
            captured.Should().NotBeNull();
            captured.Enabled.Should().BeTrue();
        }

        [Fact]
        public void EditSelectedCommand_DisabledWithoutSelection()
        {
            var (vm, _, _) = BuildSut();
            vm.EditSelectedCommand.CanExecute(null).Should().BeFalse();
            vm.SelectedRule = new TruthRule { RuleId = "R1" };
            vm.EditSelectedCommand.CanExecute(null).Should().BeTrue();
        }

        [Fact]
        public void EditSelectedCommand_RaisesEventWithCloneNotOriginal()
        {
            var (vm, _, _) = BuildSut();
            var original = new TruthRule { RuleId = "R1", Priority = 50 };
            vm.SelectedRule = original;
            TruthRule captured = null;
            vm.EditRuleRequested += (_, r) => captured = r;
            vm.EditSelectedCommand.Execute(null);

            captured.Should().NotBeSameAs(original);
            captured.RuleId.Should().Be("R1");
            captured.Priority.Should().Be(50);
        }

        [Fact]
        public void DeleteSelectedCommand_DisabledWithoutSelection()
        {
            var (vm, _, _) = BuildSut();
            vm.DeleteSelectedCommand.CanExecute(null).Should().BeFalse();
        }

        [Fact]
        public async Task DeleteSelectedCommand_OnConfirm_CallsRepoAndReloads()
        {
            var (vm, repo, dlg) = BuildSut();
            dlg.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            repo.Setup(x => x.DeleteRuleAsync("R1", It.IsAny<CancellationToken>())).ReturnsAsync(1);
            vm.SelectedRule = new TruthRule { RuleId = "R1" };

            await ((AsyncRelayCommand)vm.DeleteSelectedCommand).ExecuteAsync(null);

            repo.Verify(x => x.DeleteRuleAsync("R1", It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task DeleteSelectedCommand_OnCancel_DoesNotCallRepo()
        {
            var (vm, repo, dlg) = BuildSut();
            dlg.Setup(x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            vm.SelectedRule = new TruthRule { RuleId = "R1" };

            await ((AsyncRelayCommand)vm.DeleteSelectedCommand).ExecuteAsync(null);

            repo.Verify(x => x.DeleteRuleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ApplyEditedRule_UpsertsAndReloads()
        {
            var (vm, repo, _) = BuildSut();
            var rule = new TruthRule { RuleId = "R-NEW" };
            await vm.ApplyEditedRule(rule);

            repo.Verify(x => x.UpsertRuleAsync(rule, It.IsAny<CancellationToken>()), Times.Once);
            repo.Verify(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task ApplyEditedRule_NullRule_NoOp()
        {
            var (vm, repo, _) = BuildSut();
            await vm.ApplyEditedRule(null);
            repo.Verify(x => x.UpsertRuleAsync(It.IsAny<TruthRule>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task EnsureTableCommand_DelegatesToRepo()
        {
            var (vm, repo, _) = BuildSut();
            repo.Setup(x => x.EnsureRulesTableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

            await ((AsyncRelayCommand)vm.EnsureTableCommand).ExecuteAsync(null);

            repo.Verify(x => x.EnsureRulesTableAsync(It.IsAny<CancellationToken>()), Times.Once);
            vm.StatusMessage.Should().Be("Table ensured");
        }

        [Fact]
        public void RunRulesNowCommand_RaisesEvent()
        {
            var (vm, _, _) = BuildSut();
            int hits = 0;
            vm.RunRulesNowRequested += (_, __) => hits++;
            vm.RunRulesNowCommand.Execute(null);
            hits.Should().Be(1);
        }

        // ===== Search filtering =====

        [Fact]
        public async Task SearchText_FiltersRulesInMemory()
        {
            var (vm, repo, _) = BuildSut();
            repo.Setup(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TruthRule>
                {
                    new TruthRule { RuleId = "R-MATCH-FR", Priority = 1, AccountSide = "P" },
                    new TruthRule { RuleId = "R-OTHER", Priority = 2, AccountSide = "R" },
                    new TruthRule { RuleId = "R-MATCH-DE", Priority = 3, AccountSide = "P" }
                });
            await vm.ReloadRulesAsync();

            vm.SearchText = "MATCH";

            vm.Rules.Should().HaveCount(2);
            vm.Rules.Select(r => r.RuleId).Should().Equal("R-MATCH-FR", "R-MATCH-DE");
        }

        [Fact]
        public async Task SearchText_Empty_RestoresAllRules()
        {
            var (vm, repo, _) = BuildSut();
            repo.Setup(x => x.LoadRulesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<TruthRule>
                {
                    new TruthRule { RuleId = "A", Priority = 1 },
                    new TruthRule { RuleId = "B", Priority = 2 }
                });
            await vm.ReloadRulesAsync();
            vm.SearchText = "A";
            vm.Rules.Should().HaveCount(1);
            vm.SearchText = "";
            vm.Rules.Should().HaveCount(2);
        }
    }
}
