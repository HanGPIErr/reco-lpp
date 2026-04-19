using System;

namespace RecoTool.Services
{
    /// <summary>
    /// Event surface of <see cref="ReconciliationService"/>.
    /// Hosts the <see cref="ReconciliationService.RuleApplied"/> event that diagnostics windows
    /// (Rules Health, Rule Debug) subscribe to, plus the event-args payload and the internal
    /// raise helper used by the rules engine.
    /// </summary>
    public partial class ReconciliationService
    {
        /// <summary>
        /// Payload describing a single rule application, whether the rule fired during an
        /// <b>import</b> (Ambre ingestion), an interactive <b>edit</b> (user saved a reconciliation),
        /// or a manual <b>run-now</b> invocation from the admin tools.
        /// </summary>
        public sealed class RuleAppliedEventArgs : EventArgs
        {
            /// <summary>"import", "edit" or "run-now".</summary>
            public string Origin { get; set; }
            public string CountryId { get; set; }
            public string ReconciliationId { get; set; }
            public string RuleId { get; set; }
            /// <summary>JSON of the outputs produced by the rule.</summary>
            public string Outputs { get; set; }
            /// <summary>Human-readable message (rule label, note, or error if the apply failed).</summary>
            public string Message { get; set; }
        }

        /// <summary>
        /// Fired each time the rules engine successfully applies a rule to a reconciliation row.
        /// Diagnostics UIs subscribe to aggregate activity; production code should not rely on it
        /// for correctness (it is purely informational).
        /// </summary>
        public event EventHandler<RuleAppliedEventArgs> RuleApplied;

        /// <summary>
        /// Internal helper used by the rules engine integration points. Swallows subscriber
        /// exceptions so a buggy listener cannot break the import/edit pipeline.
        /// </summary>
        private void RaiseRuleApplied(string origin, string countryId, string recoId, string ruleId, string outputs, string message)
        {
            try
            {
                RuleApplied?.Invoke(this, new RuleAppliedEventArgs
                {
                    Origin = origin,
                    CountryId = countryId,
                    ReconciliationId = recoId,
                    RuleId = ruleId,
                    Outputs = outputs,
                    Message = message,
                });
            }
            catch { /* never propagate listener failures */ }
        }
    }
}
