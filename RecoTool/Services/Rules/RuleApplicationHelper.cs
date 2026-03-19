using System;
using System.Collections.Generic;
using System.Linq;
using RecoTool.Infrastructure.Logging;
using RecoTool.Models;

namespace RecoTool.Services.Rules
{
    /// <summary>
    /// Centralizes rule output application to a Reconciliation entity.
    /// Eliminates duplication across ReconciliationService (edit + run-now) and AmbreReconciliationUpdater (import).
    /// </summary>
    public static class RuleApplicationHelper
    {
        /// <summary>
        /// Applies rule evaluation outputs to a Reconciliation entity.
        /// Returns true if any field was modified.
        /// </summary>
        public static bool ApplyOutputs(RuleEvaluationResult result, Reconciliation reconciliation, string currentUser)
        {
            if (result?.Rule == null || reconciliation == null) return false;

            bool modified = false;

            if (result.NewActionIdSelf.HasValue)
            {
                reconciliation.Action = result.NewActionIdSelf.Value;
                modified = true;
            }

            if (result.NewActionStatusSelf.HasValue)
            {
                reconciliation.ActionStatus = result.NewActionStatusSelf.Value;
                try { reconciliation.ActionDate = DateTime.Now; } catch { }
                modified = true;
            }
            else if (result.NewActionDoneSelf.HasValue)
            {
                reconciliation.ActionStatus = result.NewActionDoneSelf.Value == 1;
                reconciliation.ActionDate = DateTime.Now;
                modified = true;
            }

            if (result.NewKpiIdSelf.HasValue)
            {
                reconciliation.KPI = result.NewKpiIdSelf.Value;
                modified = true;
            }

            if (result.NewIncidentTypeIdSelf.HasValue)
            {
                reconciliation.IncidentType = result.NewIncidentTypeIdSelf.Value;
                modified = true;
            }

            if (result.NewRiskyItemSelf.HasValue)
            {
                reconciliation.RiskyItem = result.NewRiskyItemSelf.Value;
                modified = true;
            }

            if (result.NewReasonNonRiskyIdSelf.HasValue)
            {
                reconciliation.ReasonNonRisky = result.NewReasonNonRiskyIdSelf.Value;
                modified = true;
            }

            if (result.NewToRemindSelf.HasValue)
            {
                reconciliation.ToRemind = result.NewToRemindSelf.Value;
                modified = true;
            }

            if (result.NewToRemindDaysSelf.HasValue)
            {
                try { reconciliation.ToRemindDate = DateTime.Today.AddDays(result.NewToRemindDaysSelf.Value); }
                catch { /* ignore malformed date */ }
                modified = true;
            }

            if (result.NewFirstClaimTodaySelf.HasValue && result.NewFirstClaimTodaySelf.Value == 1)
            {
                if (reconciliation.FirstClaimDate.HasValue)
                    reconciliation.LastClaimDate = DateTime.Today;
                else
                    reconciliation.FirstClaimDate = DateTime.Today;
                modified = true;
            }

            // Append user message to comments if present
            if (!string.IsNullOrWhiteSpace(result.UserMessage))
            {
                try
                {
                    var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {currentUser}: ";
                    var msg = prefix + $"[Rule {result.Rule.RuleId ?? "(unnamed)"}] {result.UserMessage}";
                    if (string.IsNullOrWhiteSpace(reconciliation.Comments))
                        reconciliation.Comments = msg;
                    else if (!reconciliation.Comments.Contains(msg))
                        reconciliation.Comments = msg + Environment.NewLine + reconciliation.Comments;
                }
                catch { }
            }

            return modified;
        }

        /// <summary>
        /// Builds a summary string of applied rule outputs for logging.
        /// </summary>
        public static string BuildOutputSummary(RuleEvaluationResult result)
        {
            if (result == null) return string.Empty;

            var parts = new List<string>();
            if (result.NewActionIdSelf.HasValue) parts.Add($"Action={result.NewActionIdSelf.Value}");
            if (result.NewKpiIdSelf.HasValue) parts.Add($"KPI={result.NewKpiIdSelf.Value}");
            if (result.NewIncidentTypeIdSelf.HasValue) parts.Add($"IncidentType={result.NewIncidentTypeIdSelf.Value}");
            if (result.NewRiskyItemSelf.HasValue) parts.Add($"RiskyItem={result.NewRiskyItemSelf.Value}");
            if (result.NewReasonNonRiskyIdSelf.HasValue) parts.Add($"ReasonNonRisky={result.NewReasonNonRiskyIdSelf.Value}");
            if (result.NewToRemindSelf.HasValue) parts.Add($"ToRemind={result.NewToRemindSelf.Value}");
            if (result.NewToRemindDaysSelf.HasValue) parts.Add($"ToRemindDays={result.NewToRemindDaysSelf.Value}");
            if (result.NewActionDoneSelf.HasValue) parts.Add($"ActionStatus={(result.NewActionDoneSelf.Value == 1 ? "DONE" : "PENDING")}");
            if (result.NewFirstClaimTodaySelf.HasValue && result.NewFirstClaimTodaySelf.Value == 1) parts.Add("FirstClaimDate=Today");

            return string.Join("; ", parts);
        }

        /// <summary>
        /// Applies outputs and logs the rule application in a single call.
        /// </summary>
        public static bool ApplyAndLog(RuleEvaluationResult result, Reconciliation reconciliation, string currentUser, string origin, string countryId, Action<string, string, string, string, string, string> raiseEvent = null)
        {
            if (result?.Rule == null || reconciliation == null) return false;
            if (!result.Rule.AutoApply) return false;

            bool modified = ApplyOutputs(result, reconciliation, currentUser);

            if (modified)
            {
                try
                {
                    var summary = BuildOutputSummary(result);
                    LogHelper.WriteRuleApplied(origin, countryId, reconciliation.ID, result.Rule.RuleId, summary, result.UserMessage);
                    raiseEvent?.Invoke(origin, countryId, reconciliation.ID, result.Rule.RuleId, summary, result.UserMessage);
                }
                catch { }
            }

            return modified;
        }
    }
}
