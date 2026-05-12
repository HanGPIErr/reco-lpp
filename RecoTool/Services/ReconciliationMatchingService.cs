using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;
using RecoTool.Domain.Repositories;
using RecoTool.Infrastructure.Logging;
using RecoTool.Infrastructure.Time;
using RecoTool.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RecoTool.Services
{
    /// <summary>
    /// Handles automatic matching logic for reconciliation lines.
    /// Extracted from ReconciliationService to reduce its responsibilities.
    /// </summary>
    public class ReconciliationMatchingService
    {
        private readonly IReconciliationService _reconciliationService;
        private readonly IOfflineFirstService _offlineFirstService;
        private readonly string _currentUser;
        private readonly IClock _clock;
        private readonly ILogger<ReconciliationMatchingService> _logger;
        private readonly IDataAmbreRepository _ambreRepo;

        public ReconciliationMatchingService(IReconciliationService reconciliationService, IOfflineFirstService offlineFirstService, string currentUser, IClock clock = null, ILogger<ReconciliationMatchingService> logger = null, IDataAmbreRepository ambreRepo = null)
        {
            _reconciliationService = reconciliationService ?? throw new ArgumentNullException(nameof(reconciliationService));
            _offlineFirstService = offlineFirstService;
            _currentUser = currentUser;
            _clock = clock ?? SystemClock.Instance;
            _logger = logger ?? NullLogger<ReconciliationMatchingService>.Instance;
            _ambreRepo = ambreRepo; // Optional — falls back to _reconciliationService.GetAmbreDataAsync when null.
        }

        /// <summary>
        /// Loads Ambre rows for the given country. Prefers <see cref="IDataAmbreRepository.GetAllAsync"/>
        /// when the repository is injected (the new abstraction); falls back to
        /// <see cref="IReconciliationService.GetAmbreDataAsync"/> otherwise — keeps the existing
        /// behavior (and unit-test setups) working when no repo is wired in.
        /// </summary>
        private async Task<IReadOnlyList<DataAmbre>> LoadAmbreAsync(string countryId, CancellationToken ct)
        {
            if (_ambreRepo != null)
            {
                var rows = await _ambreRepo.GetAllAsync(countryId, includeDeleted: false, ct).ConfigureAwait(false);
                return rows ?? (IReadOnlyList<DataAmbre>)Array.Empty<DataAmbre>();
            }
            // Legacy path — List<T> implements IReadOnlyList<T>.
            var legacy = await _reconciliationService.GetAmbreDataAsync(countryId).ConfigureAwait(false);
            return legacy ?? (IReadOnlyList<DataAmbre>)Array.Empty<DataAmbre>();
        }

        /// <summary>
        /// Effectue un rapprochement automatique basé sur les références invoice (Receivable ↔ Pivot).
        /// </summary>
        public async Task<int> PerformAutomaticMatchingAsync(string countryId, Dictionary<string, Country> countries, CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var country = countries.ContainsKey(countryId) ? countries[countryId] : null;
                if (country == null) return 0;

                var ambreData = await LoadAmbreAsync(countryId, ct).ConfigureAwait(false);
                var pivotLines = ambreData.Where(d => d.IsPivotAccount(country.CNT_AmbrePivot)).ToList();
                var receivableLines = ambreData.Where(d => d.IsReceivableAccount(country.CNT_AmbreReceivable)).ToList();

                int matchCount = 0;

                foreach (var receivableLine in receivableLines)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(receivableLine.Receivable_InvoiceFromAmbre)) continue;

                    var matchingPivotLines = pivotLines.Where(p =>
                        !string.IsNullOrEmpty(p.Pivot_MbawIDFromLabel) &&
                        p.Pivot_MbawIDFromLabel.Contains(receivableLine.Receivable_InvoiceFromAmbre))
                        .ToList();

                    if (matchingPivotLines.Any())
                    {
                        await CreateMatchingReconciliationsAsync(receivableLine, matchingPivotLines, ct);
                        matchCount++;
                    }
                }

                return matchCount;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new Exception($"Erreur lors du rapprochement automatique: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Applique la règle MANUAL_OUTGOING: trouve les paires de lignes pivot avec même guarantee ID
        /// et montants qui somment à 0, puis les marque comme MATCH / PAID BUT NOT RECONCILED.
        /// </summary>
        public async Task<int> ApplyManualOutgoingRuleAsync(string countryId, Dictionary<string, Country> countries, CancellationToken ct = default)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                ct.ThrowIfCancellationRequested();
                var country = countries.ContainsKey(countryId) ? countries[countryId] : null;
                if (country == null) return 0;

                var allUserFields = _offlineFirstService?.UserFields;
                if (allUserFields == null) return 0;

                var toMatchAction = allUserFields.FirstOrDefault(uf =>
                    string.Equals(uf.USR_Category, "Action", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(uf.USR_FieldName, "MATCH", StringComparison.OrdinalIgnoreCase));
                var payButNotReconciledKpi = allUserFields.FirstOrDefault(uf =>
                    string.Equals(uf.USR_Category, "KPI", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(uf.USR_FieldName, "PAID BUT NOT RECONCILED", StringComparison.OrdinalIgnoreCase));

                if (toMatchAction == null || payButNotReconciledKpi == null)
                {
                    LogManager.Warning("MANUAL_OUTGOING rule: Required Action or KPI not found (MATCH or PAID BUT NOT RECONCILED)");
                    return 0;
                }

                int actionId = toMatchAction.USR_ID;
                int kpiId = payButNotReconciledKpi.USR_ID;

                var ambreData = await LoadAmbreAsync(countryId, ct).ConfigureAwait(false);
                var pivotLines = ambreData.Where(d =>
                    d.IsPivotAccount(country.CNT_AmbrePivot) && !d.IsDeleted).ToList();

                int matchCount = 0;

                // Pre-load all reconciliations for PIVOT lines
                var reconciliations = new Dictionary<string, Reconciliation>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in pivotLines)
                {
                    ct.ThrowIfCancellationRequested();
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(line.ID).ConfigureAwait(false);
                    if (reco != null) reconciliations[line.ID] = reco;
                }

                // Group by guarantee ID
                var linesByGuarantee = new Dictionary<string, List<DataAmbre>>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in pivotLines)
                {
                    if (!reconciliations.TryGetValue(line.ID, out var reco) || reco == null) continue;
                    var guaranteeId = reco.DWINGS_GuaranteeID?.Trim();
                    if (string.IsNullOrWhiteSpace(guaranteeId)) continue;

                    if (!linesByGuarantee.ContainsKey(guaranteeId))
                        linesByGuarantee[guaranteeId] = new List<DataAmbre>();
                    linesByGuarantee[guaranteeId].Add(line);
                }

                // For each guarantee group, find pairs that sum to 0
                foreach (var kvp in linesByGuarantee)
                {
                    ct.ThrowIfCancellationRequested();
                    var guaranteeId = kvp.Key;
                    var lines = kvp.Value;
                    if (lines.Count < 2) continue;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        for (int j = i + 1; j < lines.Count; j++)
                        {
                            var line1 = lines[i];
                            var line2 = lines[j];

                            if (Math.Abs(line1.SignedAmount + line2.SignedAmount) >= 0.01m)
                                continue;

                            if (!reconciliations.TryGetValue(line1.ID, out var reco1) || reco1 == null) continue;
                            if (!reconciliations.TryGetValue(line2.ID, out var reco2) || reco2 == null) continue;

                            reco1.Action = actionId;
                            reco1.KPI = kpiId;
                            reco2.Action = actionId;
                            reco2.KPI = kpiId;

                            AppendComment(reco1, $"Same guarantee Pair detected in pivot => to match in Ambre (GuaranteeID={guaranteeId})");
                            AppendComment(reco2, $"Same guarantee Pair detected in pivot => to match in Ambre (GuaranteeID={guaranteeId})");

                            await _reconciliationService.SaveReconciliationsAsync(new List<Reconciliation> { reco1, reco2 }).ConfigureAwait(false);

                            LogHelper.WriteRuleApplied("MANUAL_OUTGOING", countryId, line1.ID, "GUARANTEE_PAIR",
                                $"Action={actionId}; KPI={kpiId}", $"Matched with {line2.ID} (GuaranteeID={guaranteeId})");
                            LogHelper.WriteRuleApplied("MANUAL_OUTGOING", countryId, line2.ID, "GUARANTEE_PAIR",
                                $"Action={actionId}; KPI={kpiId}", $"Matched with {line1.ID} (GuaranteeID={guaranteeId})");

                            matchCount++;
                        }
                    }
                }

                timer.Stop();
                LogManager.Info($"[PERF] MANUAL_OUTGOING rule completed: {matchCount} pair(s) matched in {timer.ElapsedMilliseconds}ms");
                return matchCount;
            }
            catch (OperationCanceledException) { timer.Stop(); throw; }
            catch (Exception ex)
            {
                timer.Stop();
                LogManager.Error($"ApplyManualOutgoingRuleAsync failed after {timer.ElapsedMilliseconds}ms", ex);
                return 0;
            }
        }

        private async Task CreateMatchingReconciliationsAsync(DataAmbre receivableLine, List<DataAmbre> pivotLines, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var receivableReco = await _reconciliationService.GetOrCreateReconciliationAsync(receivableLine.ID).ConfigureAwait(false);
            var pivotTasks = pivotLines.Select(p => _reconciliationService.GetOrCreateReconciliationAsync(p.ID));
            var pivotReconciliations = await Task.WhenAll(pivotTasks).ConfigureAwait(false);

            try
            {
                AppendComment(receivableReco, $"Auto-matched with {pivotLines.Count} pivot line(s)");
                foreach (var pivotReco in pivotReconciliations)
                    AppendComment(pivotReco, $"Auto-matched with receivable line {receivableLine.ID}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to append match comment for receivable {ReceivableId} with {PivotCount} pivot line(s)", receivableLine?.ID, pivotLines?.Count);
            }

            await _reconciliationService.SaveReconciliationsAsync(new[] { receivableReco }.Concat(pivotReconciliations)).ConfigureAwait(false);
        }

        private void AppendComment(Reconciliation reco, string message)
        {
            if (reco == null) return;
            var timestamp = $"[{_clock.Now:yyyy-MM-dd HH:mm}] {_currentUser}: ";
            var fullMsg = timestamp + message;

            if (string.IsNullOrWhiteSpace(reco.Comments))
                reco.Comments = fullMsg;
            else if (!reco.Comments.Contains(message))
                reco.Comments = reco.Comments + Environment.NewLine + fullMsg;
        }
    }
}
