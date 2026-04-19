using System;
using System.Linq;
using System.Threading.Tasks;
using RecoTool.Models;
using RecoTool.Services.Rules;

namespace RecoTool.Services
{
    /// <summary>
    /// Thin delegation surface on <see cref="ReconciliationService"/> that forwards calls to
    /// specialised collaborators:
    /// <list type="bullet">
    /// <item><see cref="ReconciliationMatchingService"/> — automatic matching + manual outgoing rule.</item>
    /// <item><see cref="RuleContextBuilder"/> — truth-table context assembly used by the rules engine.</item>
    /// </list>
    /// Kept as instance-lazy singletons so they share <c>this</c>, the offline-first service,
    /// and the current user without re-injecting them at each call site.
    /// </summary>
    public partial class ReconciliationService
    {
        // ── Automatic matching (delegated to ReconciliationMatchingService) ──────────────────────
        private ReconciliationMatchingService _matchingService;
        private ReconciliationMatchingService MatchingService
            => _matchingService ?? (_matchingService = new ReconciliationMatchingService(this, _offlineFirstService, _currentUser));

        /// <summary>Runs the automatic matching pipeline for the given country.</summary>
        public Task<int> PerformAutomaticMatchingAsync(string countryId)
            => MatchingService.PerformAutomaticMatchingAsync(countryId, _countries);

        /// <summary>Applies the manual-outgoing rule pass on the given country.</summary>
        public Task<int> ApplyManualOutgoingRuleAsync(string countryId)
            => MatchingService.ApplyManualOutgoingRuleAsync(countryId, _countries);

        // ── Truth-table helpers (delegated to RuleContextBuilder) ────────────────────────────────
        private RuleContextBuilder _ruleContextBuilder;
        private RuleContextBuilder RuleContextBuilderInstance
            => _ruleContextBuilder ?? (_ruleContextBuilder = new RuleContextBuilder(this, _offlineFirstService));

        /// <summary>
        /// Builds a <see cref="RuleContext"/> for the given Ambre/Reconciliation pair, routed through
        /// the shared <see cref="RuleContextBuilder"/>.
        /// </summary>
        private Task<RuleContext> BuildRuleContextAsync(DataAmbre a, Reconciliation r, Country country, string countryId, bool isPivot, bool? isGrouped = null, bool? isAmountMatch = null)
            => RuleContextBuilderInstance.BuildAsync(a, r, country, countryId, isPivot, isGrouped, isAmountMatch);

        /// <summary>
        /// Fetches a single live Ambre row by primary key using the per-country Ambre database.
        /// Returns <c>null</c> when the country has no database configured, when the ID is missing,
        /// or on any OleDb failure. Used by rule context builders that need the full source row.
        /// </summary>
        private async Task<DataAmbre> GetAmbreRowByIdAsync(string countryId, string id)
        {
            if (string.IsNullOrWhiteSpace(countryId) || string.IsNullOrWhiteSpace(id)) return null;
            try
            {
                var ambreCs = _offlineFirstService?.GetAmbreConnectionString(countryId);
                if (string.IsNullOrWhiteSpace(ambreCs)) return null;
                var list = await _queryExecutor.QueryAsync<DataAmbre>(
                    "SELECT TOP 1 * FROM T_Data_Ambre WHERE ID = ? AND DeleteDate IS NULL",
                    ambreCs,
                    id).ConfigureAwait(false);
                return list?.FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
