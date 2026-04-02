using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;
using OfflineFirstAccess.Models;
using RecoTool.Helpers;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using RecoTool.Services;
using RecoTool.Services.Rules;
using RecoTool.Services.External;
using Microsoft.Extensions.DependencyInjection;
using RecoTool.Services.Helpers;
using RecoTool.Infrastructure.Logging;
using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;

namespace RecoTool.Services.Ambre
{
    /// <summary>
    /// Gestionnaire de mise à  jour de la table T_Reconciliation
    /// </summary>
    public class AmbreReconciliationUpdater
    {
        private readonly OfflineFirstService _offlineFirstService;
        private readonly string _currentUser;
        private readonly ReconciliationService _reconciliationService;
        private readonly DwingsReferenceResolver _dwingsResolver;
        private readonly RulesEngine _rulesEngine;
        private readonly IFreeApiClient _freeApi;
        private TransformationService _transformationService; // Cached per import

        // ── PERF: Pre-built O(1) lookup dictionaries (built once per import, avoid O(n) FirstOrDefault per row) ──
        private Dictionary<string, DwingsInvoiceDto> _invoiceById;
        private Dictionary<string, DwingsInvoiceDto> _invoiceByBgpmt;
        private Dictionary<string, DwingsGuaranteeDto> _guaranteeById;

        public AmbreReconciliationUpdater(
            OfflineFirstService offlineFirstService,
            string currentUser,
            ReconciliationService reconciliationService,
            IFreeApiClient freeApi = null)
        {
            _offlineFirstService = offlineFirstService ?? throw new ArgumentNullException(nameof(offlineFirstService));
            _currentUser = currentUser;
            _reconciliationService = reconciliationService;
            _dwingsResolver = new DwingsReferenceResolver(reconciliationService);
            _rulesEngine = new RulesEngine(_offlineFirstService);
            _freeApi = freeApi
                ?? App.ServiceProvider?.GetService<IFreeApiClient>()
                ?? new FreeApiService();
        }

        /// <summary>
        /// Met à  jour la table T_Reconciliation avec les changements d'import
        /// </summary>
        public async Task UpdateReconciliationTableAsync(
            ImportChanges changes,
            string countryId,
            Country country,
            Action<string, int> progressCallback = null)
        {
            var totalTimer = System.Diagnostics.Stopwatch.StartNew();
            LogManager.Info($"[PERF] UpdateReconciliationTableAsync started for {countryId}");

            try
            {
                // OPTIMIZATION: Load DWINGS data once for entire import (not per phase)
                var dwTimer = System.Diagnostics.Stopwatch.StartNew();
                var dwInvoices = (await _reconciliationService.GetDwingsInvoicesAsync()).ToList();
                var dwGuarantees = (await _reconciliationService.GetDwingsGuaranteesAsync()).ToList();
                dwTimer.Stop();
                LogManager.Info($"[PERF] DWINGS data loaded: {dwInvoices.Count} invoices, {dwGuarantees.Count} guarantees in {dwTimer.ElapsedMilliseconds}ms");

                // ── PERF: Build O(1) lookup dictionaries once for entire import ──
                _invoiceById = new Dictionary<string, DwingsInvoiceDto>(dwInvoices.Count, StringComparer.OrdinalIgnoreCase);
                _invoiceByBgpmt = new Dictionary<string, DwingsInvoiceDto>(dwInvoices.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var inv in dwInvoices)
                {
                    if (!string.IsNullOrWhiteSpace(inv?.INVOICE_ID))
                        _invoiceById[inv.INVOICE_ID] = inv;
                    if (!string.IsNullOrWhiteSpace(inv?.BGPMT))
                        _invoiceByBgpmt[inv.BGPMT] = inv;
                }
                _guaranteeById = new Dictionary<string, DwingsGuaranteeDto>(dwGuarantees.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var g in dwGuarantees)
                {
                    if (!string.IsNullOrWhiteSpace(g?.GUARANTEE_ID))
                        _guaranteeById[g.GUARANTEE_ID] = g;
                }

                // Préparer les enregistrements de réconciliation
                var prepareTimer = System.Diagnostics.Stopwatch.StartNew();
                var reconciliations = await PrepareReconciliationsAsync(
                    changes.ToAdd, country, countryId, dwInvoices, dwGuarantees, progressCallback);
                prepareTimer.Stop();
                LogManager.Info($"[PERF] PrepareReconciliations completed for {changes.ToAdd.Count} new records in {prepareTimer.ElapsedMilliseconds}ms");

                // Appliquer les changements à  la base de données
                var applyTimer = System.Diagnostics.Stopwatch.StartNew();
                await ApplyReconciliationChangesAsync(
                    reconciliations,
                    changes.ToUpdate,
                    changes.ToArchive,
                    countryId);
                applyTimer.Stop();
                LogManager.Info($"[PERF] ApplyReconciliationChanges completed in {applyTimer.ElapsedMilliseconds}ms");

                // Apply MANUAL_OUTGOING rule AFTER saving to DB (so it sees ALL lines: new + existing)
                // This must happen BEFORE ApplyRulesToExistingRecordsAsync to avoid conflicts
                try
                {
                    var manualOutgoingTimer = System.Diagnostics.Stopwatch.StartNew();
                    var manualOutgoingMatches = await _reconciliationService.ApplyManualOutgoingRuleAsync(countryId).ConfigureAwait(false);
                    manualOutgoingTimer.Stop();
                    if (manualOutgoingMatches > 0)
                    {
                        LogManager.Info($"[PERF] MANUAL_OUTGOING rule: matched {manualOutgoingMatches} pair(s) in {manualOutgoingTimer.ElapsedMilliseconds}ms");
                    }
                }
                catch (Exception manualEx)
                {
                    LogManager.Warning($"Non-blocking: MANUAL_OUTGOING rule failed: {manualEx.Message}");
                }

                // Remplir les références DWINGS manquantes pour les enregistrements mis à  jour (sans écraser les liens manuels)
                try
                {
                    var fillTimer = System.Diagnostics.Stopwatch.StartNew();
                    var toRelink = new List<DataAmbre>();
                    if (changes?.ToUpdate != null && changes.ToUpdate.Count > 0)
                        toRelink.AddRange(changes.ToUpdate);

                    try
                    {
                        var unlinkedIds = await GetUnlinkedReconciliationIdsAsync(countryId).ConfigureAwait(false);
                        if (unlinkedIds != null && unlinkedIds.Count > 0)
                        {
                            var ambreCs = _offlineFirstService?.GetAmbreConnectionString(countryId);
                            if (!string.IsNullOrWhiteSpace(ambreCs))
                            {
                                var ambreRows = await LoadAmbreRowsByIdsAsync(ambreCs, unlinkedIds).ConfigureAwait(false);
                                if (ambreRows != null && ambreRows.Count > 0)
                                    toRelink.AddRange(ambreRows);
                            }
                        }
                    }
                    catch { }

                    if (toRelink.Count > 0)
                    {
                        toRelink = toRelink
                            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ID))
                            .GroupBy(a => a.ID, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .ToList();

                        await UpdateDwingsReferencesForUpdatesAsync(toRelink, country, countryId);
                    }
                    fillTimer.Stop();


                    LogManager.Info($"[PERF] UpdateDwingsReferencesForUpdates completed in {fillTimer.ElapsedMilliseconds}ms");
                }
                catch (Exception fillEx)
                {
                    LogManager.Warning($"Non-blocking: failed to backfill DWINGS refs for updates: {fillEx.Message}");
                }

                // Réappliquer les règles aux enregistrements existants
                try
                {
                    var rulesTimer = System.Diagnostics.Stopwatch.StartNew();
                    await ApplyRulesToExistingRecordsAsync(changes.ToUpdate, country, countryId, dwInvoices, dwGuarantees);
                    rulesTimer.Stop();
                    LogManager.Info($"[PERF] ApplyRulesToExistingRecords completed for {changes.ToUpdate.Count} records in {rulesTimer.ElapsedMilliseconds}ms");
                }
                catch (Exception rulesEx)
                {
                    LogManager.Warning($"Non-blocking: failed to apply rules to existing records: {rulesEx.Message}");
                }

                totalTimer.Stop();
                LogManager.Info($"[PERF] T_Reconciliation update completed for {countryId} in {totalTimer.ElapsedMilliseconds}ms (total)");
            }
            catch (Exception ex)
            {
                totalTimer.Stop();
                LogManager.Error($"Error updating T_Reconciliation for {countryId} after {totalTimer.ElapsedMilliseconds}ms", ex);
                throw new InvalidOperationException($"Failed to update reconciliation table: {ex.Message}", ex);
            }
        }

        private async Task<List<string>> GetUnlinkedReconciliationIdsAsync(string countryId)
        {
            var ids = new List<string>();
            try
            {
                var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
                using (var conn = new OleDbConnection(connectionString))
                {
                    await conn.OpenAsync();
                    using (var cmd = new OleDbCommand(
                        "SELECT [ID] FROM [T_Reconciliation] WHERE [DeleteDate] IS NULL AND (" +
                        "([DWINGS_InvoiceID] IS NULL OR [DWINGS_InvoiceID] = '') OR " +
                        "([DWINGS_BGPMT] IS NULL OR [DWINGS_BGPMT] = '') OR " +
                        "([DWINGS_GuaranteeID] IS NULL OR [DWINGS_GuaranteeID] = '')" +
                        ")",
                        conn))
                    using (var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await rdr.ReadAsync().ConfigureAwait(false))
                        {
                            var id = rdr.IsDBNull(0) ? null : Convert.ToString(rdr.GetValue(0));
                            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                        }
                    }
                }
            }
            catch { }

            return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task<List<DataAmbre>> LoadAmbreRowsByIdsAsync(string ambreConnectionString, List<string> ids)
        {
            var rows = new List<DataAmbre>();
            if (string.IsNullOrWhiteSpace(ambreConnectionString) || ids == null || ids.Count == 0) return rows;

            const int batchSize = 200;
            for (int start = 0; start < ids.Count; start += batchSize)
            {
                var batch = ids.Skip(start).Take(batchSize).ToList();
                var sb = new StringBuilder();
                sb.Append("SELECT * FROM T_Data_Ambre WHERE ID IN (");
                for (int i = 0; i < batch.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("?");
                }
                sb.Append(") AND DeleteDate IS NULL");

                using (var conn = new OleDbConnection(ambreConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);
                    using (var cmd = new OleDbCommand(sb.ToString(), conn))
                    {
                        foreach (var id in batch)
                            cmd.Parameters.AddWithValue("@ID", id);

                        using (var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            var props = typeof(DataAmbre)
                                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(p => p.CanWrite)
                                .ToList();

                            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < rdr.FieldCount; i++)
                            {
                                var name = rdr.GetName(i);
                                if (!string.IsNullOrWhiteSpace(name) && !ordinals.ContainsKey(name)) ordinals[name] = i;
                            }

                            while (await rdr.ReadAsync().ConfigureAwait(false))
                            {
                                var item = new DataAmbre();
                                foreach (var p in props)
                                {
                                    if (!ordinals.TryGetValue(p.Name, out var idx)) continue;
                                    if (rdr.IsDBNull(idx)) continue;

                                    try
                                    {
                                        var val = rdr.GetValue(idx);
                                        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                                        if (t.IsEnum)
                                        {
                                            var enumVal = Convert.ChangeType(val, Enum.GetUnderlyingType(t), CultureInfo.InvariantCulture);
                                            p.SetValue(item, Enum.ToObject(t, enumVal));
                                        }
                                        else
                                        {
                                            p.SetValue(item, Convert.ChangeType(val, t, CultureInfo.InvariantCulture));
                                        }
                                    }
                                    catch { }
                                }
                                rows.Add(item);
                            }
                        }
                    }
                }
            }

            return rows;
        }


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

        private RuleContext BuildRuleContext(DataAmbre dataAmbre, Reconciliation reconciliation, Country country, string countryId, bool isPivot, IReadOnlyList<DwingsInvoiceDto> dwInvoices, IReadOnlyList<DwingsGuaranteeDto> dwGuarantees, bool isGrouped, decimal? missingAmount, bool isNewLine)
        {
            // Determine transaction type enum name
            TransactionType? tx;
            
            if (isPivot)
            {
                // For PIVOT: use Category field (enum TransactionType)
                tx = _transformationService.DetermineTransactionType(dataAmbre.RawLabel, isPivot, dataAmbre.Category);
            }
            else
            {
                // For RECEIVABLE: use PAYMENT_METHOD from DWINGS invoice if available
                string paymentMethod = null;
                if (!string.IsNullOrWhiteSpace(reconciliation?.DWINGS_InvoiceID))
                {
                    DwingsInvoiceDto inv;
                    if (_invoiceById != null && _invoiceById.TryGetValue(reconciliation.DWINGS_InvoiceID, out inv))
                        paymentMethod = inv?.PAYMENT_METHOD;
                }
                
                // Map PAYMENT_METHOD to TransactionType enum
                if (!string.IsNullOrWhiteSpace(paymentMethod))
                {
                    var upperMethod = paymentMethod.Trim().ToUpperInvariant().Replace(' ', '_');
                    if (Enum.TryParse<TransactionType>(upperMethod, true, out var parsed))
                    {
                        tx = parsed;
                    }
                    else
                    {
                        // Fallback to label-based detection
                        tx = _transformationService.DetermineTransactionType(dataAmbre.RawLabel, isPivot, null);
                    }
                }
                else
                {
                    // No PAYMENT_METHOD available, use label-based detection
                    tx = _transformationService.DetermineTransactionType(dataAmbre.RawLabel, isPivot, null);
                }
            }
            
            string txName = tx.HasValue ? Enum.GetName(typeof(TransactionType), tx.Value) : null;

            // Guarantee type from DWINGS (requires a DWINGS_GuaranteeID link)
            string guaranteeType = null;
            if (!isPivot && !string.IsNullOrWhiteSpace(reconciliation?.DWINGS_GuaranteeID))
            {
                try
                {
                    DwingsGuaranteeDto guar;
                    if (_guaranteeById != null && _guaranteeById.TryGetValue(reconciliation.DWINGS_GuaranteeID, out guar))
                        guaranteeType = guar?.GUARANTEE_TYPE;
                }
                catch { }
            }

            // Sign from amount
            var sign = dataAmbre.SignedAmount >= 0 ? "C" : "D";

            // Presence of DWINGS links (any of Invoice/Guarantee/BGPMT)
            bool? hasDw = (!string.IsNullOrWhiteSpace(reconciliation?.DWINGS_InvoiceID)
                          || !string.IsNullOrWhiteSpace(reconciliation?.DWINGS_GuaranteeID)
                          || !string.IsNullOrWhiteSpace(reconciliation?.DWINGS_BGPMT));

            // Extended time/state inputs
            var today = DateTime.Today;
            
            // FIXED: Nullable boolean logic - only set to bool value if we can determine it, otherwise keep null
            bool? triggerDateIsNull = reconciliation?.TriggerDate.HasValue == true ? (bool?)false : (reconciliation != null ? (bool?)true : null);
            
            int? daysSinceTrigger = reconciliation?.TriggerDate.HasValue == true
                ? (int?)(today - reconciliation.TriggerDate.Value.Date).TotalDays
                : null;
            
            int? operationDaysAgo = dataAmbre.Operation_Date.HasValue
                ? (int?)(today - dataAmbre.Operation_Date.Value.Date).TotalDays
                : null;
            
            bool? isMatched = hasDw; // consider matched when any DWINGS link is present
            bool? hasManualMatch = null; // unknown at import time
            
            // FIXED: IsFirstRequest should be null if we don't have reconciliation data
            bool? isFirstRequest = reconciliation?.FirstClaimDate.HasValue == true ? (bool?)false : (reconciliation != null ? (bool?)true : null);
            
            int? daysSinceReminder = reconciliation?.LastClaimDate.HasValue == true
                ? (int?)(today - reconciliation.LastClaimDate.Value.Date).TotalDays
                : null;

            // OPTIMIZATION: Use pre-built dictionary instead of O(n) linear scan
            string mtStatus = null;
            bool? hasCommEmail = null;
            bool? bgiInitiated = null;
            try
            {
                DwingsInvoiceDto invLookup;
                if (!string.IsNullOrWhiteSpace(reconciliation?.DWINGS_InvoiceID)
                    && _invoiceById != null && _invoiceById.TryGetValue(reconciliation.DWINGS_InvoiceID, out invLookup)
                    && invLookup != null)
                {
                    mtStatus = invLookup.MT_STATUS;
                    hasCommEmail = invLookup.COMM_ID_EMAIL;
                    if (!string.IsNullOrWhiteSpace(invLookup.T_INVOICE_STATUS))
                        bgiInitiated = string.Equals(invLookup.T_INVOICE_STATUS, "INITIATED", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            return new RuleContext
            {
                CountryId = countryId,
                IsPivot = isPivot,
                GuaranteeType = guaranteeType,
                TransactionType = txName,
                HasDwingsLink = hasDw,
                IsGrouped = isGrouped,
                IsAmountMatch = isGrouped && missingAmount.HasValue && missingAmount.Value == 0,
                MissingAmount = missingAmount,
                Sign = sign,
                Bgi = reconciliation?.DWINGS_InvoiceID,
                // Extended fields
                TriggerDateIsNull = triggerDateIsNull,
                DaysSinceTrigger = daysSinceTrigger,
                OperationDaysAgo = operationDaysAgo,
                IsMatched = isMatched,
                HasManualMatch = hasManualMatch,
                IsFirstRequest = isFirstRequest,
                IsNewLine = isNewLine,
                DaysSinceReminder = daysSinceReminder,
                CurrentActionId = reconciliation?.Action,
                // New DWINGS-derived
                MtStatus = mtStatus,
                HasCommIdEmail = hasCommEmail,
                IsBgiInitiated = bgiInitiated
            };
        }

        /// <summary>
        /// HARD-CODED RULE: For PIVOT lines with DIRECT_DEBIT payment method, set Category to COLLECTION
        /// This must run BEFORE truth-table rules
        /// </summary>
        private void ApplyDirectDebitCollectionRule(List<ReconciliationStaging> staged, IReadOnlyList<DwingsInvoiceDto> dwInvoices)
        {
            if (staged == null || dwInvoices == null) return;

            int appliedCount = 0;
            foreach (var s in staged)
            {
                // Only for PIVOT lines
                if (!s.IsPivot) continue;

                // Check if line has BGI or BGPMT
                var bgi = s.Reconciliation?.DWINGS_InvoiceID;
                var bgpmt = s.Reconciliation?.DWINGS_BGPMT;
                
                if (string.IsNullOrWhiteSpace(bgi) && string.IsNullOrWhiteSpace(bgpmt))
                    continue;

                // Find invoice and check payment method (O(1) dictionary lookup)
                DwingsInvoiceDto invoice = null;
                if (_invoiceById != null && !string.IsNullOrWhiteSpace(bgi))
                    _invoiceById.TryGetValue(bgi, out invoice);
                if (invoice == null && _invoiceByBgpmt != null && !string.IsNullOrWhiteSpace(bgpmt))
                    _invoiceByBgpmt.TryGetValue(bgpmt, out invoice);

                if (invoice != null && string.Equals(invoice.PAYMENT_METHOD, "DIRECT_DEBIT", StringComparison.OrdinalIgnoreCase))
                {
                    // Set Category to COLLECTION (enum value = 0)
                    s.DataAmbre.Category = (int)TransactionType.COLLECTION;
                    appliedCount++;
                }
            }

            if (appliedCount > 0)
                LogManager.Info($"[HARD-CODED RULE] DIRECT_DEBIT → COLLECTION: Applied to {appliedCount} PIVOT line(s)");
        }

        private async Task ApplyTruthTableRulesAsync(
                List<ReconciliationStaging> staged,
                Country country,
                string countryId,
                IReadOnlyList<DwingsInvoiceDto> dwInvoices,
                IReadOnlyList<DwingsGuaranteeDto> dwGuarantees,
                bool isNewLines = true)
        {
            try
            {
                if (staged == null || staged.Count == 0) return;

                /* --------------------------------------------------------------
                 * 1️⃣  Première passe – exécution des règles (SELF) + collecte du
                 *     COUNTERPART (par BGI)
                 * -------------------------------------------------------------- */
                var counterpartIntents = new List<(string Bgi,
                                                  bool TargetIsPivot,
                                                  string RuleId,
                                                  int? ActionId,
                                                  int? KgiId,
                                                  int? IncidentTypeId,
                                                  bool? RiskyItem,
                                                  int? ReasonNonRiskyId,
                                                  bool? ToRemind,
                                                  int? ToRemindDays)>();

                const int batchSize = 2000;                     // 2000 lignes par lot
                var allEvalResults = new List<(ReconciliationStaging Staging,
                                              RuleEvaluationResult Result)>();

                for (int i = 0; i < staged.Count; i += batchSize)
                {
                    var batch = staged.Skip(i).Take(batchSize).ToList();

                    var batchTasks = batch.Select(async s =>
                    {
                        var ctx = BuildRuleContext(
                                       s.DataAmbre,
                                       s.Reconciliation,
                                       country,
                                       countryId,
                                       s.IsPivot,
                                       dwInvoices,
                                       dwGuarantees,
                                       s.IsGrouped,
                                       s.MissingAmount,
                                       isNewLines);

                        var res = await _rulesEngine
                                       .EvaluateAsync(ctx, RuleScope.Import)
                                       .ConfigureAwait(false);
                        return (Staging: s, Result: res);
                    }).ToList();

                    var batchResults = await Task.WhenAll(batchTasks);
                    allEvalResults.AddRange(batchResults);

                    if (i > 0 && i % 10000 == 0)
                        LogManager.Info($"[PERF] Rules evaluation progress: {i}/{staged.Count} records processed");
                }

                /* --------------------------------------------------------------
                 * 2️⃣  Application des sorties SELF
                 * -------------------------------------------------------------- */
                foreach (var tuple in allEvalResults)
                {
                    var s = tuple.Staging;
                    var res = tuple.Result;

                    if (res == null || res.Rule == null) continue;

                    /* ----- ne pas écraser les lignes déjà traitées par MANUAL_OUTGOING ----- */
                    //if (s.Reconciliation.Action.HasValue && s.Reconciliation.KPI.HasValue)
                    //    continue;

                    /* ------------------- SELF ------------------- */
                    if (res.Rule.ApplyTo == ApplyTarget.Self || res.Rule.ApplyTo == ApplyTarget.Both)
                    {
                        RuleApplicationHelper.ApplyOutputs(res, s.Reconciliation, _currentUser);
                        try
                        {
                            var summary = RuleApplicationHelper.BuildOutputSummary(res);
                            LogHelper.WriteRuleApplied("import", countryId, s.Reconciliation?.ID, res.Rule.RuleId, summary, res.UserMessage);
                        }
                        catch { }
                    }

                    /* ------------------- COUNTERPART ------------------- */
                    if ((res.Rule.ApplyTo == ApplyTarget.Counterpart || res.Rule.ApplyTo == ApplyTarget.Both)
                        && !string.IsNullOrWhiteSpace(s.Bgi))
                    {
                        var targetIsPivot = !s.IsPivot;   // le côté opposé
                        counterpartIntents.Add((
                            Bgi: s.Bgi.Trim().ToUpperInvariant(),
                            TargetIsPivot: targetIsPivot,
                            RuleId: res.Rule.RuleId,
                            ActionId: res.Rule.OutputActionId,
                            KgiId: res.Rule.OutputKpiId,          // ← garder le même nom que votre table si vous avez ce champ
                            IncidentTypeId: res.Rule.OutputIncidentTypeId,
                            RiskyItem: res.Rule.OutputRiskyItem,
                            ReasonNonRiskyId: res.Rule.OutputReasonNonRiskyId,
                            ToRemind: res.Rule.OutputToRemind,
                            ToRemindDays: res.Rule.OutputToRemindDays));
                    }
                }

                /* --------------------------------------------------------------
                 * 3️⃣  Deuxième passe – application des intentions COUNTERPART
                 * -------------------------------------------------------------- */
                if (counterpartIntents.Count > 0)
                {
                    var byBgi = staged
                        .Where(x => !string.IsNullOrWhiteSpace(x.Bgi))
                        .GroupBy(x => x.Bgi.Trim().ToUpperInvariant())
                        .ToDictionary(g => g.Key, g => g.ToList());

                    foreach (var intent in counterpartIntents)
                    {
                        if (!byBgi.TryGetValue(intent.Bgi, out var rows)) continue;

                        foreach (var row in rows)
                        {
                            if (row.IsPivot != intent.TargetIsPivot) continue;

                            if (intent.ActionId.HasValue) row.Reconciliation.Action = intent.ActionId.Value;
                            if (intent.KgiId.HasValue) row.Reconciliation.KPI = intent.KgiId.Value;      // ou autre champ selon votre modèle
                            if (intent.IncidentTypeId.HasValue) row.Reconciliation.IncidentType = intent.IncidentTypeId.Value;
                            if (intent.RiskyItem.HasValue) row.Reconciliation.RiskyItem = intent.RiskyItem.Value;
                            if (intent.ReasonNonRiskyId.HasValue) row.Reconciliation.ReasonNonRisky = intent.ReasonNonRiskyId.Value;
                            if (intent.ToRemind.HasValue) row.Reconciliation.ToRemind = intent.ToRemind.Value;
                            if (intent.ToRemindDays.HasValue)
                            {
                                try { row.Reconciliation.ToRemindDate = DateTime.Today.AddDays(intent.ToRemindDays.Value); }
                                catch { }
                            }

                            // Log counterpart (facultatif)
                            try
                            {
                                var parts = new List<string>();
                                if (intent.ActionId.HasValue) parts.Add($"Action={intent.ActionId.Value}");
                                if (intent.KgiId.HasValue) parts.Add($"KPI={intent.KgiId.Value}");
                                if (intent.IncidentTypeId.HasValue) parts.Add($"IncidentType={intent.IncidentTypeId.Value}");
                                if (intent.RiskyItem.HasValue) parts.Add($"RiskyItem={intent.RiskyItem.Value}");
                                if (intent.ReasonNonRiskyId.HasValue) parts.Add($"ReasonNonRisky={intent.ReasonNonRiskyId.Value}");
                                if (intent.ToRemind.HasValue) parts.Add($"ToRemind={intent.ToRemind.Value}");
                                if (intent.ToRemindDays.HasValue) parts.Add($"ToRemindDays={intent.ToRemindDays.Value}");

                                var outStr = string.Join("; ", parts);
                                LogHelper.WriteRuleApplied("import", countryId, row.Reconciliation?.ID,
                                                           intent.RuleId, outStr, "Counterpart application");
                            }
                            catch { }
                        }
                    }
                }

                /* --------------------------------------------------------------
                 * 4️⃣  Fallback – aucune règle n’a touché la ligne (seulement pour
                 *      les nouvelles lignes)
                 * -------------------------------------------------------------- */
                if (isNewLines)
                {
                    const int ACTION_INVESTIGATE = (int)ActionType.Investigate;   // 7
                    int fallbackCount = 0;

                    foreach (var tuple in allEvalResults)
                    {
                        var s = tuple.Staging;
                        var res = tuple.Result;

                        // Si une action a déjà été mise (MANUAL_OUTGOING, fallback, etc.) on ne touche pas
                        if (s.Reconciliation.Action.HasValue) continue;

                        // Aucun résultat de règle → appliquer le fallback
                        if (res == null || res.Rule == null)
                        {
                            s.Reconciliation.Action = ACTION_INVESTIGATE;
                            s.Reconciliation.ActionStatus = true;          // DONE
                            s.Reconciliation.ActionDate = DateTime.Now;

                            // commentaire d’audit
                            var prefix = $"[{DateTime.Now:yyyy-MM-dd HH:mm}] {_currentUser}: ";
                            var msg = $"{prefix}New line set to INVESTIGATE – no matching rule found";
                            if (string.IsNullOrWhiteSpace(s.Reconciliation.Comments))
                                s.Reconciliation.Comments = msg;
                            else if (!s.Reconciliation.Comments.Contains("no matching rule found"))
                                s.Reconciliation.Comments = msg + Environment.NewLine + s.Reconciliation.Comments;

                            fallbackCount++;

                            // log fallback
                            try
                            {
                                LogHelper.WriteRuleApplied("import", countryId, s.Reconciliation?.ID,
                                                           "FALLBACK_INVESTIGATE",
                                                           $"Action={ACTION_INVESTIGATE}",
                                                           "No matching rule – default to INVESTIGATE");
                            }
                            catch { }
                        }
                    }

                    if (fallbackCount > 0)
                        LogManager.Info($"[FALLBACK RULE] Applied INVESTIGATE to {fallbackCount} line(s) (no matching rule).");
                }

                /* --------------------------------------------------------------
                 * 5️⃣ Post‑processing – ReasonNonRisky & IT‑Issue → INVESTIGATE
                 * -------------------------------------------------------------- */
                const int REASON_NOT_OBSERVED = (int)Risky.NoObservedRiskExpectedDelay;   // 32
                const int REASON_COMMISSION_PAID = (int)Risky.CollectedCommissionsCredit67P; // 30
                const int ACTION_INVEST = (int)ActionType.Investigate;               // 7
                const int KPI_IT_ISSUES = (int)KPIType.ITIssues;                     // 19

                foreach (var s in staged)
                {
                    var rec = s.Reconciliation;
                    if (rec == null) continue;

                    /* ----- ReasonNonRisky automatique ----- */
                    if (rec.ActionStatus == true && !rec.ReasonNonRisky.HasValue)
                    {
                        // heuristique : si la ligne possède déjà un TriggerDate, on considère que c’est le système qui a déjà déclenché
                        bool systemTrigger = rec.TriggerDate.HasValue && rec.TriggerDate.Value < DateTime.Today;
                        rec.ReasonNonRisky = systemTrigger ? REASON_COMMISSION_PAID : REASON_NOT_OBSERVED;
                    }

                    /* ----- IT Issue → FORCE INVESTIGATE (DONE) ----- */
                    if (rec.KPI.HasValue && rec.KPI.Value == KPI_IT_ISSUES)
                    {
                        rec.Action = ACTION_INVEST;
                        rec.ActionStatus = true;            // DONE
                        rec.ActionDate = DateTime.Now;
                    }
                }

                /* --------------------------------------------------------------
                 * 6️⃣ Normalisation finale de ActionStatus / ActionDate
                 * -------------------------------------------------------------- */
                try
                {
                    var allUserFields = _offlineFirstService?.UserFields;
                    var nowLocal = DateTime.Now;

                    foreach (var s in staged)
                    {
                        var rec = s?.Reconciliation;
                        if (rec == null) continue;

                        // Si ActionStatus a déjà été fixé (par la règle ou le fallback) on le garde,
                        // sinon on applique la règle « NA = DONE, sinon PENDING »
                        if (!rec.ActionStatus.HasValue)
                        {
                            bool isNa = !rec.Action.HasValue || UserFieldUpdateService.IsActionNA(rec.Action, allUserFields);
                            rec.ActionStatus = isNa;               // true = DONE, false = PENDING
                        }

                        // Toujours garder une date d’action lorsqu’une Action (ou un statut) existe
                        if (rec.Action.HasValue || rec.ActionStatus.HasValue)
                        {
                            rec.ActionDate = nowLocal;
                        }
                    }
                }
                catch { /* non‑bloquant */ }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Truth‑table rules application failed: {ex.Message}");
            }
        }

        private async Task ApplyReconciliationChangesAsync(
            List<Reconciliation> toInsert,
            List<DataAmbre> toUpdate,
            List<DataAmbre> toArchive,
            string countryId)
        {
            var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
            
            using (var conn = new OleDbConnection(connectionString))
            {
                await conn.OpenAsync();

                // Unarchive updated records
                if (toUpdate.Any())
                {
                    await UnarchiveRecordsAsync(conn, toUpdate);
                }

                // Archive deleted records
                if (toArchive.Any())
                {
                    await ArchiveRecordsAsync(conn, toArchive);
                }

                // Insert new reconciliations
                if (toInsert.Any())
                {
                    await InsertReconciliationsAsync(conn, toInsert);
                }
            }
        }

        private async Task UpdateDwingsReferencesForUpdatesAsync(
            List<DataAmbre> updatedRecords,
            Country country,
            string countryId)
        {
            try
            {
                if (updatedRecords == null || updatedRecords.Count == 0) return;
                var invoices = await _reconciliationService.GetDwingsInvoicesAsync();
                var dwList = invoices?.ToList() ?? new List<DwingsInvoiceDto>();

                var guarantees = await _reconciliationService.GetDwingsGuaranteesAsync();
                var dwGuaranteeList = guarantees?.ToList();

                // PERF: Build lookups once before resolution loop
                var lookup = new DwingsInvoiceLookup(dwList);
                _dwingsResolver.SetInvoiceLookup(lookup);
                _dwingsResolver.PreBuildLookups(dwList, dwGuaranteeList);

                // PERF: Phase 1 — resolve all references in memory (CPU-bound string matching)
                var resolvedRefs = new List<(string Id, Ambre.DwingsTokens Refs)>();
                foreach (var amb in updatedRecords)
                {
                    if (amb == null || string.IsNullOrWhiteSpace(amb.ID)) continue;
                    bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);
                    var refs = _dwingsResolver.ResolveReferences(amb, isPivot, dwList, dwGuaranteeList);
                    if (refs != null && (!string.IsNullOrWhiteSpace(refs.InvoiceId)
                                      || !string.IsNullOrWhiteSpace(refs.CommissionId)
                                      || !string.IsNullOrWhiteSpace(refs.GuaranteeId)))
                    {
                        resolvedRefs.Add((amb.ID, refs));
                    }
                }

                if (resolvedRefs.Count == 0) return;

                // PERF: Phase 2 — single prepared UPDATE per row (merges 3 separate UPDATEs into 1)
                var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
                using (var conn = new OleDbConnection(connectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);
                    using (var tx = conn.BeginTransaction())
                    {
                        try
                        {
                            var nowUtc = DateTime.UtcNow;

                            using (var cmd = new OleDbCommand(
                                "UPDATE [T_Reconciliation] SET " +
                                "[DWINGS_InvoiceID] = IIF(([DWINGS_InvoiceID] IS NULL OR [DWINGS_InvoiceID] = '') AND ? <> '', ?, [DWINGS_InvoiceID]), " +
                                "[DWINGS_BGPMT] = IIF(([DWINGS_BGPMT] IS NULL OR [DWINGS_BGPMT] = '') AND ? <> '', ?, [DWINGS_BGPMT]), " +
                                "[DWINGS_GuaranteeID] = IIF(([DWINGS_GuaranteeID] IS NULL OR [DWINGS_GuaranteeID] = '') AND ? <> '', ?, [DWINGS_GuaranteeID]), " +
                                "[LastModified]=?, [ModifiedBy]=? " +
                                "WHERE [ID]=?", conn, tx))
                            {
                                cmd.Parameters.Add("@InvCheck", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@InvVal", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@BgpmtCheck", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@BgpmtVal", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@GuarCheck", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@GuarVal", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@LastModified", OleDbType.Date);
                                cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255);

                                cmd.Prepare();

                                foreach (var item in resolvedRefs)
                                {
                                    var inv = item.Refs.InvoiceId ?? string.Empty;
                                    var bgp = item.Refs.CommissionId ?? string.Empty;
                                    var guar = item.Refs.GuaranteeId ?? string.Empty;

                                    cmd.Parameters["@InvCheck"].Value = inv;
                                    cmd.Parameters["@InvVal"].Value = inv;
                                    cmd.Parameters["@BgpmtCheck"].Value = bgp;
                                    cmd.Parameters["@BgpmtVal"].Value = bgp;
                                    cmd.Parameters["@GuarCheck"].Value = guar;
                                    cmd.Parameters["@GuarVal"].Value = guar;
                                    cmd.Parameters["@LastModified"].Value = nowUtc;
                                    cmd.Parameters["@ModifiedBy"].Value = _currentUser;
                                    cmd.Parameters["@ID"].Value = item.Id;

                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }

                            tx.Commit();
                            LogManager.Info($"[PERF] Backfilled DWINGS refs for {resolvedRefs.Count} records");
                        }
                        catch
                        {
                            tx.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Backfill DWINGS refs failed: {ex.Message}");
            }
        }

        /// -------------------------------------------------------------------
        /// 1️⃣  Si une action a été marquée DONE → assigner automatiquement
        ///     le ReasonNonRisky adéquat
        /// -------------------------------------------------------------------
        private void AutoSetReasonNonRisky(List<ReconciliationStaging> staged)
        {
            const int REASON_WE_DO_NOT_OBSERVE = 32; // “We do not observe risk …”
            const int REASON_COMMISSION_PAID = 30; // “Commissions already collected …”

            foreach (var s in staged)
            {
                var rec = s.Reconciliation;
                if (!rec.ActionStatus.HasValue) continue; // aucune action appliquée

                // 1️⃣ User‑triggered (ActionStatus = true && ReasonNonRisky is null)
                if (!rec.ReasonNonRisky.HasValue)
                {
                    // le trigger vient d’être appliqué automatiquement ? → on regarde le trigger date
                    bool systemTrigger = rec.TriggerDate.HasValue && rec.TriggerDate.Value < DateTime.Today;
                    rec.ReasonNonRisky = systemTrigger ? REASON_COMMISSION_PAID : REASON_WE_DO_NOT_OBSERVE;
                }
            }
        }

        /// -------------------------------------------------------------------
        /// 2️⃣  KPI = IT ISSUE ⇒ forcer Action = INVESTIGATE (DONE)
        /// -------------------------------------------------------------------
        private void EnforceItIssueAction(List<ReconciliationStaging> staged)
        {
            const int KPI_IT_ISSUE = (int)KPIType.ITIssues;   // 19
            const int ACTION_INVEST = (int)ActionType.Investigate; // 7

            foreach (var s in staged)
            {
                if (s.Reconciliation?.KPI == KPI_IT_ISSUE)
                {
                    s.Reconciliation.Action = ACTION_INVEST;
                    s.Reconciliation.ActionStatus = true;                // DONE
                }
            }
        }

        /// -------------------------------------------------------------------
        /// 3️⃣  Fallback : aucune règle n’a matché → action INVESTIGATE (DONE)
        /// -------------------------------------------------------------------
        private void ApplyFallbackRule(List<ReconciliationStaging> staged)
        {
            const int ACTION_INVEST = (int)ActionType.Investigate; // 7

            foreach (var s in staged)
            {
                // Si aucune colonne d’action ou de KPI n’a été remplie par le moteur…
                if (!s.Reconciliation.Action.HasValue && !s.Reconciliation.KPI.HasValue)
                {
                    s.Reconciliation.Action = ACTION_INVEST;
                    s.Reconciliation.ActionStatus = true;   // DONE
                }
            }
        }

        /// <summary>
        /// Réapplique les règles de truth-table aux enregistrements existants (ToUpdate).
        /// Cela permet de mettre à jour Action, KPI, IncidentType, etc. selon les règles actuelles.
        /// </summary>
        private async Task ApplyRulesToExistingRecordsAsync(
            List<DataAmbre> updatedRecords,
            Country country,
            string countryId,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            IReadOnlyList<DwingsGuaranteeDto> dwGuarantees)
        {
            try
            {
                if (updatedRecords == null || updatedRecords.Count == 0) return;

                var timer = System.Diagnostics.Stopwatch.StartNew();
                LogManager.Info($"[PERF] Applying truth-table rules to {updatedRecords.Count} existing records");

                // DWINGS data passed from caller to avoid reloading
                _transformationService = new TransformationService(new List<Country> { country });

                // Load existing reconciliations from database
                var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
                var reconciliations = new Dictionary<string, Reconciliation>(StringComparer.OrdinalIgnoreCase);
                
                using (var conn = new OleDbConnection(connectionString))
                {
                    await conn.OpenAsync();
                    
                    // Batch load reconciliations (Access IN clause limit ~1000)
                    var ids = updatedRecords.Select(r => r.ID).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    const int batchSize = 500;
                    
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        var batch = ids.Skip(i).Take(batchSize).ToList();
                        var inClause = string.Join(",", batch.Select((_, idx) => "?"));
                        
                        using (var cmd = new OleDbCommand(
                            $"SELECT * FROM [T_Reconciliation] WHERE [ID] IN ({inClause})", conn))
                        {
                            foreach (var id in batch)
                                cmd.Parameters.AddWithValue("@ID", id);
                            
                            using (var reader = await cmd.ExecuteReaderAsync())
                            {
                                while (await reader.ReadAsync())
                                {
                                    var rec = MapReconciliationFromReader(reader);
                                    if (rec != null && !string.IsNullOrWhiteSpace(rec.ID))
                                        reconciliations[rec.ID] = rec;
                                }
                            }
                        }
                    }
                }

                // Create staging items
                var staged = new List<ReconciliationStaging>();
                foreach (var dataAmbre in updatedRecords)
                {
                    if (!reconciliations.TryGetValue(dataAmbre.ID, out var reconciliation))
                        continue;

                    staged.Add(new ReconciliationStaging
                    {
                        Reconciliation = reconciliation,
                        DataAmbre = dataAmbre,
                        IsPivot = dataAmbre.IsPivotAccount(country.CNT_AmbrePivot),
                        Bgi = reconciliation.DWINGS_InvoiceID
                    });
                }

                if (staged.Count == 0)
                {
                    LogManager.Info("No reconciliations found for existing records");
                    return;
                }

                // Calculate KPIs (IsGrouped, MissingAmount)
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
                LogManager.Info($"[PERF] KPI calculation completed for {staged.Count} existing records in {kpiTimer.ElapsedMilliseconds}ms");

                // Apply special MANUAL_OUTGOING pairing rule FIRST (before truth-table rules)
                // This prevents truth-table from overwriting guarantee-based matches
                try
                {
                    var manualOutgoingMatches = await _reconciliationService.ApplyManualOutgoingRuleAsync(countryId).ConfigureAwait(false);
                    if (manualOutgoingMatches > 0)
                    {
                        LogManager.Info($"MANUAL_OUTGOING rule: matched {manualOutgoingMatches} pair(s) - these will be excluded from truth-table rules");
                        
                        // Reload reconciliations that were updated by MANUAL_OUTGOING rule
                        // to ensure staged items have the latest Action/KPI values
                        using (var conn = new OleDbConnection(connectionString))
                        {
                            await conn.OpenAsync();
                            var ids = staged.Select(s => s.Reconciliation.ID).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            const int reloadBatchSize = 500;
                            
                            for (int i = 0; i < ids.Count; i += reloadBatchSize)
                            {
                                var batch = ids.Skip(i).Take(reloadBatchSize).ToList();
                                var inClause = string.Join(",", batch.Select((_, idx) => "?"));
                                
                                using (var cmd = new OleDbCommand(
                                    $"SELECT * FROM [T_Reconciliation] WHERE [ID] IN ({inClause})", conn))
                                {
                                    foreach (var id in batch)
                                        cmd.Parameters.AddWithValue("@ID", id);
                                    
                                    using (var reader = await cmd.ExecuteReaderAsync())
                                    {
                                        while (await reader.ReadAsync())
                                        {
                                            var rec = MapReconciliationFromReader(reader);
                                            if (rec != null && !string.IsNullOrWhiteSpace(rec.ID))
                                            {
                                                // Update staged item with fresh data
                                                var stagedItem = staged.FirstOrDefault(s => string.Equals(s.Reconciliation.ID, rec.ID, StringComparison.OrdinalIgnoreCase));
                                                if (stagedItem != null)
                                                {
                                                    stagedItem.Reconciliation = rec;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Error applying MANUAL_OUTGOING rule: {ex.Message}", ex);
                }

                // Apply truth-table rules (skip lines already processed by MANUAL_OUTGOING)
                var rulesTimer = System.Diagnostics.Stopwatch.StartNew();
                LogManager.Info($"Evaluating truth-table rules for {staged.Count} existing records...");
                // isNewLines=false disables FALLBACK rule (existing lines should keep their current state if no rule matches)
                await ApplyTruthTableRulesAsync(staged, country, countryId, dwInvoices, dwGuarantees, isNewLines: false);

                // Count how many had rules applied
                rulesTimer.Stop();
                int rulesAppliedCount = staged.Count(s => s.Reconciliation.Action.HasValue || s.Reconciliation.KPI.HasValue);
                LogManager.Info($"[PERF] Rules evaluation complete: {rulesAppliedCount}/{staged.Count} records had rules applied in {rulesTimer.ElapsedMilliseconds}ms");

                // Update database with rule results - OPTIMIZED with batching
                var dbUpdateTimer = System.Diagnostics.Stopwatch.StartNew();
                using (var conn = new OleDbConnection(connectionString))
                {
                    await conn.OpenAsync();
                    using (var tx = conn.BeginTransaction())
                    {
                        try
                        {
                            var nowUtc = DateTime.UtcNow;
                            const int dbBatchSize = 500; // Batch DB updates for better performance
                            int updateCount = 0;
                            
                            using (var cmd = new OleDbCommand(
                                @"UPDATE [T_Reconciliation] SET 
                                    [Action]=?, [KPI]=?, [IncidentType]=?, [RiskyItem]=?, [ReasonNonRisky]=?,
                                    [ToRemind]=?, [ToRemindDate]=?, [FirstClaimDate]=?,
                                    [LastModified]=?, [ModifiedBy]=?
                                  WHERE [ID]=?", conn, tx))
                            {
                                // Pre-create parameters once with explicit sizes for VarChar
                                cmd.Parameters.Add("@Action", OleDbType.Integer);
                                cmd.Parameters.Add("@KPI", OleDbType.Integer);
                                cmd.Parameters.Add("@IncidentType", OleDbType.Integer);
                                cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean);
                                cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer);
                                cmd.Parameters.Add("@ToRemind", OleDbType.Boolean);
                                cmd.Parameters.Add("@ToRemindDate", OleDbType.Date);
                                cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date);
                                cmd.Parameters.Add("@LastModified", OleDbType.Date);
                                cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255);
                                cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255);
                                
                                cmd.Prepare(); // Prepare statement once (requires explicit sizes for VarChar)
                                
                                foreach (var s in staged)
                                {
                                    var rec = s.Reconciliation;
                                    
                                    cmd.Parameters["@Action"].Value = rec.Action.HasValue ? (object)rec.Action.Value : DBNull.Value;
                                    cmd.Parameters["@KPI"].Value = rec.KPI.HasValue ? (object)rec.KPI.Value : DBNull.Value;
                                    cmd.Parameters["@IncidentType"].Value = rec.IncidentType.HasValue ? (object)rec.IncidentType.Value : DBNull.Value;
                                    cmd.Parameters["@RiskyItem"].Value = rec.RiskyItem.HasValue ? (object)rec.RiskyItem.Value : DBNull.Value;
                                    cmd.Parameters["@ReasonNonRisky"].Value = rec.ReasonNonRisky.HasValue ? (object)rec.ReasonNonRisky.Value : DBNull.Value;
                                    cmd.Parameters["@ToRemind"].Value = rec.ToRemind;
                                    cmd.Parameters["@ToRemindDate"].Value = rec.ToRemindDate.HasValue ? (object)rec.ToRemindDate.Value : DBNull.Value;
                                    cmd.Parameters["@FirstClaimDate"].Value = rec.FirstClaimDate.HasValue ? (object)rec.FirstClaimDate.Value : DBNull.Value;
                                    cmd.Parameters["@LastModified"].Value = nowUtc;
                                    cmd.Parameters["@ModifiedBy"].Value = _currentUser;
                                    cmd.Parameters["@ID"].Value = rec.ID;
                                    
                                    await cmd.ExecuteNonQueryAsync();
                                    updateCount++;
                                    
                                    // Periodic progress log
                                    if (updateCount % 10000 == 0)
                                        LogManager.Info($"[PERF] DB update progress: {updateCount}/{staged.Count} records updated");
                                }
                            }
                            
                            tx.Commit();
                            dbUpdateTimer.Stop();
                            LogManager.Info($"[PERF] DB updates completed: {staged.Count} records in {dbUpdateTimer.ElapsedMilliseconds}ms");
                        }
                        catch
                        {
                            tx.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Failed to apply rules to existing records: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Maps a Reconciliation object from a DataReader
        /// </summary>
        private Reconciliation MapReconciliationFromReader(System.Data.Common.DbDataReader reader)
        {
            try
            {
                return new Reconciliation
                {
                    ID = reader["ID"]?.ToString(),
                    DWINGS_GuaranteeID = reader["DWINGS_GuaranteeID"]?.ToString(),
                    DWINGS_InvoiceID = reader["DWINGS_InvoiceID"]?.ToString(),
                    DWINGS_BGPMT = reader["DWINGS_BGPMT"]?.ToString(),
                    Action = reader["Action"] as int?,
                    ActionStatus = reader["ActionStatus"] as bool?,
                    ActionDate = reader["ActionDate"] as DateTime?,
                    Comments = reader["Comments"]?.ToString(),
                    InternalInvoiceReference = reader["InternalInvoiceReference"]?.ToString(),
                    FirstClaimDate = reader["FirstClaimDate"] as DateTime?,
                    LastClaimDate = reader["LastClaimDate"] as DateTime?,
                    ToRemind = (reader["ToRemind"] as bool?) ?? false,
                    SwiftCode = reader["SwiftCode"]?.ToString(),
                    PaymentReference = reader["PaymentReference"]?.ToString(),
                    KPI = reader["KPI"] as int?,
                    IncidentType = reader["IncidentType"] as int?,
                    RiskyItem = reader["RiskyItem"] as bool?,
                    ReasonNonRisky = reader["ReasonNonRisky"] as int?,
                    TriggerDate = reader["TriggerDate"] as DateTime?,
                    CreationDate = reader["CreationDate"] as DateTime?,
                    ModifiedBy = reader["ModifiedBy"]?.ToString(),
                    LastModified = reader["LastModified"] as DateTime?
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task UnarchiveRecordsAsync(OleDbConnection conn, List<DataAmbre> records)
        {
            var ids = records
                .Select(d => d?.ID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ids.Any()) return;

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    
                    // OPTIMIZATION: Batch update with IN clause (Access supports up to ~1000 items)
                    const int batchSize = 500;
                    int totalCount = 0;
                    
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        var batch = ids.Skip(i).Take(batchSize).ToList();
                        var inClause = string.Join(",", batch.Select((_, idx) => $"?"));
                        
                        using (var cmd = new OleDbCommand(
                            $"UPDATE [T_Reconciliation] SET [DeleteDate]=NULL, [LastModified]=?, [ModifiedBy]=? " +
                            $"WHERE [ID] IN ({inClause}) AND [DeleteDate] IS NOT NULL", conn, tx))
                        {
                            cmd.Parameters.Add("@LastModified", OleDbType.Date).Value = nowUtc;
                            cmd.Parameters.AddWithValue("@ModifiedBy", _currentUser);
                            foreach (var id in batch)
                            {
                                cmd.Parameters.AddWithValue("@ID", id);
                            }
                            totalCount += await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    
                    tx.Commit();
                    LogManager.Info($"Unarchived {totalCount} reconciliation record(s)");
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private async Task ArchiveRecordsAsync(OleDbConnection conn, List<DataAmbre> records)
        {
            var ids = records
                .Select(d => d?.ID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ids.Any()) return;

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    
                    // OPTIMIZATION: Batch update with IN clause
                    const int batchSize = 500;
                    int totalCount = 0;
                    
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        var batch = ids.Skip(i).Take(batchSize).ToList();
                        var inClause = string.Join(",", batch.Select((_, idx) => $"?"));
                        
                        using (var cmd = new OleDbCommand(
                            $"UPDATE [T_Reconciliation] SET [DeleteDate]=?, [LastModified]=?, [ModifiedBy]=? " +
                            $"WHERE [ID] IN ({inClause}) AND [DeleteDate] IS NULL", conn, tx))
                        {
                            cmd.Parameters.Add("@DeleteDate", OleDbType.Date).Value = nowUtc;
                            cmd.Parameters.Add("@LastModified", OleDbType.Date).Value = nowUtc;
                            cmd.Parameters.AddWithValue("@ModifiedBy", _currentUser);
                            foreach (var id in batch)
                            {
                                cmd.Parameters.AddWithValue("@ID", id);
                            }
                            totalCount += await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    
                    tx.Commit();
                    LogManager.Info($"Archived {totalCount} reconciliation record(s)");
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private async Task InsertReconciliationsAsync(OleDbConnection conn, List<Reconciliation> reconciliations)
        {
            // Get existing IDs to ensure insert-only
            var existingIds = await GetExistingIdsAsync(conn, reconciliations.Select(r => r.ID).ToList());
            var toInsert = reconciliations.Where(r => !existingIds.Contains(r.ID)).ToList();
            if (toInsert.Count == 0) return;

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    int insertedCount = 0;

                    // PERF: Create a single prepared command and reuse it for all inserts
                    // (avoids 20k+ OleDbCommand allocations + parameter setup)
                    using (var cmd = new OleDbCommand(@"INSERT INTO [T_Reconciliation] (
                        [ID],[DWINGS_GuaranteeID],[DWINGS_InvoiceID],[DWINGS_BGPMT],
                        [Action],[ActionStatus],[ActionDate],[Comments],[InternalInvoiceReference],[FirstClaimDate],[LastClaimDate],
                        [ToRemind],[ToRemindDate],[ACK],[SwiftCode],[PaymentReference],[MbawData],[SpiritData],[KPI],
                        [IncidentType],[RiskyItem],[ReasonNonRisky],[CreationDate],[ModifiedBy],[LastModified]
                    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", conn, tx))
                    {
                        // Pre-create parameters once with explicit types
                        cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@DWINGS_GuaranteeID", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@DWINGS_InvoiceID", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@DWINGS_BGPMT", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@Action", OleDbType.Integer);
                        cmd.Parameters.Add("@ActionStatus", OleDbType.Boolean);
                        cmd.Parameters.Add("@ActionDate", OleDbType.Date);
                        cmd.Parameters.Add("@Comments", OleDbType.LongVarWChar, int.MaxValue);
                        cmd.Parameters.Add("@InternalInvoiceReference", OleDbType.VarWChar, 255);
                        
                        cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date);
                        cmd.Parameters.Add("@LastClaimDate", OleDbType.Date);
                        cmd.Parameters.Add("@ToRemind", OleDbType.Boolean);
                        cmd.Parameters.Add("@ToRemindDate", OleDbType.Date);
                        cmd.Parameters.Add("@ACK", OleDbType.Boolean);
                        
                        cmd.Parameters.Add("@SwiftCode", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@PaymentReference", OleDbType.VarWChar, 255);
                        // Long text fields
                        var pMbaw = cmd.Parameters.Add("@MbawData", OleDbType.LongVarWChar, int.MaxValue);
                        pMbaw.Value = DBNull.Value;
                        var pSpirit = cmd.Parameters.Add("@SpiritData", OleDbType.LongVarWChar, int.MaxValue);
                        pSpirit.Value = DBNull.Value;
                        
                        cmd.Parameters.Add("@KPI", OleDbType.Integer);
                        cmd.Parameters.Add("@IncidentType", OleDbType.Integer);
                        cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean);
                        cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer);
                        cmd.Parameters.Add("@CreationDate", OleDbType.Date);
                        
                        cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255);
                        
                        cmd.Parameters.Add("@LastModified", OleDbType.Date);

                        cmd.Prepare();

                        foreach (var rec in toInsert)
                        {
                            cmd.Parameters["@ID"].Value = (object)rec.ID ?? DBNull.Value;
                            cmd.Parameters["@DWINGS_GuaranteeID"].Value = (object)rec.DWINGS_GuaranteeID ?? DBNull.Value;
                            cmd.Parameters["@DWINGS_InvoiceID"].Value = (object)rec.DWINGS_InvoiceID ?? DBNull.Value;
                            cmd.Parameters["@DWINGS_BGPMT"].Value = (object)rec.DWINGS_BGPMT ?? DBNull.Value;
                            
                            cmd.Parameters["@Action"].Value = rec.Action.HasValue ? (object)rec.Action.Value : DBNull.Value;
                            cmd.Parameters["@ActionStatus"].Value = rec.ActionStatus.HasValue ? (object)rec.ActionStatus.Value : DBNull.Value;
                            cmd.Parameters["@ActionDate"].Value = rec.ActionDate.HasValue ? (object)rec.ActionDate.Value : DBNull.Value;
                            cmd.Parameters["@Comments"].Value = (object)rec.Comments ?? DBNull.Value;
                            cmd.Parameters["@InternalInvoiceReference"].Value = (object)rec.InternalInvoiceReference ?? DBNull.Value;
                            
                            cmd.Parameters["@FirstClaimDate"].Value = 
                                rec.FirstClaimDate.HasValue ? (object)rec.FirstClaimDate.Value : DBNull.Value;
                            cmd.Parameters["@LastClaimDate"].Value = 
                                rec.LastClaimDate.HasValue ? (object)rec.LastClaimDate.Value : DBNull.Value;
                            cmd.Parameters["@ToRemind"].Value = rec.ToRemind;
                            cmd.Parameters["@ToRemindDate"].Value = 
                                rec.ToRemindDate.HasValue ? (object)rec.ToRemindDate.Value : DBNull.Value;
                            cmd.Parameters["@ACK"].Value = rec.ACK;
                            
                            cmd.Parameters["@SwiftCode"].Value = (object)rec.SwiftCode ?? DBNull.Value;
                            cmd.Parameters["@PaymentReference"].Value = (object)rec.PaymentReference ?? DBNull.Value;
                            pMbaw.Value = rec.MbawData ?? (object)DBNull.Value;
                            pSpirit.Value = rec.SpiritData ?? (object)DBNull.Value;
                            
                            cmd.Parameters["@KPI"].Value = 
                                rec.KPI.HasValue ? (object)rec.KPI.Value : DBNull.Value;
                            cmd.Parameters["@IncidentType"].Value = 
                                rec.IncidentType.HasValue ? (object)rec.IncidentType.Value : DBNull.Value;
                            cmd.Parameters["@RiskyItem"].Value = 
                                rec.RiskyItem.HasValue ? (object)rec.RiskyItem.Value : DBNull.Value;
                            cmd.Parameters["@ReasonNonRisky"].Value = 
                                rec.ReasonNonRisky.HasValue ? (object)rec.ReasonNonRisky.Value : DBNull.Value;
                            cmd.Parameters["@CreationDate"].Value = 
                                rec.CreationDate.HasValue ? (object)rec.CreationDate.Value : DBNull.Value;
                            
                            cmd.Parameters["@ModifiedBy"].Value = (object)rec.ModifiedBy ?? DBNull.Value;
                            
                            cmd.Parameters["@LastModified"].Value = 
                                rec.LastModified.HasValue ? (object)rec.LastModified.Value : DBNull.Value;

                            insertedCount += await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    tx.Commit();
                    LogManager.Info($"Inserted {insertedCount} new reconciliation record(s)");
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private async Task<HashSet<string>> GetExistingIdsAsync(OleDbConnection conn, List<string> ids)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            if (!ids.Any()) return existing;

            const int chunkSize = 500;
            for (int i = 0; i < ids.Count; i += chunkSize)
            {
                var chunk = ids.Skip(i).Take(chunkSize).ToList();
                var placeholders = string.Join(",", Enumerable.Repeat("?", chunk.Count));
                
                using (var cmd = new OleDbCommand(
                    $"SELECT [ID] FROM [T_Reconciliation] WHERE [ID] IN ({placeholders})", conn))
                {
                    foreach (var id in chunk)
                        cmd.Parameters.AddWithValue("@ID", id);
                        
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var id = reader[0]?.ToString();
                            if (!string.IsNullOrWhiteSpace(id))
                                existing.Add(id);
                        }
                    }
                }
            }
            
            return existing;
        }

        private OleDbCommand CreateInsertCommand(OleDbConnection conn, OleDbTransaction tx, Reconciliation rec)
        {
            var cmd = new OleDbCommand(@"INSERT INTO [T_Reconciliation] (
                [ID],[DWINGS_GuaranteeID],[DWINGS_InvoiceID],[DWINGS_BGPMT],
                [Action],[ActionStatus],[ActionDate],[Comments],[InternalInvoiceReference],[FirstClaimDate],[LastClaimDate],
                [ToRemind],[ToRemindDate],[ACK],[SwiftCode],[PaymentReference],[MbawData],[SpiritData],[KPI],
                [IncidentType],[RiskyItem],[ReasonNonRisky],[CreationDate],[ModifiedBy],[LastModified]
            ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", conn, tx);

            // Add parameters in order
            cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255).Value = (object)rec.ID ?? DBNull.Value;
            cmd.Parameters.Add("@DWINGS_GuaranteeID", OleDbType.VarWChar, 255).Value = (object)rec.DWINGS_GuaranteeID ?? DBNull.Value;
            cmd.Parameters.Add("@DWINGS_InvoiceID", OleDbType.VarWChar, 255).Value = (object)rec.DWINGS_InvoiceID ?? DBNull.Value;
            cmd.Parameters.Add("@DWINGS_BGPMT", OleDbType.VarWChar, 255).Value = (object)rec.DWINGS_BGPMT ?? DBNull.Value;
            
            cmd.Parameters.AddWithValue("@Action", (object)rec.Action ?? DBNull.Value);
            cmd.Parameters.Add("@ActionStatus", OleDbType.Boolean).Value = rec.ActionStatus.HasValue ? (object)rec.ActionStatus.Value : DBNull.Value;
            cmd.Parameters.Add("@ActionDate", OleDbType.Date).Value = rec.ActionDate.HasValue ? (object)rec.ActionDate.Value : DBNull.Value;
            cmd.Parameters.Add("@Comments", OleDbType.LongVarWChar, int.MaxValue).Value = (object)rec.Comments ?? DBNull.Value;
            cmd.Parameters.Add("@InternalInvoiceReference", OleDbType.VarWChar, 255).Value = (object)rec.InternalInvoiceReference ?? DBNull.Value;
            
            cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date).Value = 
                rec.FirstClaimDate.HasValue ? (object)rec.FirstClaimDate.Value : DBNull.Value;
            cmd.Parameters.Add("@LastClaimDate", OleDbType.Date).Value = 
                rec.LastClaimDate.HasValue ? (object)rec.LastClaimDate.Value : DBNull.Value;
            cmd.Parameters.Add("@ToRemind", OleDbType.Boolean).Value = rec.ToRemind;
            cmd.Parameters.Add("@ToRemindDate", OleDbType.Date).Value = 
                rec.ToRemindDate.HasValue ? (object)rec.ToRemindDate.Value : DBNull.Value;
            cmd.Parameters.Add("@ACK", OleDbType.Boolean).Value = rec.ACK;
            
            cmd.Parameters.Add("@SwiftCode", OleDbType.VarWChar, 255).Value = (object)rec.SwiftCode ?? DBNull.Value;
            cmd.Parameters.Add("@PaymentReference", OleDbType.VarWChar, 255).Value = (object)rec.PaymentReference ?? DBNull.Value;
            // Long text fields
            var pMbaw = cmd.Parameters.Add("@MbawData", OleDbType.LongVarWChar, int.MaxValue);
            pMbaw.Value = rec.MbawData ?? (object)DBNull.Value;
            var pSpirit = cmd.Parameters.Add("@SpiritData", OleDbType.LongVarWChar, int.MaxValue);
            pSpirit.Value = rec.SpiritData ?? (object)DBNull.Value;
            
            cmd.Parameters.Add("@KPI", OleDbType.Integer).Value = 
                rec.KPI.HasValue ? (object)rec.KPI.Value : DBNull.Value;
            cmd.Parameters.Add("@IncidentType", OleDbType.Integer).Value = 
                rec.IncidentType.HasValue ? (object)rec.IncidentType.Value : DBNull.Value;
            cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean).Value = 
                rec.RiskyItem.HasValue ? (object)rec.RiskyItem.Value : DBNull.Value;
            cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer).Value = 
                rec.ReasonNonRisky.HasValue ? (object)rec.ReasonNonRisky.Value : DBNull.Value;
            cmd.Parameters.Add("@CreationDate", OleDbType.Date).Value = 
                rec.CreationDate.HasValue ? (object)rec.CreationDate.Value : DBNull.Value;
                
            cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255).Value = (object)rec.ModifiedBy ?? DBNull.Value;
            
            cmd.Parameters.Add("@LastModified", OleDbType.Date).Value = 
                rec.LastModified.HasValue ? (object)rec.LastModified.Value : DBNull.Value;

            return cmd;
        }

        private class ReconciliationStaging
        {
            public Reconciliation Reconciliation { get; set; }
            public DataAmbre DataAmbre { get; set; }
            public bool IsPivot { get; set; }
            public string Bgi { get; set; }
            
            // Calculated KPIs from ReconciliationKpiCalculator
            public bool IsGrouped { get; set; }
            public decimal? MissingAmount { get; set; }
        }
    }
}