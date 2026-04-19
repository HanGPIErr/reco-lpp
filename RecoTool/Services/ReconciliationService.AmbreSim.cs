using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Models;
using RecoTool.Services.Ambre;
using RecoTool.Services.DTOs;
using RecoTool.Services.Rules;

namespace RecoTool.Services
{
    // Partial: Ambre-file import simulation.
    // Read-only debug feature that re-runs the import pipeline against an Excel file
    // (the same kind you'd pass to ImportAmbreFile) but never persists anything — it
    // only reports which rules would match and what they would change.
    public partial class ReconciliationService
    {
        /// <summary>
        /// Simulates importing an AMBRE Excel file for <paramref name="countryId"/> and evaluates the
        /// <see cref="RuleScope.Import"/> rules for every transformed row. Produces a list of
        /// <see cref="RuleSimulationRow"/> annotated with import-specific fields (Account, Currency,
        /// SignedAmount, ExistsInDb, ChangesSummary, WouldMutate).
        ///
        /// Read-only: no local / network DB writes. Safe to run while another user is working.
        ///
        /// The expensive steps (Excel read, transformation, DWINGS cache warm-up) are all awaited
        /// asynchronously with a progress callback so the UI can stream feedback.
        /// </summary>
        public async Task<List<RuleSimulationRow>> SimulateAmbreImportFromFileAsync(
            string filePath,
            string countryId,
            IProgress<(string message, int percent)> progress = null,
            CancellationToken ct = default,
            string dwingsFilePath = null)
        {
            var result = new List<RuleSimulationRow>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("AMBRE file not found", filePath);
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId is required", nameof(countryId));
            if (_offlineFirstService == null || _rulesEngine == null)
                return result;
            if (_countries == null || !_countries.TryGetValue(countryId, out var country) || country == null)
                return result;

            // --- 1) Load AMBRE import configuration (fields + transforms) ---
            progress?.Report(("Loading configuration…", 2));
            var configLoader = new AmbreConfigurationLoader(_offlineFirstService);
            await configLoader.EnsureInitializedAsync().ConfigureAwait(false);
            var importResultSink = new ImportResult { CountryId = countryId, StartTime = DateTime.UtcNow };
            var config = await configLoader.LoadConfigurationsAsync(countryId, importResultSink).ConfigureAwait(false);
            if (config == null || importResultSink.Errors.Any())
                throw new InvalidOperationException("Cannot load AMBRE configuration: " + string.Join("; ", importResultSink.Errors));

            // --- 2) Read + filter + transform, exactly like the real import ---
            var dataProcessor = new AmbreDataProcessor(_offlineFirstService, _currentUser);
            dataProcessor.SetConfigurationLoader(configLoader);

            progress?.Report(("Reading Excel file…", 10));
            var rawRows = await dataProcessor.ReadExcelFilesAsync(
                new[] { filePath },
                config.ImportFields,
                isMultiFile: false,
                progressCallback: (msg, p) => progress?.Report((msg, 10 + p / 5))).ConfigureAwait(false);
            if (rawRows == null || rawRows.Count == 0)
                return result;

            progress?.Report(("Filtering by country accounts…", 35));
            var filtered = dataProcessor.FilterRowsByCountryAccounts(rawRows, country);
            if (filtered.Count == 0)
                return result;

            progress?.Report(("Transforming rows…", 45));
            var transformed = await dataProcessor.TransformDataAsync(filtered, config.Transforms, country).ConfigureAwait(false);
            if (transformed.Count == 0)
                return result;

            // --- 3a) If the user provided a DWINGS snapshot, load it into memory and prepare
            //         the async-local override. Otherwise, warm the regular DWINGS cache.
            DwingsOverride dwSnapshot = null;
            if (!string.IsNullOrWhiteSpace(dwingsFilePath))
            {
                progress?.Report(("Loading DWINGS snapshot…", 52));
                var (dwInvoices, dwGuarantees) = await DwingsService.LoadFromPathAsync(dwingsFilePath).ConfigureAwait(false);
                dwSnapshot = new DwingsOverride
                {
                    Invoices = dwInvoices ?? new List<DwingsInvoiceDto>(),
                    Guarantees = dwGuarantees ?? new List<DwingsGuaranteeDto>()
                };
            }
            else
            {
                progress?.Report(("Initializing DWINGS caches…", 55));
                await EnsureDwingsCachesInitializedAsync().ConfigureAwait(false);
            }

            // --- 4) Evaluate rules for every transformed row. ---
            // We do NOT create/persist Reconciliation rows. For each DataAmbre, we look up the
            // existing Reconciliation (if any) for the lock/audit metadata; otherwise we stub a
            // blank Reconciliation so the rule can still see default values.
            progress?.Report(("Evaluating rules…", 60));
            int total = transformed.Count;
            int done = 0;

            // Install the DWINGS override for the whole evaluation loop if the user picked a snapshot.
            // AsyncLocal flows through all awaits below, so BuildRuleContextAsync picks it up transparently.
            using (dwSnapshot != null ? ApplyDwingsOverride(dwSnapshot) : null)
            {
                foreach (var amb in transformed)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        if (amb == null || string.IsNullOrWhiteSpace(amb.ID)) continue;

                        // Try to read the existing reconciliation (without creating one).
                        Reconciliation reco = null;
                        try { reco = await GetReconciliationByIdAsync(countryId, amb.ID).ConfigureAwait(false); } catch { }
                        bool existsInDb = reco != null;
                        if (reco == null)
                        {
                            // New row: stub a blank reconciliation so the rule engine has defaults.
                            reco = new Reconciliation { ID = amb.ID };
                        }

                        bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);
                        var ctx = await BuildRuleContextAsync(amb, reco, country, countryId, isPivot).ConfigureAwait(false);
                        var res = await _rulesEngine.EvaluateAsync(ctx, RuleScope.Import, ct).ConfigureAwait(false);

                        var row = new RuleSimulationRow
                        {
                            ReconciliationId = amb.ID,
                            IsPivot = isPivot,
                            Account = amb.Account_ID,
                            Currency = amb.CCY,
                            SignedAmount = amb.SignedAmount,
                            RawLabel = amb.RawLabel,
                            ExistsInDb = existsInDb,
                            MatchedRuleId = res?.Rule?.RuleId,
                            MatchedRulePriority = res?.Rule?.Priority,
                            ProposedActionId = res?.NewActionIdSelf,
                            ProposedKpiId = res?.NewKpiIdSelf,
                            ApplyTo = res?.Rule?.ApplyTo,
                            CurrentActionId = reco.Action,
                            CurrentKpiId = reco.KPI,
                            UserMessage = res?.UserMessage,
                            // Cache refs for on-demand rule debugging (double-click in the UI).
                            SimulatedAmbre = amb,
                            SimulatedReco = reco,
                            // Shared reference: every row keeps the same snapshot pointer (shallow copy).
                            DwingsSnapshot = dwSnapshot
                        };

                        // Compute change summary + WouldMutate flag based on actual delta vs existing reco.
                        if (res?.Rule != null)
                        {
                            var changes = BuildSimulationChangeSummary(res, reco);
                            row.ChangesSummary = changes;
                            row.WouldMutate = !string.IsNullOrEmpty(changes);
                        }

                        result.Add(row);
                    }
                    catch (Exception rowEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SimulateAmbre] row {amb?.ID} failed: {rowEx.Message}");
                    }
                    finally
                    {
                        done++;
                        if (done % 25 == 0 || done == total)
                        {
                            int pct = 60 + (int)(done * 40.0 / Math.Max(1, total));
                            progress?.Report(($"Evaluating rules… {done}/{total}", Math.Min(99, pct)));
                        }
                    }
                }
            }

            progress?.Report(("Done.", 100));
            return result;
        }

        /// <summary>
        /// Produces a compact human-readable summary of the output fields that would actually change
        /// on <paramref name="reco"/> if <paramref name="res"/> were applied. Empty string when the
        /// rule would be a no-op (all proposed values equal the current ones).
        /// </summary>
        private static string BuildSimulationChangeSummary(RuleEvaluationResult res, Reconciliation reco)
        {
            if (res?.Rule == null || reco == null) return null;
            var parts = new List<string>();

            void AddIntChange(string field, int? proposed, int? current)
            {
                if (!proposed.HasValue) return;
                if (proposed == current) return;
                parts.Add($"{field}: {current?.ToString() ?? "—"} → {proposed}");
            }
            void AddBoolChange(string field, bool? proposed, bool? current)
            {
                if (!proposed.HasValue) return;
                if (proposed == current) return;
                parts.Add($"{field}: {current?.ToString() ?? "—"} → {proposed}");
            }

            AddIntChange("Action", res.NewActionIdSelf, reco.Action);
            AddIntChange("KPI", res.NewKpiIdSelf, reco.KPI);
            AddIntChange("IncidentType", res.NewIncidentTypeIdSelf, reco.IncidentType);
            AddIntChange("ReasonNonRisky", res.NewReasonNonRiskyIdSelf, reco.ReasonNonRisky);
            AddBoolChange("RiskyItem", res.NewRiskyItemSelf, reco.RiskyItem);
            AddBoolChange("ToRemind", res.NewToRemindSelf, reco.ToRemind);
            if (res.NewActionStatusSelf.HasValue)
                AddBoolChange("ActionStatus", res.NewActionStatusSelf, reco.ActionStatus);
            else if (res.NewActionDoneSelf.HasValue)
            {
                bool proposedDone = res.NewActionDoneSelf.Value == 1;
                if (reco.ActionStatus != proposedDone)
                    parts.Add($"ActionStatus: {reco.ActionStatus?.ToString() ?? "—"} → {proposedDone}");
            }
            if (res.NewFirstClaimTodaySelf == true)
            {
                var today = DateTime.Today;
                if (reco.FirstClaimDate.HasValue)
                {
                    if (reco.LastClaimDate != today)
                        parts.Add($"LastClaimDate: {reco.LastClaimDate?.ToString("yyyy-MM-dd") ?? "—"} → {today:yyyy-MM-dd}");
                }
                else
                {
                    parts.Add($"FirstClaimDate: — → {today:yyyy-MM-dd}");
                }
            }
            if (res.NewToRemindDaysSelf.HasValue)
            {
                try
                {
                    var target = DateTime.Today.AddDays(res.NewToRemindDaysSelf.Value);
                    if (reco.ToRemindDate != target)
                        parts.Add($"ToRemindDate: {reco.ToRemindDate?.ToString("yyyy-MM-dd") ?? "—"} → {target:yyyy-MM-dd}");
                }
                catch { }
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        /// <summary>
        /// Re-evaluates every rule against a simulated row (built earlier by
        /// <see cref="SimulateAmbreImportFromFileAsync"/>) so the UI can show a per-condition
        /// debug report on double-click. No DB writes, no cache invalidation.
        /// </summary>
        /// <param name="row">Row produced by the AMBRE-file simulator; must carry SimulatedAmbre.</param>
        /// <param name="countryId">Country in scope (required for the rule context).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>(RuleContext, per-rule debug evaluations) or (null, null) if the row is invalid.</returns>
        public async Task<(RuleContext Context, List<RuleDebugEvaluation> Evaluations)> EvaluateAllRulesForSimulatedRowAsync(
            Rules.RuleSimulationRow row,
            string countryId,
            System.Threading.CancellationToken ct = default)
        {
            if (row?.SimulatedAmbre == null) return (null, null);
            if (_offlineFirstService == null || _rulesEngine == null) return (null, null);
            if (_countries == null || !_countries.TryGetValue(countryId, out var country) || country == null)
                return (null, null);

            var amb = row.SimulatedAmbre;
            var reco = row.SimulatedReco ?? new Reconciliation { ID = amb.ID };
            bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);

            // Re-apply the simulation-time DWINGS snapshot (if any) so the debug view matches what
            // the simulation reported. Without this, BuildRuleContextAsync would fall back to the
            // country's live DW cache and the per-condition report would drift.
            using (row.DwingsSnapshot != null ? ApplyDwingsOverride(row.DwingsSnapshot) : null)
            {
                var ctx = await BuildRuleContextAsync(amb, reco, country, countryId, isPivot).ConfigureAwait(false);
                var evaluations = await _rulesEngine.EvaluateAllForDebugAsync(ctx, RuleScope.Import, ct).ConfigureAwait(false);
                return (ctx, evaluations);
            }
        }
    }
}
