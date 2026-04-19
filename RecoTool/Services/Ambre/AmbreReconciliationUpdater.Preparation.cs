using OfflineFirstAccess.Helpers;
using RecoTool.Helpers;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// Note: TransformationService lives in the parent namespace RecoTool.Services and resolves automatically
// from RecoTool.Services.Ambre via namespace lookup.

namespace RecoTool.Services.Ambre
{
    /// <summary>
    /// Preparation phase for <see cref="AmbreReconciliationUpdater"/>:
    /// builds <see cref="Reconciliation"/> entities for new AMBRE rows, resolving
    /// DWINGS references via fast-path (dictionary lookups) or slow-path (Free API).
    /// Exposed helpers: <see cref="PrepareReconciliationsAsync"/>, <see cref="CreateReconciliationAsync"/>,
    /// <see cref="CalculatePaymentReferenceForPivot"/>.
    /// </summary>
    public partial class AmbreReconciliationUpdater
    {
        private async Task<List<Reconciliation>> PrepareReconciliationsAsync(
            List<DataAmbre> newRecords,
            Country country,
            string countryId,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            IReadOnlyList<DwingsGuaranteeDto> dwGuarantees,
            Action<string, int> progressCallback = null)
        {
            // DWINGS data passed from caller to avoid reloading
            _transformationService = new TransformationService(new List<Country> { country });

            var staged = new List<ReconciliationStaging>();

            GuaranteeCache.Initialise(dwGuarantees);

            // ── PERF: Build O(1) invoice lookup once for entire import ──
            var invoiceLookup = new DwingsInvoiceLookup(dwInvoices);
            _dwingsResolver.SetInvoiceLookup(invoiceLookup);
            _dwingsResolver.PreBuildLookups(dwInvoices, dwGuarantees);

            // ── Separate fast path (DWINGS only) from slow path (needs Free API) ──
            var freeApiRecords = new List<DataAmbre>();
            var fastRecords = new List<DataAmbre>();
            foreach (var da in newRecords)
            {
                var reference = da.Reconciliation_Num ?? da.RawLabel ?? string.Empty;
                if (reference.Contains("FK") || reference.Contains("IPA"))
                    freeApiRecords.Add(da);
                else
                    fastRecords.Add(da);
            }

            int total = newRecords.Count;
            int processed = 0;
            var overallTimer = System.Diagnostics.Stopwatch.StartNew();
            const int batchSize = 500;

            // ── Phase 1: Fast path — DWINGS resolution only (CPU-bound, no API calls) ──
            // Uses Parallel.ForEach via Task.Run to avoid blocking the UI thread.
            // IMPORTANT: progressCallback must NOT be called inside Parallel.ForEach —
            // it dispatches to the UI thread which is blocked by ForEach → deadlock.
            progressCallback?.Invoke($"Resolving DWINGS references: 0/{fastRecords.Count}...", 42);
            LogManager.Info($"[PERF] Phase 1: {fastRecords.Count} records (DWINGS only), {freeApiRecords.Count} records (Free API)");

            var fastStaged = new ConcurrentBag<ReconciliationStaging>();
            var fastTimer = System.Diagnostics.Stopwatch.StartNew();

            await Task.Run(() =>
            {
                Parallel.ForEach(
                    fastRecords,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    dataAmbre =>
                    {
                        var reconciliation = new Reconciliation
                        {
                            ID = dataAmbre.ID,
                            CreationDate = DateTime.UtcNow,
                            ModifiedBy = _currentUser,
                            LastModified = DateTime.UtcNow,
                            Version = 1
                        };

                        bool isPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot);
                        var dwingsRefs = _dwingsResolver.ResolveReferences(
                            dataAmbre, isPivot, dwInvoices, dwGuarantees);

                        reconciliation.DWINGS_InvoiceID = dwingsRefs.InvoiceId;
                        reconciliation.DWINGS_BGPMT = dwingsRefs.CommissionId;
                        reconciliation.DWINGS_GuaranteeID = dwingsRefs.GuaranteeId;

                        if (isPivot)
                        {
                            reconciliation.PaymentReference = CalculatePaymentReferenceForPivot(
                                dataAmbre, dwingsRefs.GuaranteeId, dwingsRefs.InvoiceId, dwGuarantees);
                        }

                        fastStaged.Add(new ReconciliationStaging
                        {
                            Reconciliation = reconciliation,
                            DataAmbre = dataAmbre,
                            IsPivot = isPivot,
                            Bgi = reconciliation.DWINGS_InvoiceID
                        });
                    });
            }).ConfigureAwait(false);

            fastTimer.Stop();
            staged.AddRange(fastStaged);
            processed = fastRecords.Count;

            // ── Diagnostic: verify resolution hit rates ──
            int hitInvoice = staged.Count(s => !string.IsNullOrWhiteSpace(s.Reconciliation.DWINGS_InvoiceID));
            int hitBgpmt = staged.Count(s => !string.IsNullOrWhiteSpace(s.Reconciliation.DWINGS_BGPMT));
            int hitGuarantee = staged.Count(s => !string.IsNullOrWhiteSpace(s.Reconciliation.DWINGS_GuaranteeID));
            LogManager.Info($"[PERF] Phase 1: {fastRecords.Count} records in {fastTimer.ElapsedMilliseconds}ms " +
                $"| Hits: Invoice={hitInvoice}, BGPMT={hitBgpmt}, Guarantee={hitGuarantee} " +
                $"| Lookups: dwInvoices={dwInvoices?.Count ?? 0}, dwGuarantees={dwGuarantees?.Count ?? 0}");

            progressCallback?.Invoke($"DWINGS resolved: {fastRecords.Count} in {fastTimer.ElapsedMilliseconds}ms " +
                $"(Invoice={hitInvoice}, BGPMT={hitBgpmt}, Guarantee={hitGuarantee})", 75);

            // ── Phase 2: Slow path — records needing Free API (throttled) ──
            if (freeApiRecords.Count > 0)
            {
                progressCallback?.Invoke($"Free API: 0/{freeApiRecords.Count}...", 76);
                LogManager.Info($"[PERF] Phase 2: {freeApiRecords.Count} Free API candidates");

                for (int i = 0; i < freeApiRecords.Count; i += batchSize)
                {
                    var batch = freeApiRecords.Skip(i).Take(batchSize).ToList();
                    // SECURE: Each task has timeout + exception isolation to prevent one failure from blocking the batch
                    var batchTasks = batch.Select(async dataAmbre =>
                    {
                        try
                        {
                            // SECURE: Add timeout to prevent indefinite blocking on Free API
                            // Free can be slow; keep this aligned with the 5-minute Free HTTP timeout.
                            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                            var reconciliation = await CreateReconciliationAsync(
                                dataAmbre, country, countryId, dwInvoices, dwGuarantees, cts.Token).ConfigureAwait(false);
                            return new ReconciliationStaging
                            {
                                Reconciliation = reconciliation,
                                DataAmbre = dataAmbre,
                                IsPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot),
                                Bgi = reconciliation.DWINGS_InvoiceID
                            };
                        }
                        catch (OperationCanceledException)
                        {
                            // SECURE: Free API timeout - do not block the import; keep the record as not found.
                            LogManager.Warning($"[FreeAPI] Timeout for {dataAmbre.ID} after 5 minutes.");
                            return new ReconciliationStaging
                            {
                                Reconciliation = new Reconciliation { ID = dataAmbre.ID },
                                DataAmbre = dataAmbre,
                                IsPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot),
                                Bgi = null
                            };
                        }
                        catch (Exception ex)
                        {
                            // SECURE: Best-effort enrichment only. Any Free error should not block the import
                            // nor cause the same record to be retried forever.
                            LogManager.Warning($"[FreeAPI] Best-effort enrichment failed for {dataAmbre.ID}: {ex.Message}");
                            return new ReconciliationStaging
                            {
                                Reconciliation = new Reconciliation { ID = dataAmbre.ID },
                                DataAmbre = dataAmbre,
                                IsPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot),
                                Bgi = null
                            };
                        }
                    }).ToList();
                    
                    // SECURE: Use WhenAll with ConfigureAwait to avoid deadlocks
                    var results = await Task.WhenAll(batchTasks).ConfigureAwait(false);
                    staged.AddRange(results.Where(r => r != null));
                    processed += batch.Count;

                    int apiDone = processed - fastRecords.Count;
                    int pct = 76 + (int)(9.0 * apiDone / freeApiRecords.Count);
                    progressCallback?.Invoke($"Free API: {apiDone}/{freeApiRecords.Count} ({processed}/{total} total)...", pct);
                }

                int freeApiHits = staged.Count(s => !string.IsNullOrWhiteSpace(s.Reconciliation.MbawData) && s.Reconciliation.MbawData != "Not found");
                LogManager.Info($"[PERF] Free API: {freeApiHits}/{freeApiRecords.Count} matched");
            }

            overallTimer.Stop();
            progressCallback?.Invoke($"Reconciliation prepared: {total} records in {overallTimer.ElapsedMilliseconds / 1000}s", 85);
            LogManager.Info($"[PERF] PrepareReconciliations: {total} records in {overallTimer.ElapsedMilliseconds}ms (fast={fastRecords.Count}, api={freeApiRecords.Count})");

            // ── KPI calculation ──
            var kpiTimer = System.Diagnostics.Stopwatch.StartNew();
            var kpiStaging = staged.Select(s => new ReconciliationKpiCalculator.ReconciliationStaging
            {
                DataAmbre = s.DataAmbre,
                Reconciliation = s.Reconciliation,
                IsPivot = s.IsPivot
            }).ToList();

            ReconciliationKpiCalculator.CalculateKpis(kpiStaging);

            // Copy calculated KPIs back to staging items
            for (int i = 0; i < staged.Count && i < kpiStaging.Count; i++)
            {
                staged[i].IsGrouped = kpiStaging[i].IsGrouped;
                staged[i].MissingAmount = kpiStaging[i].MissingAmount;
            }
            kpiTimer.Stop();
            LogManager.Info($"[PERF] KPI calculation completed for {staged.Count} records in {kpiTimer.ElapsedMilliseconds}ms");

            // HARD-CODED RULE: For PIVOT lines with DIRECT_DEBIT payment method, set Category to COLLECTION
            ApplyDirectDebitCollectionRule(staged, dwInvoices);

            // Apply truth-table rules (import scope) - pass dwInvoices and dwGuarantees to avoid reloading
            // MANUAL_OUTGOING rule will be applied AFTER saving to DB (in UpdateReconciliationTableAsync)
            // isNewLines=true enables FALLBACK rule for lines without matches
            await ApplyTruthTableRulesAsync(staged, country, countryId, dwInvoices, dwGuarantees, isNewLines: true);

            // 1️⃣  ReasonNonRisky automatique
            AutoSetReasonNonRisky(staged);

            // 2️⃣  IT Issue → INVESTIGATE
            EnforceItIssueAction(staged);

            // 3️⃣  Fallback (seulement lors d'un import ; pour les lignes déjà en base on ne veut pas tout écraser)
            ApplyFallbackRule(staged);

            return staged.Select(s => s.Reconciliation).ToList();
        }

        private async Task<Reconciliation> CreateReconciliationAsync(
            DataAmbre dataAmbre,
            Country country,
            string countryId,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            IReadOnlyList<DwingsGuaranteeDto> dwGuarantees,
            CancellationToken cancellationToken = default)
        {
            var reconciliation = new Reconciliation
            {
                ID = dataAmbre.ID,
                CreationDate = DateTime.UtcNow,
                ModifiedBy = _currentUser,
                LastModified = DateTime.UtcNow,
                Version = 1
            };

            bool isPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot);

            // Resolve DWINGS references (sync — CPU-bound O(1) lookups, no I/O)
            var dwingsRefs = _dwingsResolver.ResolveReferences(
                dataAmbre, isPivot, dwInvoices, dwGuarantees);
                
            reconciliation.DWINGS_InvoiceID = dwingsRefs.InvoiceId;
            reconciliation.DWINGS_BGPMT = dwingsRefs.CommissionId;
            reconciliation.DWINGS_GuaranteeID = dwingsRefs.GuaranteeId;

            // FREE API FALLBACK: trigger only when no DWINGS Guarantee is linked
            if (string.IsNullOrWhiteSpace(reconciliation.DWINGS_GuaranteeID))
            {
                try
                {
                    var day = dataAmbre.Operation_Date ?? dataAmbre.Value_Date ?? DateTime.Today;
                    var reference = dataAmbre.Reconciliation_Num ?? dataAmbre.RawLabel ?? string.Empty;
                    var serviceCode = country?.CNT_ServiceCode;
                    // Only call the Free API if it has not been called before (MbawData empty for new lines)
                    if (string.IsNullOrWhiteSpace(reconciliation.MbawData) && (reference.Contains("FK") || reference.Contains("IPA")))
                    {
                        var payload = await _freeApi.SearchAsync(day, reference, serviceCode, cancellationToken).ConfigureAwait(false);
                        // Store payload or a sentinel "Not found" to prevent future calls
                        reconciliation.MbawData = string.IsNullOrWhiteSpace(payload) ? "Not found" : payload;

                        if (!string.IsNullOrWhiteSpace(payload))
                        {
                            // Extract tokens from the payload
                            var foundBgpmt = DwingsLinkingHelper.ExtractBgpmtToken(payload);
                            var foundBgi = DwingsLinkingHelper.ExtractBgiToken(payload);
                            var foundGid = DwingsLinkingHelper.ExtractGuaranteeId(payload);

                            if (!string.IsNullOrWhiteSpace(foundGid) && string.IsNullOrWhiteSpace(reconciliation.DWINGS_GuaranteeID))
                                reconciliation.DWINGS_GuaranteeID = foundGid;
                            if (!string.IsNullOrWhiteSpace(foundBgi) && string.IsNullOrWhiteSpace(reconciliation.DWINGS_InvoiceID))
                                reconciliation.DWINGS_InvoiceID = foundBgi;
                            if (!string.IsNullOrWhiteSpace(foundBgpmt) && string.IsNullOrWhiteSpace(reconciliation.DWINGS_BGPMT))
                                reconciliation.DWINGS_BGPMT = foundBgpmt;

                            // Fallback: use GuaranteeCache only if no structured G/N-ref was found by regex
                            if (string.IsNullOrWhiteSpace(reconciliation.DWINGS_GuaranteeID))
                            {
                                var searchByOfficial = GuaranteeCache.FindGuaranteeId(payload);
                                if (searchByOfficial != null)
                                    reconciliation.DWINGS_GuaranteeID = searchByOfficial;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // SECURE: Free API timeout - do not block the import; keep the record as not found.
                    LogManager.Warning($"[FreeAPI] Timeout for {dataAmbre.ID} after 5 minutes.");
                    reconciliation.MbawData = "Not found";
                }
                catch (Exception ex)
                {
                    // SECURE: Best-effort enrichment only. Any Free error should not block the import
                    // nor cause the same record to be retried forever.
                    LogManager.Warning($"[FreeAPI] Best-effort enrichment failed for {dataAmbre.ID}: {ex.Message}");
                    if (string.IsNullOrWhiteSpace(reconciliation.MbawData))
                        reconciliation.MbawData = "Not found";
                }
            }

            // For PIVOT: Auto-fill PaymentReference for bulk trigger
            if (isPivot)
            {
                reconciliation.PaymentReference = CalculatePaymentReferenceForPivot(
                    dataAmbre, dwingsRefs.GuaranteeId, dwingsRefs.InvoiceId, dwGuarantees);
            }

            // KPI and Action are set by truth-table rules only

            return reconciliation;
        }

        /// <summary>
        /// Calculates Payment Reference for PIVOT lines based on priority:
        /// 1. Reconciliation_Num (if not empty)
        /// 2. If guarantee type is REISSUANCE => Guarantee ID
        /// 3. Else => BGI (Invoice ID)
        /// 4. If none => blank (user will set manually)
        /// </summary>
        private string CalculatePaymentReferenceForPivot(
            DataAmbre dataAmbre, 
            string guaranteeId, 
            string invoiceId,
            IReadOnlyList<DwingsGuaranteeDto> dwGuarantees)
        {
            // Priority 1: Reconciliation_Num
            if (!string.IsNullOrWhiteSpace(dataAmbre.Reconciliation_Num))
                return dataAmbre.Reconciliation_Num.Trim();

            // Priority 2: If guarantee type is REISSUANCE => use Guarantee ID (O(1) lookup)
            if (!string.IsNullOrWhiteSpace(guaranteeId))
            {
                DwingsGuaranteeDto guarantee;
                if (_guaranteeById != null && _guaranteeById.TryGetValue(guaranteeId, out guarantee)
                    && guarantee != null
                    && string.Equals(guarantee.GUARANTEE_TYPE, "REISSUANCE", StringComparison.OrdinalIgnoreCase))
                {
                    return guaranteeId.Trim();
                }
            }

            // Priority 3: BGI (Invoice ID)
            if (!string.IsNullOrWhiteSpace(invoiceId))
                return invoiceId.Trim();

            // Priority 4: Blank (user will set manually)
            return null;
        }
    }
}
