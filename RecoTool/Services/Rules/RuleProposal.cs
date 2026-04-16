using System;

namespace RecoTool.Services.Rules
{
    /// <summary>
    /// Status lifecycle for a rule proposal.
    /// </summary>
    public enum ProposalStatus
    {
        Pending,
        Accepted,
        Rejected,
        Applied,
        /// <summary>
        /// The context has changed since the proposal was created (e.g. a field was edited by another user,
        /// or the DWINGS data evolved so the rule no longer matches). A stale proposal should not be applied
        /// without re-evaluation.
        /// </summary>
        Stale
    }

    /// <summary>
    /// One pending suggestion emitted by a rule with <see cref="TruthRule.Mode"/> = <see cref="RuleMode.Propose"/>.
    /// Proposals live in T_RuleProposals and are displayed in the Rules Health Center so a human can accept/reject.
    /// </summary>
    public class RuleProposal
    {
        public int? ProposalId { get; set; }            // AUTOINCREMENT PK, null before insert
        public string RecoId { get; set; }              // FK → T_Reconciliation.ID
        public string RuleId { get; set; }              // rule that produced the proposal
        public string Field { get; set; }               // Action | KPI | IncidentType | RiskyItem | ReasonNonRisky | ToRemind | ToRemindDate | FirstClaimDate | ActionStatus
        public string OldValue { get; set; }            // value before (rendered as string)
        public string NewValue { get; set; }            // value proposed by the rule (rendered as string)
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
        public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
        public string DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public DateTime? DeleteDate { get; set; }       // for offline-first sync

        /// <summary>
        /// Short human-readable summary used in list / toast displays.
        /// </summary>
        public string Summary => $"{Field}: {OldValue ?? "(null)"} → {NewValue ?? "(null)"}";

        public string StatusBadge => Status.ToString().ToUpperInvariant();
    }
}
