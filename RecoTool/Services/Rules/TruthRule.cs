using System;
using System.Collections.Generic;

namespace RecoTool.Services.Rules
{
    public enum RuleScope
    {
        Import,
        Edit,
        Both,
        /// <summary>
        /// Triggered when external data (DWINGS, AMBRE refresh) changes, not necessarily a user edit.
        /// Evaluated via background re-evaluation or explicit Run-Now.
        /// </summary>
        DataChanged,
        /// <summary>
        /// Triggered by scheduled jobs (reminders, escalations based on time elapsed).
        /// Never fired during import or UI save.
        /// </summary>
        Scheduled
    }

    public enum ApplyTarget
    {
        Self,
        Counterpart,
        Both
    }

    /// <summary>
    /// Defines how a matching rule acts on the reconciliation.
    /// </summary>
    public enum RuleMode
    {
        /// <summary>Apply outputs directly to the reconciliation (classic behaviour).</summary>
        Apply,
        /// <summary>Create a pending proposal that a user has to accept/reject, without mutating the reconciliation.</summary>
        Propose
    }

    /// <summary>
    /// Declarative rule loaded from the referential truth-table or JSON fallback.
    /// Each condition is optional: when null/empty, it does not restrict matching.
    /// </summary>
    public class TruthRule
    {
        public string RuleId { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;
        public RuleScope Scope { get; set; } = RuleScope.Both;

        // Conditions
        // 'P' (Pivot), 'R' (Receivable), or '*' for any
        public string AccountSide { get; set; } = "*";
        // Semi-colon or comma separated list (e.g., "ISSU;REISSU;NOTIF"). '*' for any.
        public string GuaranteeType { get; set; }
        // Semi-colon or comma separated enum names from TransactionType (e.g., "INCOMING_PAYMENT;PAYMENT") or '*'
        public string TransactionType { get; set; }
        // Booking code(s): '*' for any, or a list like "FR;DE;ES". Matched against RuleContext.CountryId
        public string Booking { get; set; }
        // null => don't care; true/false => must match
        public bool? HasDwingsLink { get; set; }
        public bool? IsGrouped { get; set; }
        public bool? IsAmountMatch { get; set; }
        // Missing amount range conditions (IsGrouped must be true for this to apply)
        public decimal? MissingAmountMin { get; set; }
        public decimal? MissingAmountMax { get; set; }
        // 'C' (credit), 'D' (debit), or '*'
        public string Sign { get; set; } = "*";

        // New DWINGS-related input conditions
        // MT status condition: Wildcard (don't check), Acked, NotAcked, or Null
        public MtStatusCondition MTStatus { get; set; } = MtStatusCondition.Wildcard;
        // True when COMM_ID_EMAIL flag is set on the DWINGS invoice
        public bool? CommIdEmail { get; set; }
        // True when DWINGS invoice status is "INITIATED"
        public bool? BgiStatusInitiated { get; set; }

        public string InvoiceStatus { get; set; }      

        // T_PAYMENT_REQUEST_STATUS condition: "CANCELLED", "FULLY_EXECUTED", "INITIATED", "REJECTED", "REQUEST_FAILED", "REQUESTED" (semi-colon or comma separated, or '*' for any)
        public string PaymentRequestStatus { get; set; }

        // Time/state conditions
        public bool? TriggerDateIsNull { get; set; }
        public int? DaysSinceTriggerMin { get; set; }
        public int? DaysSinceTriggerMax { get; set; }
        public int? OperationDaysAgoMin { get; set; }
        public int? OperationDaysAgoMax { get; set; }
        public bool? IsMatched { get; set; }
        public bool? HasManualMatch { get; set; }
        public bool? IsFirstRequest { get; set; }
        public bool? IsNewLine { get; set; }
        public int? DaysSinceReminderMin { get; set; }
        public int? DaysSinceReminderMax { get; set; }

        // Current state conditions
        // Semi-colon or comma separated action IDs (e.g., "1;3;7"). '*' for any or null to not filter.
        public string CurrentActionId { get; set; }
        // null => don't care; true => action must be DONE; false => action must be PENDING
        public bool? IsActionDone { get; set; }

        // Outputs
        public int? OutputActionId { get; set; }
        public int? OutputKpiId { get; set; }
        public int? OutputIncidentTypeId { get; set; }
        public bool? OutputRiskyItem { get; set; }
        public int? OutputReasonNonRiskyId { get; set; }
        public bool? OutputToRemind { get; set; }
        public int? OutputToRemindDays { get; set; }
        // New: set ActionStatus (true = DONE, false = PENDING). Null => leave as-is (defaults apply)
        public bool? OutputActionDone { get; set; }
        // New: set FirstClaimDate to today when true (self only)
        public bool? OutputFirstClaimToday { get; set; }
        public ApplyTarget ApplyTo { get; set; } = ApplyTarget.Self;
        public bool AutoApply { get; set; } = true;
        public string Message { get; set; }
        // If set, this Edit-scope rule only fires when the edited field matches (e.g. "Linking", "ActionStatus").
        // Null or empty => fires on any edit (legacy behaviour).
        public string TriggerOnField { get; set; }

        /// <summary>
        /// If true (default), fields edited manually by a user within the protection window
        /// (see <see cref="UserEditLockDays"/>) cannot be overwritten by this rule.
        /// Set to false for "system of record" rules that must overrule users.
        /// </summary>
        public bool RespectUserEdits { get; set; } = true;

        /// <summary>
        /// Number of days during which a user-edited field is locked from rule overwrite.
        /// Null or &lt;=0 falls back to the global default (7 days).
        /// </summary>
        public int? UserEditLockDays { get; set; } = 7;

        /// <summary>
        /// Apply (default) or Propose. When Propose, the matching rule creates a pending suggestion
        /// in T_RuleProposals instead of mutating the reconciliation.
        /// </summary>
        public RuleMode Mode { get; set; } = RuleMode.Apply;
    }

    /// <summary>
    /// Minimal evaluation context provided to the engine.
    /// </summary>
    public class RuleContext
    {
        public string CountryId { get; set; }
        public bool IsPivot { get; set; }
        public string GuaranteeType { get; set; } // e.g., ISSUANCE/REISSUANCE/ADVISING
        public string TransactionType { get; set; } // enum name from Services.Enums.TransactionType
        public bool? HasDwingsLink { get; set; }
        public bool? IsGrouped { get; set; }
        public bool? IsAmountMatch { get; set; }
        public decimal? MissingAmount { get; set; } // Discrepancy when grouped (Receivable + Pivot)
        public string Sign { get; set; } // 'C' or 'D'
        public string Bgi { get; set; } // DWINGS_InvoiceID

        // New DWINGS-derived inputs
        public string MtStatus { get; set; } // Raw MT status from DWINGS ("ACKED", "NOT_ACKED", null, etc.)
        public bool? HasCommIdEmail { get; set; }
        public bool? IsBgiInitiated { get; set; }

        // Extended inputs for time/state rules
        public bool? TriggerDateIsNull { get; set; }
        public int? DaysSinceTrigger { get; set; }
        public int? OperationDaysAgo { get; set; }
        public bool? IsMatched { get; set; }
        public bool? HasManualMatch { get; set; }
        public bool? IsFirstRequest { get; set; }
        public bool? IsNewLine { get; set; }
        public int? DaysSinceReminder { get; set; }
        public int? CurrentActionId { get; set; }
        public bool? IsActionDone { get; set; }

        public string InvoiceStatus { get; set; }      

        public string PaymentRequestStatus { get; set; } // T_PAYMENT_REQUEST_STATUS from DWINGS invoice

        // Set by the UI to indicate which field was just edited (e.g. "Action", "ActionStatus", "Linking").
        // Used by TriggerOnField filtering in the engine.
        public string EditedField { get; set; }

        /// <summary>
        /// Pipe-separated list of field names the user has manually edited on this reconciliation
        /// (e.g. "Action|KPI"). Consumed by <see cref="Rules.RuleApplicationHelper"/> to enforce
        /// the per-field user-edit lock.
        /// </summary>
        public string UserEditedFields { get; set; }

        /// <summary>
        /// Timestamp of the last user edit (UI). Null if the row has never been edited by a user.
        /// Used together with <see cref="Rules.TruthRule.UserEditLockDays"/> to compute the lock window.
        /// </summary>
        public DateTime? LastModifiedByUser { get; set; }
    }

    public class RuleEvaluationResult
    {
        public TruthRule Rule { get; set; }
        public int? NewActionIdSelf { get; set; }
        public int? NewKpiIdSelf { get; set; }
        public int? NewIncidentTypeIdSelf { get; set; }
        public bool? NewRiskyItemSelf { get; set; }
        public int? NewReasonNonRiskyIdSelf { get; set; }
        public bool? NewToRemindSelf { get; set; }
        public int? NewToRemindDaysSelf { get; set; }
        public bool? NewActionStatusSelf { get; set; }
        // New: set FirstClaimDate to today when true
        public List<(string ReconciliationId, int? ActionId, int? KpiId)> CounterpartUpdates { get; set; } = new List<(string, int?, int?)>();
        public bool RequiresUserConfirm { get; set; }
        public string UserMessage { get; set; }

        // 0 = PENDING, 1 = DONE (colonne OutputActionDone)
        public int? NewActionDoneSelf { get; set; }

        // false = ignore, true = today (colonne OutputFirstClaimToday)
        public bool? NewFirstClaimTodaySelf { get; set; }
    }
}
