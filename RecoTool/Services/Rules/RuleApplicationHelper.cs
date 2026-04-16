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
        /// <summary>Global fallback lock window (days) when a rule does not define <see cref="TruthRule.UserEditLockDays"/>.</summary>
        public const int DefaultUserEditLockDays = 7;

        /// <summary>
        /// Records that a user has manually edited one or more fields on the reconciliation.
        /// Updates <see cref="Reconciliation.LastModifiedByUser"/> to now and merges <paramref name="editedFields"/>
        /// into <see cref="Reconciliation.UserEditedFields"/> (pipe-separated, deduplicated, case-insensitive).
        /// Call this from the UI layer right before saving a user-driven change so that rules with
        /// <c>RespectUserEdits=true</c> won't silently overwrite the user's choice during the next evaluation.
        /// </summary>
        public static void StampUserEdit(Reconciliation reconciliation, params string[] editedFields)
        {
            if (reconciliation == null || editedFields == null || editedFields.Length == 0) return;

            reconciliation.LastModifiedByUser = DateTime.Now;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(reconciliation.UserEditedFields))
            {
                foreach (var f in reconciliation.UserEditedFields.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = f.Trim();
                    if (!string.IsNullOrWhiteSpace(t)) set.Add(t);
                }
            }
            foreach (var f in editedFields)
            {
                if (!string.IsNullOrWhiteSpace(f)) set.Add(f.Trim());
            }
            reconciliation.UserEditedFields = set.Count == 0 ? null : string.Join("|", set);
        }

        /// <summary>
        /// Clears the user-edit stamp on a reconciliation (e.g. when an admin decides to un-protect the row).
        /// </summary>
        public static void ClearUserEditStamp(Reconciliation reconciliation)
        {
            if (reconciliation == null) return;
            reconciliation.LastModifiedByUser = null;
            reconciliation.UserEditedFields = null;
        }

        /// <summary>
        /// Returns true if <paramref name="fieldName"/> is currently protected from rule overwrite
        /// because the user edited it recently. Honours <see cref="TruthRule.RespectUserEdits"/>.
        /// </summary>
        public static bool IsFieldLockedByUserEdit(string fieldName, Reconciliation reconciliation, TruthRule rule)
        {
            if (rule == null || !rule.RespectUserEdits) return false;
            if (reconciliation == null || string.IsNullOrWhiteSpace(fieldName)) return false;
            if (!reconciliation.LastModifiedByUser.HasValue) return false;
            if (string.IsNullOrWhiteSpace(reconciliation.UserEditedFields)) return false;

            var userFields = reconciliation.UserEditedFields.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            bool matched = false;
            foreach (var f in userFields)
            {
                if (string.Equals(f.Trim(), fieldName, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
            }
            if (!matched) return false;

            int days = rule.UserEditLockDays.HasValue && rule.UserEditLockDays.Value > 0
                ? rule.UserEditLockDays.Value : DefaultUserEditLockDays;
            return reconciliation.LastModifiedByUser.Value.AddDays(days) > DateTime.Now;
        }

        /// <summary>
        /// Applies rule evaluation outputs to a Reconciliation entity.
        /// Returns true if any field was actually changed (idempotent: a rule re-applied
        /// on an already-consistent state returns false and performs no mutation).
        /// Per-field user-edit protection: fields listed in <c>Reconciliation.UserEditedFields</c>
        /// within the rule's lock window are skipped when the rule has <c>RespectUserEdits=true</c>.
        /// </summary>
        public static bool ApplyOutputs(RuleEvaluationResult result, Reconciliation reconciliation, string currentUser)
        {
            if (result?.Rule == null || reconciliation == null) return false;

            bool modified = false;
            var rule = result.Rule;
            var lockedFields = new List<string>();

            bool TryChange(string field, Func<bool> apply)
            {
                if (IsFieldLockedByUserEdit(field, reconciliation, rule)) { lockedFields.Add(field); return false; }
                return apply();
            }

            // --- Action ---
            if (result.NewActionIdSelf.HasValue && reconciliation.Action != result.NewActionIdSelf.Value)
            {
                TryChange("Action", () =>
                {
                    reconciliation.Action = result.NewActionIdSelf.Value;
                    modified = true; return true;
                });
            }

            // --- ActionStatus + ActionDate ---
            // Only stamp ActionDate when status effectively changes.
            if (result.NewActionStatusSelf.HasValue)
            {
                if (reconciliation.ActionStatus != result.NewActionStatusSelf.Value)
                {
                    TryChange("ActionStatus", () =>
                    {
                        reconciliation.ActionStatus = result.NewActionStatusSelf.Value;
                        try { reconciliation.ActionDate = DateTime.Now; } catch { }
                        modified = true; return true;
                    });
                }
            }
            else if (result.NewActionDoneSelf.HasValue)
            {
                bool newStatus = result.NewActionDoneSelf.Value == 1;
                if (reconciliation.ActionStatus != newStatus)
                {
                    TryChange("ActionStatus", () =>
                    {
                        reconciliation.ActionStatus = newStatus;
                        try { reconciliation.ActionDate = DateTime.Now; } catch { }
                        modified = true; return true;
                    });
                }
            }

            // --- KPI ---
            if (result.NewKpiIdSelf.HasValue && reconciliation.KPI != result.NewKpiIdSelf.Value)
            {
                TryChange("KPI", () =>
                {
                    reconciliation.KPI = result.NewKpiIdSelf.Value;
                    modified = true; return true;
                });
            }

            // --- IncidentType ---
            if (result.NewIncidentTypeIdSelf.HasValue && reconciliation.IncidentType != result.NewIncidentTypeIdSelf.Value)
            {
                TryChange("IncidentType", () =>
                {
                    reconciliation.IncidentType = result.NewIncidentTypeIdSelf.Value;
                    modified = true; return true;
                });
            }

            // --- RiskyItem ---
            if (result.NewRiskyItemSelf.HasValue && reconciliation.RiskyItem != result.NewRiskyItemSelf.Value)
            {
                TryChange("RiskyItem", () =>
                {
                    reconciliation.RiskyItem = result.NewRiskyItemSelf.Value;
                    modified = true; return true;
                });
            }

            // --- ReasonNonRisky ---
            if (result.NewReasonNonRiskyIdSelf.HasValue && reconciliation.ReasonNonRisky != result.NewReasonNonRiskyIdSelf.Value)
            {
                TryChange("ReasonNonRisky", () =>
                {
                    reconciliation.ReasonNonRisky = result.NewReasonNonRiskyIdSelf.Value;
                    modified = true; return true;
                });
            }

            // --- ToRemind ---
            if (result.NewToRemindSelf.HasValue && reconciliation.ToRemind != result.NewToRemindSelf.Value)
            {
                TryChange("ToRemind", () =>
                {
                    reconciliation.ToRemind = result.NewToRemindSelf.Value;
                    modified = true; return true;
                });
            }

            // --- ToRemindDate (derived from NewToRemindDaysSelf relative to today) ---
            if (result.NewToRemindDaysSelf.HasValue)
            {
                try
                {
                    var target = DateTime.Today.AddDays(result.NewToRemindDaysSelf.Value);
                    if (reconciliation.ToRemindDate != target)
                    {
                        TryChange("ToRemindDate", () =>
                        {
                            reconciliation.ToRemindDate = target;
                            modified = true; return true;
                        });
                    }
                }
                catch { /* ignore malformed date */ }
            }

            // --- FirstClaimDate / LastClaimDate ---
            if (result.NewFirstClaimTodaySelf == true)
            {
                var today = DateTime.Today;
                if (reconciliation.FirstClaimDate.HasValue)
                {
                    // Existing claim → refresh LastClaimDate only if different day
                    if (reconciliation.LastClaimDate != today)
                    {
                        TryChange("LastClaimDate", () =>
                        {
                            reconciliation.LastClaimDate = today;
                            modified = true; return true;
                        });
                    }
                }
                else
                {
                    TryChange("FirstClaimDate", () =>
                    {
                        reconciliation.FirstClaimDate = today;
                        modified = true; return true;
                    });
                }
            }

            // --- Append user message to comments (idempotent: deduplicate by rule+message content, ignoring timestamp) ---
            if (!string.IsNullOrWhiteSpace(result.UserMessage))
            {
                try
                {
                    var rulePattern = $"[Rule {result.Rule.RuleId ?? "(unnamed)"}] {result.UserMessage}";
                    bool alreadyPresent = !string.IsNullOrEmpty(reconciliation.Comments)
                                          && reconciliation.Comments.IndexOf(rulePattern, StringComparison.Ordinal) >= 0;
                    if (!alreadyPresent)
                    {
                        // Comments is a safe field to append to (never "locked" by user-edit since we only append)
                        var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {currentUser}: ";
                        var msg = prefix + rulePattern;
                        if (string.IsNullOrWhiteSpace(reconciliation.Comments))
                            reconciliation.Comments = msg;
                        else
                            reconciliation.Comments = msg + Environment.NewLine + reconciliation.Comments;
                        modified = true;
                    }
                }
                catch { }
            }

            // --- Audit stamp: record which rule was last applied (only when something actually changed) ---
            if (modified)
            {
                reconciliation.LastRuleAppliedId = rule.RuleId;
                reconciliation.LastRuleAppliedAt = DateTime.UtcNow;
            }

            // --- Diagnostic: annotate Comments when the rule was partially suppressed by user-edit lock ---
            if (lockedFields.Count > 0)
            {
                try
                {
                    var note = $"[Rule {rule.RuleId ?? "(unnamed)"}] Suppressed on: {string.Join(",", lockedFields)} (user-edit lock)";
                    bool noteAlready = !string.IsNullOrEmpty(reconciliation.Comments)
                                       && reconciliation.Comments.IndexOf(note, StringComparison.Ordinal) >= 0;
                    if (!noteAlready)
                    {
                        var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {currentUser}: ";
                        var line = prefix + note;
                        reconciliation.Comments = string.IsNullOrWhiteSpace(reconciliation.Comments)
                            ? line
                            : line + Environment.NewLine + reconciliation.Comments;
                    }
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
            if (result.NewFirstClaimTodaySelf == true) parts.Add("FirstClaimDate=Today");

            return string.Join("; ", parts);
        }

        /// <summary>
        /// Applies outputs and logs the rule application in a single call.
        /// When the rule is in <see cref="RuleMode.Propose"/> mode and <paramref name="proposalRepository"/>
        /// is provided, a pending proposal is created instead of mutating the reconciliation.
        /// </summary>
        public static bool ApplyAndLog(RuleEvaluationResult result, Reconciliation reconciliation, string currentUser, string origin, string countryId, Action<string, string, string, string, string, string> raiseEvent = null, RuleProposalRepository proposalRepository = null)
        {
            if (result?.Rule == null || reconciliation == null) return false;
            if (!result.Rule.AutoApply) return false;

            // Route to Propose if the rule asks for it AND a repository is available.
            // If no repository is passed, the proposal is silently lost (by design: callers that want
            // Propose support must pass the repository explicitly).
            if (result.Rule.Mode == RuleMode.Propose)
            {
                if (proposalRepository == null) return false;
                try
                {
                    var proposals = BuildProposalsFromResult(result, reconciliation, currentUser);
                    if (proposals.Count > 0)
                    {
                        proposalRepository.InsertProposalsAsync(proposals).GetAwaiter().GetResult();
                        try
                        {
                            var summary = BuildOutputSummary(result);
                            LogHelper.WriteRuleApplied(origin + ":propose", countryId, reconciliation.ID, result.Rule.RuleId, summary, result.UserMessage);
                            raiseEvent?.Invoke(origin + ":propose", countryId, reconciliation.ID, result.Rule.RuleId, summary, result.UserMessage);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Rules] Propose failed for rule {result.Rule.RuleId}: {ex.Message}");
                }
                return false; // never mutates the reconciliation
            }

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

        /// <summary>
        /// Builds the list of proposals (one per changed field) that a Propose-mode rule would produce
        /// against the current state of the reconciliation. Emits no proposal for fields that would be
        /// a no-op (already at the target value).
        /// </summary>
        public static List<RuleProposal> BuildProposalsFromResult(RuleEvaluationResult result, Reconciliation reco, string currentUser)
        {
            var list = new List<RuleProposal>();
            if (result?.Rule == null || reco == null) return list;

            var ruleId = result.Rule.RuleId ?? "(unnamed)";
            var now = DateTime.UtcNow;

            void Add(string field, string oldVal, string newVal)
            {
                if (string.Equals(oldVal ?? string.Empty, newVal ?? string.Empty, StringComparison.Ordinal)) return;
                list.Add(new RuleProposal
                {
                    RecoId = reco.ID,
                    RuleId = ruleId,
                    Field = field,
                    OldValue = oldVal,
                    NewValue = newVal,
                    CreatedAt = now,
                    CreatedBy = currentUser,
                    Status = ProposalStatus.Pending
                });
            }

            if (result.NewActionIdSelf.HasValue && reco.Action != result.NewActionIdSelf.Value)
                Add("Action", reco.Action?.ToString(), result.NewActionIdSelf.Value.ToString());

            if (result.NewActionStatusSelf.HasValue && reco.ActionStatus != result.NewActionStatusSelf.Value)
                Add("ActionStatus", reco.ActionStatus?.ToString(), result.NewActionStatusSelf.Value.ToString());
            else if (result.NewActionDoneSelf.HasValue)
            {
                bool newStatus = result.NewActionDoneSelf.Value == 1;
                if (reco.ActionStatus != newStatus)
                    Add("ActionStatus", reco.ActionStatus?.ToString(), newStatus.ToString());
            }

            if (result.NewKpiIdSelf.HasValue && reco.KPI != result.NewKpiIdSelf.Value)
                Add("KPI", reco.KPI?.ToString(), result.NewKpiIdSelf.Value.ToString());

            if (result.NewIncidentTypeIdSelf.HasValue && reco.IncidentType != result.NewIncidentTypeIdSelf.Value)
                Add("IncidentType", reco.IncidentType?.ToString(), result.NewIncidentTypeIdSelf.Value.ToString());

            if (result.NewRiskyItemSelf.HasValue && reco.RiskyItem != result.NewRiskyItemSelf.Value)
                Add("RiskyItem", reco.RiskyItem?.ToString(), result.NewRiskyItemSelf.Value.ToString());

            if (result.NewReasonNonRiskyIdSelf.HasValue && reco.ReasonNonRisky != result.NewReasonNonRiskyIdSelf.Value)
                Add("ReasonNonRisky", reco.ReasonNonRisky?.ToString(), result.NewReasonNonRiskyIdSelf.Value.ToString());

            if (result.NewToRemindSelf.HasValue && reco.ToRemind != result.NewToRemindSelf.Value)
                Add("ToRemind", reco.ToRemind.ToString(), result.NewToRemindSelf.Value.ToString());

            if (result.NewToRemindDaysSelf.HasValue)
            {
                try
                {
                    var target = DateTime.Today.AddDays(result.NewToRemindDaysSelf.Value);
                    if (reco.ToRemindDate != target)
                        Add("ToRemindDate", reco.ToRemindDate?.ToString("yyyy-MM-dd"), target.ToString("yyyy-MM-dd"));
                }
                catch { }
            }

            if (result.NewFirstClaimTodaySelf == true)
            {
                var today = DateTime.Today;
                if (reco.FirstClaimDate.HasValue)
                {
                    if (reco.LastClaimDate != today)
                        Add("LastClaimDate", reco.LastClaimDate?.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
                }
                else
                {
                    Add("FirstClaimDate", null, today.ToString("yyyy-MM-dd"));
                }
            }

            return list;
        }
    }
}
