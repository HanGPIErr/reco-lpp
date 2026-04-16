using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;
using RecoTool.Infrastructure.Logging;
using RecoTool.Services;

namespace RecoTool.Services.Rules
{
    /// <summary>
    /// Diagnostics orchestrator for the rules engine:
    ///   - dry-run Simulator (predict rule impact on current dataset)
    ///   - Coverage Report (historical stats from %AppData%/RecoTool/rules-*.log)
    ///   - Impact Preview (diff a draft rule against current rule-set)
    ///   - Rule Tester (evaluate a hand-crafted context)
    ///
    /// Never mutates the rules table or the reconciliation table: read-only diagnostics.
    /// </summary>
    public class RulesDiagnosticsService
    {
        private readonly ReconciliationService _reconciliationService;
        private readonly TruthTableRepository _ruleRepository;

        public RulesDiagnosticsService(ReconciliationService reconciliationService, TruthTableRepository ruleRepository)
        {
            _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
        }

        #region Simulator

        /// <summary>
        /// Runs all rules in memory against the current dataset for the active country and
        /// aggregates the results per rule. Does NOT write to the DB.
        /// </summary>
        public async Task<SimulationReport> SimulateAsync(
            RuleScope scope,
            IProgress<(int done, int total)> progress = null,
            CancellationToken ct = default)
        {
            var rules = await _ruleRepository.LoadRulesAsync(ct).ConfigureAwait(false) ?? new List<TruthRule>();
            var rows = await _reconciliationService.SimulateRulesAsync(ids: null, scope: scope, progress: progress, ct: ct).ConfigureAwait(false);

            var byRule = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId))
                .GroupBy(r => r.MatchedRuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Enrich with historical last-applied dates (from log files)
            Dictionary<string, DateTime> lastAppliedByRule = null;
            try
            {
                var coverage = await LoadCoverageAsync(90, origin: null, country: null, ct: ct).ConfigureAwait(false);
                lastAppliedByRule = coverage?.PerRule?
                    .Where(s => s.LastApplied.HasValue)
                    .ToDictionary(s => s.RuleId, s => s.LastApplied.Value, StringComparer.OrdinalIgnoreCase);
            }
            catch { /* non-blocking */ }

            var hits = new List<RuleHitStats>();
            foreach (var rule in rules.OrderBy(r => r.Priority).ThenBy(r => r.RuleId))
            {
                var matched = byRule.TryGetValue(rule.RuleId ?? string.Empty, out var list) ? list : new List<RuleSimulationRow>();
                DateTime? lastApplied = null;
                if (lastAppliedByRule != null && rule.RuleId != null && lastAppliedByRule.TryGetValue(rule.RuleId, out var ts))
                    lastApplied = ts;
                hits.Add(new RuleHitStats
                {
                    RuleId = rule.RuleId,
                    Priority = rule.Priority,
                    Scope = rule.Scope,
                    Enabled = rule.Enabled,
                    AutoApply = rule.AutoApply,
                    Message = rule.Message,
                    HitCount = matched.Count,
                    CoveragePercent = rows.Count == 0 ? 0.0 : (100.0 * matched.Count / rows.Count),
                    SampleRecoIds = matched.Take(10).Select(m => m.ReconciliationId).ToList(),
                    LastHistoricalApplication = lastApplied,
                    Applicable = rule.Scope == RuleScope.Both || rule.Scope == scope
                });
            }

            return new SimulationReport
            {
                SimulatedAt = DateTime.Now,
                Scope = scope,
                TotalRows = rows.Count,
                MatchedRows = rows.Count(r => !string.IsNullOrWhiteSpace(r.MatchedRuleId)),
                UnmatchedRows = rows.Count(r => string.IsNullOrWhiteSpace(r.MatchedRuleId)),
                SampleUnmatchedRecoIds = rows.Where(r => string.IsNullOrWhiteSpace(r.MatchedRuleId))
                                             .Take(20).Select(r => r.ReconciliationId).ToList(),
                RuleHits = hits,
                RawRows = rows
            };
        }

        #endregion

        #region Coverage (historical)

        /// <summary>
        /// Reads %AppData%/RecoTool/rules-*.log for the last N days and aggregates stats.
        /// </summary>
        public Task<CoverageReport> LoadCoverageAsync(int lastNDays, string origin = null, string country = null, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                var report = new CoverageReport
                {
                    PeriodStart = DateTime.Today.AddDays(-Math.Max(0, lastNDays)),
                    PeriodEnd = DateTime.Today,
                    PerRule = new List<RuleLogStats>(),
                    Warnings = new List<string>()
                };

                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecoTool");
                    if (!Directory.Exists(dir)) return report;

                    var statsByRule = new Dictionary<string, RuleLogStats>(StringComparer.OrdinalIgnoreCase);
                    int total = 0;

                    // rules-YYYYMMDD.log
                    foreach (var file in Directory.EnumerateFiles(dir, "rules-*.log"))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(file);
                        // parse date suffix
                        var suffix = name.Length > 6 ? name.Substring(name.Length - 8) : null;
                        if (!DateTime.TryParseExact(suffix, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
                            continue;
                        if (fileDate.Date < report.PeriodStart) continue;
                        if (fileDate.Date > report.PeriodEnd) continue;

                        string[] lines;
                        try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                        catch { continue; }

                        foreach (var line in lines)
                        {
                            // Columns: ts, user, origin, country, recoId, ruleId, outputs, message
                            var parts = line.Split('\t');
                            if (parts.Length < 6) continue;
                            var tsStr = parts[0];
                            var ori = parts[2];
                            var cty = parts[3];
                            var ruleId = parts[5];
                            if (string.IsNullOrWhiteSpace(ruleId)) continue;
                            if (!string.IsNullOrWhiteSpace(origin) && !string.Equals(origin, ori, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!string.IsNullOrWhiteSpace(country) && !string.Equals(country, cty, StringComparison.OrdinalIgnoreCase)) continue;

                            if (!statsByRule.TryGetValue(ruleId, out var st))
                            {
                                st = new RuleLogStats { RuleId = ruleId, ByCountry = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) };
                                statsByRule[ruleId] = st;
                            }
                            st.Total++;
                            total++;

                            if (string.Equals(ori, "import", StringComparison.OrdinalIgnoreCase)) st.Import++;
                            else if (string.Equals(ori, "edit", StringComparison.OrdinalIgnoreCase)) st.Edit++;
                            else if (string.Equals(ori, "run-now", StringComparison.OrdinalIgnoreCase)) st.RunNow++;
                            else st.Other++;

                            if (!string.IsNullOrWhiteSpace(cty))
                            {
                                if (st.ByCountry.ContainsKey(cty)) st.ByCountry[cty]++;
                                else st.ByCountry[cty] = 1;
                            }

                            if (DateTime.TryParseExact(tsStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                            {
                                if (!st.LastApplied.HasValue || ts > st.LastApplied.Value)
                                    st.LastApplied = ts;
                            }
                        }
                    }

                    report.TotalApplications = total;
                    report.PerRule = statsByRule.Values
                        .OrderByDescending(s => s.Total)
                        .ToList();

                    // Diagnostics
                    if (total > 0)
                    {
                        var fallback = report.PerRule.FirstOrDefault(s => string.Equals(s.RuleId, "FALLBACK_INVESTIGATE", StringComparison.OrdinalIgnoreCase));
                        if (fallback != null && fallback.Total * 100.0 / total >= 15.0)
                            report.Warnings.Add($"⚠ FALLBACK_INVESTIGATE fired {fallback.Total} times ({fallback.Total * 100.0 / total:F1}%) — many rows have no matching rule.");
                    }

                    // Dead rules: loaded from current rule-set but not present in log
                    try
                    {
                        var rules = _ruleRepository.LoadRulesAsync(ct).GetAwaiter().GetResult() ?? new List<TruthRule>();
                        foreach (var r in rules.Where(x => x.Enabled))
                        {
                            if (!statsByRule.ContainsKey(r.RuleId ?? string.Empty))
                                report.Warnings.Add($"⚠ Dead rule: '{r.RuleId}' never fired in the last {lastNDays} days.");
                        }
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadCoverageAsync failed: {ex.Message}");
                }
                return report;
            }, ct);
        }

        #endregion

        #region Impact Preview

        /// <summary>
        /// Evaluates the impact of swapping the existing version of a rule with a draft.
        /// Returns the set of reconciliations that would start matching, stop matching, or stay matching.
        /// </summary>
        public async Task<ImpactReport> PreviewImpactAsync(
            TruthRule draft,
            RuleScope scope,
            IProgress<(int done, int total)> progress = null,
            CancellationToken ct = default)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            // 1) Baseline simulation with current rule-set
            var before = await _reconciliationService.SimulateRulesAsync(ids: null, scope: scope, progress: progress, ct: ct).ConfigureAwait(false);

            // 2) Simulation with the draft swapped in (in-memory only).
            //    We do this by temporarily replacing the rule via an isolated RulesEngine instance.
            //    Since we can't inject into _reconciliationService.RulesEngine without side-effects,
            //    we ask the service to evaluate per-row and then re-evaluate targeted rows with the draft.
            //    For correctness (priority-first-match), we simulate the full sequence locally on each row.
            var rules = (await _ruleRepository.LoadRulesAsync(ct).ConfigureAwait(false)) ?? new List<TruthRule>();
            var simulatedRules = rules
                .Where(r => !string.Equals(r.RuleId, draft.RuleId, StringComparison.OrdinalIgnoreCase))
                .Concat(new[] { draft })
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.RuleId)
                .ToList();

            // Build a transient in-memory engine using a fake repository that returns simulatedRules
            var transientEngine = new InMemoryRulesEngine(simulatedRules);

            // Re-run evaluation locally for every row. We need their RuleContext — cheapest way
            // is to re-query per row through _reconciliationService.
            var draftIds = before.Select(r => r.ReconciliationId).ToList();
            var after = new List<RuleSimulationRow>(draftIds.Count);

            // We still need a RuleContext per row. We reuse SimulateRulesAsync but overriding the engine locally.
            // Since the public service API doesn't accept a custom engine, we build contexts by asking the
            // service for debug info per row (already exposes RuleContext), then evaluate via the transient engine.
            int done = 0;
            int total = draftIds.Count;
            foreach (var id in draftIds)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var dbg = await _reconciliationService.GetRuleDebugInfoAsync(id).ConfigureAwait(false);
                    var ctx = dbg.Context;
                    if (ctx == null) continue;
                    var res = transientEngine.Evaluate(ctx, scope);
                    after.Add(new RuleSimulationRow
                    {
                        ReconciliationId = id,
                        MatchedRuleId = res?.Rule?.RuleId,
                        MatchedRulePriority = res?.Rule?.Priority,
                        ProposedActionId = res?.NewActionIdSelf,
                        ProposedKpiId = res?.NewKpiIdSelf,
                        ApplyTo = res?.Rule?.ApplyTo,
                        UserMessage = res?.UserMessage
                    });
                }
                catch { }
                finally
                {
                    done++;
                    try { progress?.Report((done, total)); } catch { }
                }
            }

            var beforeById = before.ToDictionary(x => x.ReconciliationId, x => x, StringComparer.OrdinalIgnoreCase);
            var afterById = after.ToDictionary(x => x.ReconciliationId, x => x, StringComparer.OrdinalIgnoreCase);

            var newlyMatching = new List<string>();
            var noLongerMatching = new List<string>();
            var changedOutputs = new List<string>();

            foreach (var id in beforeById.Keys)
            {
                var b = beforeById[id];
                afterById.TryGetValue(id, out var a);
                bool bMatches = string.Equals(b?.MatchedRuleId, draft.RuleId, StringComparison.OrdinalIgnoreCase);
                bool aMatches = string.Equals(a?.MatchedRuleId, draft.RuleId, StringComparison.OrdinalIgnoreCase);
                if (!bMatches && aMatches) newlyMatching.Add(id);
                else if (bMatches && !aMatches) noLongerMatching.Add(id);
                else if (bMatches && aMatches)
                {
                    if (b.ProposedActionId != a.ProposedActionId || b.ProposedKpiId != a.ProposedKpiId)
                        changedOutputs.Add(id);
                }
            }

            return new ImpactReport
            {
                DraftRuleId = draft.RuleId,
                Scope = scope,
                TotalRows = before.Count,
                BeforeMatchCount = before.Count(r => string.Equals(r.MatchedRuleId, draft.RuleId, StringComparison.OrdinalIgnoreCase)),
                AfterMatchCount = after.Count(r => string.Equals(r.MatchedRuleId, draft.RuleId, StringComparison.OrdinalIgnoreCase)),
                NewlyMatchingIds = newlyMatching,
                NoLongerMatchingIds = noLongerMatching,
                ChangedOutputsIds = changedOutputs
            };
        }

        #endregion

        #region Rule Tester

        /// <summary>
        /// Evaluate a hand-crafted RuleContext against the current rule-set in debug mode.
        /// </summary>
        public async Task<List<RuleDebugEvaluation>> TestAsync(RuleContext ctx, RuleScope scope, CancellationToken ct = default)
        {
            if (ctx == null) return new List<RuleDebugEvaluation>();
            var rules = (await _ruleRepository.LoadRulesAsync(ct).ConfigureAwait(false)) ?? new List<TruthRule>();
            var engine = new InMemoryRulesEngine(rules);
            return engine.EvaluateAll(ctx, scope);
        }

        #endregion
    }

    #region Simple data-carrier classes (kept in the same file to limit file-count churn)

    public class RuleSimulationRow
    {
        public string ReconciliationId { get; set; }
        public bool IsPivot { get; set; }
        public string MatchedRuleId { get; set; }
        public int? MatchedRulePriority { get; set; }
        public int? ProposedActionId { get; set; }
        public int? ProposedKpiId { get; set; }
        public ApplyTarget? ApplyTo { get; set; }
        public int? CurrentActionId { get; set; }
        public int? CurrentKpiId { get; set; }
        public string UserMessage { get; set; }

        // --- Additional fields populated by AMBRE file simulation (SimulateAmbreImportFromFileAsync).
        // Left null/empty for the legacy in-DB simulation path.
        /// <summary>Account_ID from AMBRE (useful to distinguish Pivot vs Receivable rows in the report).</summary>
        public string Account { get; set; }
        /// <summary>CCY from AMBRE (grouping / display).</summary>
        public string Currency { get; set; }
        /// <summary>Signed amount (import value) — helpful to spot big-impact rows at a glance.</summary>
        public decimal? SignedAmount { get; set; }
        /// <summary>Raw label from AMBRE (diagnostic).</summary>
        public string RawLabel { get; set; }
        /// <summary>
        /// True if the row already exists in the local <c>T_Reconciliation</c>. When false, the simulated
        /// import would INSERT a new row; when true, UPDATE (so any existing user-edit lock applies).
        /// </summary>
        public bool ExistsInDb { get; set; }
        /// <summary>Short summary "Field: old → new" for proposed outputs that differ from the current state.</summary>
        public string ChangesSummary { get; set; }
        /// <summary>True when <see cref="MatchedRuleId"/> is non-null AND at least one proposed output differs from current.</summary>
        public bool WouldMutate { get; set; }
    }

    public class RuleHitStats
    {
        public string RuleId { get; set; }
        public int Priority { get; set; }
        public RuleScope Scope { get; set; }
        public bool Enabled { get; set; }
        public bool AutoApply { get; set; }
        public string Message { get; set; }
        public int HitCount { get; set; }
        public double CoveragePercent { get; set; }
        public List<string> SampleRecoIds { get; set; } = new List<string>();
        public DateTime? LastHistoricalApplication { get; set; }
        public bool Applicable { get; set; }
        public bool IsDead => HitCount == 0;
        public string CoverageDisplay => $"{CoveragePercent:F1}%";
        public string LastAppliedDisplay => LastHistoricalApplication.HasValue ? LastHistoricalApplication.Value.ToString("yyyy-MM-dd") : "—";
        public string StatusBadge => !Enabled ? "DISABLED" : IsDead ? "DEAD" : "ACTIVE";
    }

    public class SimulationReport
    {
        public DateTime SimulatedAt { get; set; }
        public RuleScope Scope { get; set; }
        public int TotalRows { get; set; }
        public int MatchedRows { get; set; }
        public int UnmatchedRows { get; set; }
        public List<string> SampleUnmatchedRecoIds { get; set; } = new List<string>();
        public List<RuleHitStats> RuleHits { get; set; } = new List<RuleHitStats>();
        public List<RuleSimulationRow> RawRows { get; set; } = new List<RuleSimulationRow>();

        public double UnmatchedPercent => TotalRows == 0 ? 0 : 100.0 * UnmatchedRows / TotalRows;
        public string Summary => $"Scope={Scope}; {MatchedRows}/{TotalRows} matched ({100.0 - UnmatchedPercent:F1}%); {UnmatchedRows} unmatched ({UnmatchedPercent:F1}%)";
    }

    public class RuleLogStats
    {
        public string RuleId { get; set; }
        public int Total { get; set; }
        public int Import { get; set; }
        public int Edit { get; set; }
        public int RunNow { get; set; }
        public int Other { get; set; }
        public DateTime? LastApplied { get; set; }
        public Dictionary<string, int> ByCountry { get; set; } = new Dictionary<string, int>();
        public string LastAppliedDisplay => LastApplied.HasValue ? LastApplied.Value.ToString("yyyy-MM-dd HH:mm") : "—";
        public string TopCountry => ByCountry == null || ByCountry.Count == 0 ? "—" : string.Join(", ", ByCountry.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    public class CoverageReport
    {
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int TotalApplications { get; set; }
        public List<RuleLogStats> PerRule { get; set; } = new List<RuleLogStats>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class ImpactReport
    {
        public string DraftRuleId { get; set; }
        public RuleScope Scope { get; set; }
        public int TotalRows { get; set; }
        public int BeforeMatchCount { get; set; }
        public int AfterMatchCount { get; set; }
        public List<string> NewlyMatchingIds { get; set; } = new List<string>();
        public List<string> NoLongerMatchingIds { get; set; } = new List<string>();
        public List<string> ChangedOutputsIds { get; set; } = new List<string>();
        public int Delta => AfterMatchCount - BeforeMatchCount;
        public string Summary => $"Before: {BeforeMatchCount} rows | After: {AfterMatchCount} rows (Δ {(Delta >= 0 ? "+" : "")}{Delta})";
    }

    #endregion

    /// <summary>
    /// Lightweight synchronous rule evaluator usable with arbitrary rule lists (for impact preview & tester).
    /// Replicates the matching logic of RulesEngine but without caching and without DB access.
    /// </summary>
    internal class InMemoryRulesEngine
    {
        private readonly List<TruthRule> _rules;

        public InMemoryRulesEngine(IEnumerable<TruthRule> rules)
        {
            _rules = (rules ?? Enumerable.Empty<TruthRule>())
                .OrderBy(r => r?.Priority ?? int.MaxValue)
                .ThenBy(r => r?.RuleId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public RuleEvaluationResult Evaluate(RuleContext ctx, RuleScope scope)
        {
            if (ctx == null) return null;
            foreach (var r in _rules)
            {
                if (r == null || !r.Enabled) continue;
                if (r.Scope != RuleScope.Both && r.Scope != scope) continue;
                if (!AllConditionsMet(r, ctx)) continue;
                return new RuleEvaluationResult
                {
                    Rule = r,
                    NewActionIdSelf = r.OutputActionId,
                    NewKpiIdSelf = r.OutputKpiId,
                    NewIncidentTypeIdSelf = r.OutputIncidentTypeId,
                    NewRiskyItemSelf = r.OutputRiskyItem,
                    NewReasonNonRiskyIdSelf = r.OutputReasonNonRiskyId,
                    NewToRemindSelf = r.OutputToRemind,
                    NewToRemindDaysSelf = r.OutputToRemindDays,
                    NewActionDoneSelf = r.OutputActionDone.HasValue ? (r.OutputActionDone.Value ? 1 : 0) : (int?)null,
                    NewFirstClaimTodaySelf = r.OutputFirstClaimToday,
                    RequiresUserConfirm = !string.IsNullOrWhiteSpace(r.Message),
                    UserMessage = r.Message
                };
            }
            return null;
        }

        public List<RuleDebugEvaluation> EvaluateAll(RuleContext ctx, RuleScope scope)
        {
            var results = new List<RuleDebugEvaluation>();
            if (ctx == null) return results;
            foreach (var r in _rules)
            {
                if (r == null) continue;
                var conditions = DescribeConditions(r, ctx);
                bool allMet = conditions.TrueForAll(c => c.IsMet);
                results.Add(new RuleDebugEvaluation
                {
                    Rule = r,
                    IsEnabled = r.Enabled,
                    Conditions = conditions,
                    IsMatch = r.Enabled && allMet && (r.Scope == RuleScope.Both || r.Scope == scope)
                });
            }
            return results;
        }

        private static bool AllConditionsMet(TruthRule r, RuleContext c)
        {
            foreach (var cd in DescribeConditions(r, c)) if (!cd.IsMet) return false;
            return true;
        }

        private static List<RuleConditionDebug> DescribeConditions(TruthRule r, RuleContext c)
        {
            // Delegate to RulesEngine via reflection? No — duplicate the rules in a minimal way that covers
            // the same fields as RulesEngine.EvaluateConditions (which is private).
            // To stay in sync, we rely on the fact that InMemoryRulesEngine is used only for diagnostics:
            // Tester & Impact. For authoritative matching, the production engine RulesEngine is still used.
            var list = new List<RuleConditionDebug>();

            if (!IsWildcard(r.AccountSide))
            {
                bool needP = r.AccountSide.Equals("P", StringComparison.OrdinalIgnoreCase);
                bool needR = r.AccountSide.Equals("R", StringComparison.OrdinalIgnoreCase);
                list.Add(new RuleConditionDebug { Field = "AccountSide", Expected = r.AccountSide, Actual = c.IsPivot ? "P" : "R", IsMet = (needP && c.IsPivot) || (needR && !c.IsPivot) });
            }
            if (!IsWildcard(r.Booking))
                list.Add(new RuleConditionDebug { Field = "Booking", Expected = r.Booking, Actual = c.CountryId ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.CountryId) && MatchesSet(r.Booking, c.CountryId) });
            if (!IsWildcard(r.GuaranteeType))
                list.Add(new RuleConditionDebug { Field = "GuaranteeType", Expected = r.GuaranteeType, Actual = c.GuaranteeType ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.GuaranteeType) && MatchesSet(r.GuaranteeType, c.GuaranteeType) });
            if (!IsWildcard(r.TransactionType))
                list.Add(new RuleConditionDebug { Field = "TransactionType", Expected = r.TransactionType, Actual = c.TransactionType ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.TransactionType) && MatchesSet(r.TransactionType, c.TransactionType) });
            if (r.HasDwingsLink.HasValue)
                list.Add(new RuleConditionDebug { Field = "HasDwingsLink", Expected = r.HasDwingsLink.Value.ToString(), Actual = c.HasDwingsLink?.ToString() ?? "(null)", IsMet = c.HasDwingsLink == r.HasDwingsLink.Value });
            if (r.IsGrouped.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsGrouped", Expected = r.IsGrouped.Value.ToString(), Actual = c.IsGrouped?.ToString() ?? "(null)", IsMet = c.IsGrouped == r.IsGrouped.Value });
            if (r.IsAmountMatch.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsAmountMatch", Expected = r.IsAmountMatch.Value.ToString(), Actual = c.IsAmountMatch?.ToString() ?? "(null)", IsMet = c.IsAmountMatch == r.IsAmountMatch.Value });
            if (r.MissingAmountMin.HasValue || r.MissingAmountMax.HasValue)
            {
                bool met = c.MissingAmount.HasValue;
                if (met)
                {
                    var a = c.MissingAmount.Value;
                    if (r.MissingAmountMin.HasValue && a < r.MissingAmountMin.Value) met = false;
                    if (r.MissingAmountMax.HasValue && a > r.MissingAmountMax.Value) met = false;
                }
                list.Add(new RuleConditionDebug { Field = "MissingAmount", Expected = $"[{r.MissingAmountMin?.ToString() ?? "∞"}, {r.MissingAmountMax?.ToString() ?? "∞"}]", Actual = c.MissingAmount?.ToString("F2") ?? "(null)", IsMet = met });
            }
            if (!IsWildcard(r.Sign))
                list.Add(new RuleConditionDebug { Field = "Sign", Expected = r.Sign, Actual = c.Sign ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.Sign) && r.Sign.Equals(c.Sign, StringComparison.OrdinalIgnoreCase) });
            if (r.MTStatus != MtStatusCondition.Wildcard)
                list.Add(new RuleConditionDebug { Field = "MTStatus", Expected = r.MTStatus.ToString(), Actual = c.MtStatus ?? "(null)", IsMet = MatchesMtStatus(r.MTStatus, c.MtStatus) });
            if (r.CommIdEmail.HasValue)
                list.Add(new RuleConditionDebug { Field = "CommIdEmail", Expected = r.CommIdEmail.Value.ToString(), Actual = c.HasCommIdEmail?.ToString() ?? "(null)", IsMet = c.HasCommIdEmail.HasValue && c.HasCommIdEmail.Value == r.CommIdEmail.Value });
            if (r.BgiStatusInitiated.HasValue)
                list.Add(new RuleConditionDebug { Field = "BgiStatusInitiated", Expected = r.BgiStatusInitiated.Value.ToString(), Actual = c.IsBgiInitiated?.ToString() ?? "(null)", IsMet = c.IsBgiInitiated.HasValue && c.IsBgiInitiated.Value == r.BgiStatusInitiated.Value });
            if (!IsWildcard(r.PaymentRequestStatus))
                list.Add(new RuleConditionDebug { Field = "PaymentRequestStatus", Expected = r.PaymentRequestStatus, Actual = c.PaymentRequestStatus ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.PaymentRequestStatus) && MatchesSet(r.PaymentRequestStatus, c.PaymentRequestStatus) });
            if (!IsWildcard(r.InvoiceStatus))
                list.Add(new RuleConditionDebug { Field = "InvoiceStatus", Expected = r.InvoiceStatus, Actual = c.InvoiceStatus ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.InvoiceStatus) && MatchesSet(r.InvoiceStatus, c.InvoiceStatus) });
            if (r.TriggerDateIsNull.HasValue)
                list.Add(new RuleConditionDebug { Field = "TriggerDateIsNull", Expected = r.TriggerDateIsNull.Value.ToString(), Actual = c.TriggerDateIsNull?.ToString() ?? "(null)", IsMet = c.TriggerDateIsNull.HasValue && c.TriggerDateIsNull.Value == r.TriggerDateIsNull.Value });
            if (r.DaysSinceTriggerMin.HasValue || r.DaysSinceTriggerMax.HasValue)
            {
                bool met = c.DaysSinceTrigger.HasValue;
                if (met)
                {
                    var v = c.DaysSinceTrigger.Value;
                    if (r.DaysSinceTriggerMin.HasValue && v < r.DaysSinceTriggerMin.Value) met = false;
                    if (r.DaysSinceTriggerMax.HasValue && v > r.DaysSinceTriggerMax.Value) met = false;
                }
                list.Add(new RuleConditionDebug { Field = "DaysSinceTrigger", Expected = $"[{r.DaysSinceTriggerMin}, {r.DaysSinceTriggerMax}]", Actual = c.DaysSinceTrigger?.ToString() ?? "(null)", IsMet = met });
            }
            if (r.OperationDaysAgoMin.HasValue || r.OperationDaysAgoMax.HasValue)
            {
                bool met = c.OperationDaysAgo.HasValue;
                if (met)
                {
                    var v = c.OperationDaysAgo.Value;
                    if (r.OperationDaysAgoMin.HasValue && v < r.OperationDaysAgoMin.Value) met = false;
                    if (r.OperationDaysAgoMax.HasValue && v > r.OperationDaysAgoMax.Value) met = false;
                }
                list.Add(new RuleConditionDebug { Field = "OperationDaysAgo", Expected = $"[{r.OperationDaysAgoMin}, {r.OperationDaysAgoMax}]", Actual = c.OperationDaysAgo?.ToString() ?? "(null)", IsMet = met });
            }
            if (r.IsMatched.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsMatched", Expected = r.IsMatched.Value.ToString(), Actual = c.IsMatched?.ToString() ?? "(null)", IsMet = c.IsMatched == r.IsMatched.Value });
            if (r.HasManualMatch.HasValue)
                list.Add(new RuleConditionDebug { Field = "HasManualMatch", Expected = r.HasManualMatch.Value.ToString(), Actual = c.HasManualMatch?.ToString() ?? "(null)", IsMet = c.HasManualMatch == r.HasManualMatch.Value });
            if (r.IsFirstRequest.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsFirstRequest", Expected = r.IsFirstRequest.Value.ToString(), Actual = c.IsFirstRequest?.ToString() ?? "(null)", IsMet = c.IsFirstRequest == r.IsFirstRequest.Value });
            if (r.IsNewLine.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsNewLine", Expected = r.IsNewLine.Value.ToString(), Actual = c.IsNewLine?.ToString() ?? "(null)", IsMet = c.IsNewLine == r.IsNewLine.Value });
            if (r.DaysSinceReminderMin.HasValue || r.DaysSinceReminderMax.HasValue)
            {
                bool met = c.DaysSinceReminder.HasValue;
                if (met)
                {
                    var v = c.DaysSinceReminder.Value;
                    if (r.DaysSinceReminderMin.HasValue && v < r.DaysSinceReminderMin.Value) met = false;
                    if (r.DaysSinceReminderMax.HasValue && v > r.DaysSinceReminderMax.Value) met = false;
                }
                list.Add(new RuleConditionDebug { Field = "DaysSinceReminder", Expected = $"[{r.DaysSinceReminderMin}, {r.DaysSinceReminderMax}]", Actual = c.DaysSinceReminder?.ToString() ?? "(null)", IsMet = met });
            }
            if (!IsWildcard(r.CurrentActionId))
                list.Add(new RuleConditionDebug { Field = "CurrentActionId", Expected = r.CurrentActionId, Actual = c.CurrentActionId?.ToString() ?? "(null)", IsMet = c.CurrentActionId.HasValue && MatchesSet(r.CurrentActionId, c.CurrentActionId.Value.ToString()) });
            if (r.IsActionDone.HasValue)
                list.Add(new RuleConditionDebug { Field = "IsActionDone", Expected = r.IsActionDone.Value.ToString(), Actual = c.IsActionDone?.ToString() ?? "(null)", IsMet = c.IsActionDone == r.IsActionDone.Value });
            if (!string.IsNullOrWhiteSpace(r.TriggerOnField))
                list.Add(new RuleConditionDebug { Field = "TriggerOnField", Expected = r.TriggerOnField, Actual = c.EditedField ?? "(null)", IsMet = !string.IsNullOrWhiteSpace(c.EditedField) && MatchesSet(r.TriggerOnField, c.EditedField) });

            return list;
        }

        private static bool IsWildcard(string s) => string.IsNullOrWhiteSpace(s) || s.Trim() == "*";

        private static bool MatchesSet(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || expected.Trim() == "*") return true;
            if (string.IsNullOrWhiteSpace(actual)) return false;
            var tokens = expected.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in tokens)
                if (string.Equals(t.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool MatchesMtStatus(MtStatusCondition expected, string actual)
        {
            switch (expected)
            {
                case MtStatusCondition.Wildcard: return true;
                case MtStatusCondition.Acked: return string.Equals(actual, "ACKED", StringComparison.OrdinalIgnoreCase);
                case MtStatusCondition.NotAcked: return string.Equals(actual, "NOT_ACKED", StringComparison.OrdinalIgnoreCase) || string.Equals(actual, "NACKED", StringComparison.OrdinalIgnoreCase);
                case MtStatusCondition.Null: return string.IsNullOrWhiteSpace(actual);
                default: return true;
            }
        }
    }
}
