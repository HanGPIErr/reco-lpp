using System;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services.Rules;
using Xunit;

namespace RecoTool.Tests.Services.Rules
{
    /// <summary>
    /// Tests pour <see cref="RuleApplicationHelper"/> — pure logique d'application
    /// d'un <see cref="RuleEvaluationResult"/> sur une <see cref="Reconciliation"/>.
    /// Couvre :
    ///   • StampUserEdit / ClearUserEditStamp
    ///   • IsFieldLockedByUserEdit (fenêtre de protection 7j par défaut)
    ///   • ApplyOutputs (idempotence, application des champs, lock)
    ///   • BuildOutputSummary
    /// </summary>
    public class RuleApplicationHelperTests
    {
        // ===== StampUserEdit =====

        [Fact]
        public void StampUserEdit_NullReco_NoOp()
        {
            // Pas d'exception
            RuleApplicationHelper.StampUserEdit(null, "Action");
        }

        [Fact]
        public void StampUserEdit_EmptyArray_NoOp()
        {
            var r = new Reconciliation();
            RuleApplicationHelper.StampUserEdit(r);
            r.LastModifiedByUser.Should().BeNull();
            r.UserEditedFields.Should().BeNull();
        }

        [Fact]
        public void StampUserEdit_NewField_RecordsTimestampAndField()
        {
            var r = new Reconciliation();
            var before = DateTime.Now.AddSeconds(-1);
            RuleApplicationHelper.StampUserEdit(r, "Action");
            r.LastModifiedByUser.Should().NotBeNull();
            r.LastModifiedByUser.Value.Should().BeOnOrAfter(before);
            r.UserEditedFields.Should().Be("Action");
        }

        [Fact]
        public void StampUserEdit_AddNewField_MergesIntoExisting()
        {
            var r = new Reconciliation { UserEditedFields = "Action" };
            RuleApplicationHelper.StampUserEdit(r, "KPI");
            r.UserEditedFields.Should().Contain("Action").And.Contain("KPI");
            r.UserEditedFields.Split('|').Should().HaveCount(2);
        }

        [Fact]
        public void StampUserEdit_DuplicateFieldCaseInsensitive_NotAddedTwice()
        {
            var r = new Reconciliation { UserEditedFields = "action" };
            RuleApplicationHelper.StampUserEdit(r, "ACTION", "kpi");
            r.UserEditedFields.Split('|').Should().HaveCount(2);
        }

        [Fact]
        public void StampUserEdit_NullOrEmptyFieldsInArgsIgnored()
        {
            var r = new Reconciliation();
            RuleApplicationHelper.StampUserEdit(r, "Action", null, "", "  ", "KPI");
            r.UserEditedFields.Split('|').Should().HaveCount(2);
        }

        // ===== ClearUserEditStamp =====

        [Fact]
        public void ClearUserEditStamp_ResetsBothFields()
        {
            var r = new Reconciliation
            {
                LastModifiedByUser = DateTime.Now,
                UserEditedFields = "Action|KPI"
            };
            RuleApplicationHelper.ClearUserEditStamp(r);
            r.LastModifiedByUser.Should().BeNull();
            r.UserEditedFields.Should().BeNull();
        }

        [Fact]
        public void ClearUserEditStamp_Null_NoOp()
        {
            RuleApplicationHelper.ClearUserEditStamp(null);
        }

        // ===== IsFieldLockedByUserEdit =====

        [Fact]
        public void IsFieldLockedByUserEdit_RuleNotRespectingUserEdits_NeverLocked()
        {
            var rule = new TruthRule { RespectUserEdits = false };
            var reco = new Reconciliation { LastModifiedByUser = DateTime.Now, UserEditedFields = "Action" };
            RuleApplicationHelper.IsFieldLockedByUserEdit("Action", reco, rule).Should().BeFalse();
        }

        [Fact]
        public void IsFieldLockedByUserEdit_NoTimestamp_NotLocked()
        {
            var rule = new TruthRule { RespectUserEdits = true };
            var reco = new Reconciliation { UserEditedFields = "Action" };
            RuleApplicationHelper.IsFieldLockedByUserEdit("Action", reco, rule).Should().BeFalse();
        }

        [Fact]
        public void IsFieldLockedByUserEdit_FieldNotInList_NotLocked()
        {
            var rule = new TruthRule { RespectUserEdits = true };
            var reco = new Reconciliation
            {
                LastModifiedByUser = DateTime.Now,
                UserEditedFields = "Action"
            };
            RuleApplicationHelper.IsFieldLockedByUserEdit("KPI", reco, rule).Should().BeFalse();
        }

        [Fact]
        public void IsFieldLockedByUserEdit_WithinDefaultWindow_Locked()
        {
            var rule = new TruthRule { RespectUserEdits = true, UserEditLockDays = null };
            var reco = new Reconciliation
            {
                LastModifiedByUser = DateTime.Now.AddDays(-3),
                UserEditedFields = "Action"
            };
            RuleApplicationHelper.IsFieldLockedByUserEdit("Action", reco, rule).Should().BeTrue();
        }

        [Fact]
        public void IsFieldLockedByUserEdit_OutsideWindow_NotLocked()
        {
            var rule = new TruthRule { RespectUserEdits = true, UserEditLockDays = 7 };
            var reco = new Reconciliation
            {
                LastModifiedByUser = DateTime.Now.AddDays(-10),
                UserEditedFields = "Action"
            };
            RuleApplicationHelper.IsFieldLockedByUserEdit("Action", reco, rule).Should().BeFalse();
        }

        [Fact]
        public void IsFieldLockedByUserEdit_CaseInsensitiveFieldMatch()
        {
            var rule = new TruthRule { RespectUserEdits = true };
            var reco = new Reconciliation
            {
                LastModifiedByUser = DateTime.Now.AddDays(-1),
                UserEditedFields = "ACTION"
            };
            RuleApplicationHelper.IsFieldLockedByUserEdit("action", reco, rule).Should().BeTrue();
        }

        [Fact]
        public void IsFieldLockedByUserEdit_ZeroOrNegativeDays_FallsBackToDefault()
        {
            var rule = new TruthRule { RespectUserEdits = true, UserEditLockDays = 0 };
            var reco = new Reconciliation
            {
                LastModifiedByUser = DateTime.Now.AddDays(-3),
                UserEditedFields = "Action"
            };
            // Default = 7 jours
            RuleApplicationHelper.IsFieldLockedByUserEdit("Action", reco, rule).Should().BeTrue();
        }

        // ===== ApplyOutputs =====

        private static RuleEvaluationResult MakeResult(TruthRule rule, Action<RuleEvaluationResult> setup = null)
        {
            var res = new RuleEvaluationResult { Rule = rule };
            setup?.Invoke(res);
            return res;
        }

        [Fact]
        public void ApplyOutputs_NullArgs_ReturnsFalse()
        {
            RuleApplicationHelper.ApplyOutputs(null, new Reconciliation(), "u").Should().BeFalse();
            RuleApplicationHelper.ApplyOutputs(new RuleEvaluationResult { Rule = null }, new Reconciliation(), "u").Should().BeFalse();
            RuleApplicationHelper.ApplyOutputs(new RuleEvaluationResult { Rule = new TruthRule() }, null, "u").Should().BeFalse();
        }

        [Fact]
        public void ApplyOutputs_AllOutputs_AppliedAndModifiedTrue()
        {
            var rule = new TruthRule { RuleId = "R1" };
            var res = MakeResult(rule, r =>
            {
                r.NewActionIdSelf = 5;
                r.NewKpiIdSelf = 18;
                r.NewIncidentTypeIdSelf = 24;
                r.NewRiskyItemSelf = true;
                r.NewReasonNonRiskyIdSelf = 99;
                r.NewToRemindSelf = true;
                r.NewToRemindDaysSelf = 3;
                r.NewActionDoneSelf = 1;  // → ActionStatus = true
                r.NewFirstClaimTodaySelf = true;
            });
            var reco = new Reconciliation();

            var modified = RuleApplicationHelper.ApplyOutputs(res, reco, "alice");

            modified.Should().BeTrue();
            reco.Action.Should().Be(5);
            reco.KPI.Should().Be(18);
            reco.IncidentType.Should().Be(24);
            reco.RiskyItem.Should().BeTrue();
            reco.ReasonNonRisky.Should().Be(99);
            reco.ToRemind.Should().BeTrue();
            reco.ToRemindDate.Should().Be(DateTime.Today.AddDays(3));
            reco.ActionStatus.Should().BeTrue();
            reco.ActionDate.Should().NotBeNull();
            reco.FirstClaimDate.Should().Be(DateTime.Today);
            reco.LastRuleAppliedId.Should().Be("R1");
            reco.LastRuleAppliedAt.Should().NotBeNull();
        }

        [Fact]
        public void ApplyOutputs_Idempotent_SecondCallReturnsFalse()
        {
            var rule = new TruthRule { RuleId = "R" };
            var res = MakeResult(rule, r => { r.NewActionIdSelf = 5; r.NewKpiIdSelf = 18; });
            var reco = new Reconciliation();

            RuleApplicationHelper.ApplyOutputs(res, reco, "u").Should().BeTrue();
            // Second call : aucun changement à apporter
            var lastRuleAt = reco.LastRuleAppliedAt;
            RuleApplicationHelper.ApplyOutputs(res, reco, "u").Should().BeFalse();
            // Le LastRuleAppliedAt ne bouge pas si rien n'a été modifié
            reco.LastRuleAppliedAt.Should().Be(lastRuleAt);
        }

        [Fact]
        public void ApplyOutputs_FieldLockedByUserEdit_NotOverwritten()
        {
            var rule = new TruthRule { RuleId = "R", RespectUserEdits = true, UserEditLockDays = 7 };
            var res = MakeResult(rule, r => r.NewActionIdSelf = 99);
            var reco = new Reconciliation
            {
                Action = 5,
                LastModifiedByUser = DateTime.Now.AddDays(-1),
                UserEditedFields = "Action"
            };

            RuleApplicationHelper.ApplyOutputs(res, reco, "alice").Should().BeFalse();
            reco.Action.Should().Be(5, "le champ utilisateur ne doit pas être écrasé pendant la fenêtre de lock");
            // Comments doit contenir une note de suppression
            reco.Comments.Should().Contain("Suppressed on: Action");
        }

        [Fact]
        public void ApplyOutputs_ActionStatusFromActionDone_StampsActionDate()
        {
            var rule = new TruthRule { RuleId = "R" };
            var res = MakeResult(rule, r => r.NewActionDoneSelf = 1);
            var reco = new Reconciliation { ActionStatus = false };

            RuleApplicationHelper.ApplyOutputs(res, reco, "u").Should().BeTrue();
            reco.ActionStatus.Should().BeTrue();
            reco.ActionDate.Should().NotBeNull();
        }

        [Fact]
        public void ApplyOutputs_FirstClaimDate_RefreshesLastClaimWhenAlreadyClaimed()
        {
            var rule = new TruthRule { RuleId = "R" };
            var res = MakeResult(rule, r => r.NewFirstClaimTodaySelf = true);
            var reco = new Reconciliation
            {
                FirstClaimDate = DateTime.Today.AddDays(-10), // déjà réclamé il y a 10j
                LastClaimDate = DateTime.Today.AddDays(-5)
            };

            RuleApplicationHelper.ApplyOutputs(res, reco, "u").Should().BeTrue();
            reco.FirstClaimDate.Should().Be(DateTime.Today.AddDays(-10), "premier claim ne doit pas être réécrit");
            reco.LastClaimDate.Should().Be(DateTime.Today, "last claim mis à jour à aujourd'hui");
        }

        [Fact]
        public void ApplyOutputs_UserMessage_AppendedToComments()
        {
            var rule = new TruthRule { RuleId = "R-MSG" };
            var res = MakeResult(rule, r =>
            {
                r.NewActionIdSelf = 5;
                r.UserMessage = "please review";
            });
            var reco = new Reconciliation();

            RuleApplicationHelper.ApplyOutputs(res, reco, "alice").Should().BeTrue();
            reco.Comments.Should().Contain("[Rule R-MSG] please review");
            reco.Comments.Should().Contain("alice");
        }

        [Fact]
        public void ApplyOutputs_UserMessage_DeduplicatedOnSecondCall()
        {
            var rule = new TruthRule { RuleId = "R-MSG" };
            var res = MakeResult(rule, r =>
            {
                r.NewActionIdSelf = 5;
                r.UserMessage = "please review";
            });
            var reco = new Reconciliation();

            RuleApplicationHelper.ApplyOutputs(res, reco, "alice");
            var firstComment = reco.Comments;

            // Deuxième appel : Action déjà appliquée, message déjà présent → pas de nouvel ajout
            RuleApplicationHelper.ApplyOutputs(res, reco, "alice");
            reco.Comments.Should().Be(firstComment);
        }

        // ===== BuildOutputSummary =====

        [Fact]
        public void BuildOutputSummary_NullResult_ReturnsEmpty()
        {
            RuleApplicationHelper.BuildOutputSummary(null).Should().Be(string.Empty);
        }

        [Fact]
        public void BuildOutputSummary_AllOutputs_ConcatenatesEntries()
        {
            var res = new RuleEvaluationResult
            {
                NewActionIdSelf = 5, NewKpiIdSelf = 18, NewRiskyItemSelf = true,
                NewActionDoneSelf = 1, NewFirstClaimTodaySelf = true
            };
            var s = RuleApplicationHelper.BuildOutputSummary(res);
            s.Should().Contain("Action=5")
             .And.Contain("KPI=18")
             .And.Contain("RiskyItem=True")
             .And.Contain("ActionStatus=DONE")
             .And.Contain("FirstClaimDate=Today");
        }

        [Fact]
        public void BuildOutputSummary_ActionDoneZero_RendersAsPending()
        {
            var s = RuleApplicationHelper.BuildOutputSummary(new RuleEvaluationResult { NewActionDoneSelf = 0 });
            s.Should().Be("ActionStatus=PENDING");
        }

        [Fact]
        public void BuildOutputSummary_NoOutputs_ReturnsEmpty()
        {
            RuleApplicationHelper.BuildOutputSummary(new RuleEvaluationResult()).Should().Be(string.Empty);
        }
    }
}
