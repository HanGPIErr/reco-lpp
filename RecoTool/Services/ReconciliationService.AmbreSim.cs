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
            CancellationToken ct = default)
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

            // --- 3) Warm caches once (DWINGS used by rule context). ---
            progress?.Report(("Initializing DWINGS caches…", 55));
            await EnsureDwingsCachesInitializedAsync().ConfigureAwait(false);

            // --- 4) Evaluate rules for every transformed row. ---
            // We do NOT create/persist Reconciliation rows. For each DataAmbre, we look up the
            // existing Reconciliation (if any) for the lock/audit metadata; otherwise we stub a
            // blank Reconciliation so the rule can still see default values.
            progress?.Report(("Evaluating rules…", 60));
            int total = transformed.Count;
            int done = 0;
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
                        UserMessage = res?.UserMessage
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
    }
}
