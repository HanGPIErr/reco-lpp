using System;
using System.Collections.Generic;

namespace RecoTool.Services.DTOs
{
    /// <summary>
    /// Type of operation that started a change journal run.
    /// Persisted as a string in T_ImportRun.Kind so the enum values are meaningful/stable.
    /// </summary>
    public enum RunKind
    {
        /// <summary>Full AMBRE file import (add/update/archive + rule application).</summary>
        AmbreImport,
        /// <summary>DWINGS snapshot refresh (linking changes only, no AMBRE mutation).</summary>
        DwingsRefresh,
        /// <summary>Bulk user edit batch (e.g. multi-row action assignment from the grid).</summary>
        UserBatch,
    }

    /// <summary>
    /// Type of an individual journal entry. Stored as a string in
    /// T_ReconciliationChangeJournal.Kind so downstream readers (Excel exports, the UI panel…)
    /// remain forward-compatible if new kinds are introduced later.
    /// </summary>
    public enum ChangeKind
    {
        /// <summary>A new AMBRE row was inserted by the import.</summary>
        AmbreAdded,
        /// <summary>An existing AMBRE row had at least one material field changed.</summary>
        AmbreUpdated,
        /// <summary>An AMBRE row was logically deleted (DeleteDate set).</summary>
        AmbreArchived,
        /// <summary>A truth-table rule fired and mutated reconciliation fields on the row.</summary>
        RuleApplied,
        /// <summary>DWINGS reference was resolved/linked onto the row.</summary>
        DwingsLinked,
        /// <summary>DWINGS invoice or guarantee data backing a linked row has changed.</summary>
        DwingsChanged,
        /// <summary>A user edited a field manually from the UI.</summary>
        UserEdit,
        /// <summary>Atomic field diff (OldValue/NewValue) emitted alongside an Updated/RuleApplied kind.</summary>
        FieldChanged,
    }

    /// <summary>
    /// Summary row from <c>T_ImportRun</c>. Projection used by the "Recent activity" panel.
    /// </summary>
    public sealed class ImportRunSummary
    {
        public Guid ImportRunId { get; set; }
        public string CountryId { get; set; }
        public RunKind Kind { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public string TriggeredBy { get; set; }
        public string SourceFiles { get; set; }
        public int NewCount { get; set; }
        public int UpdatedCount { get; set; }
        public int ArchivedCount { get; set; }
        public int RulesAppliedCount { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>Total rows touched (new + updated + archived), pre-computed for UI bindings.</summary>
        public int TouchedRows => NewCount + UpdatedCount + ArchivedCount;
    }

    /// <summary>
    /// Single row from <c>T_ReconciliationChangeJournal</c>. One instance per atomic mutation.
    /// Both the UI row-hover tooltip and the full-history popup bind to this shape.
    /// </summary>
    public sealed class RowChange
    {
        public Guid Id { get; set; }
        public Guid ImportRunId { get; set; }
        public string CountryId { get; set; }
        public string RowId { get; set; }
        public ChangeKind Kind { get; set; }

        /// <summary>Free-form origin tag: "Import", "Rule:R_XYZ", "User:gbenard", etc.</summary>
        public string Source { get; set; }
        public string FieldName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Context { get; set; }
        public DateTime TimestampUtc { get; set; }
    }

    /// <summary>
    /// Aggregated impact of a single run, pre-computed server-side so the panel can render
    /// without further round-trips. Each group carries the matching row IDs so the panel can
    /// push an <c>ID IN (…)</c> filter to the grid without another SELECT.
    /// </summary>
    public sealed class RunImpactReport
    {
        public ImportRunSummary Run { get; set; }
        public List<RuleImpact> RulesFired { get; } = new List<RuleImpact>();
        public List<FieldImpact> MaterialChanges { get; } = new List<FieldImpact>();
        public List<AlertImpact> Alerts { get; } = new List<AlertImpact>();
    }

    /// <summary>How many times a given rule fired during the run, and on which rows.</summary>
    public sealed class RuleImpact
    {
        public string RuleId { get; set; }
        public int Count { get; set; }
        public List<string> RowIds { get; } = new List<string>();
    }

    /// <summary>How many rows had a given material field mutated during the run.</summary>
    public sealed class FieldImpact
    {
        public string FieldName { get; set; }
        public int Count { get; set; }
        public List<string> RowIds { get; } = new List<string>();
    }

    /// <summary>
    /// Semantic alert computed from the raw journal (e.g. "flipped to RiskyItem", "set to Investigate").
    /// Pre-aggregated so the UI does not have to know the business rules for detection.
    /// </summary>
    public sealed class AlertImpact
    {
        public string Label { get; set; }
        public int Count { get; set; }
        public List<string> RowIds { get; } = new List<string>();
    }
}
