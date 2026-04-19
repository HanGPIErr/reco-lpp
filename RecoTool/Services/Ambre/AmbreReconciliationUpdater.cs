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
    /// Gestionnaire de mise à jour de la table T_Reconciliation.
    /// Le gros de l'implémentation est réparti sur plusieurs fichiers partiels :
    /// <list type="bullet">
    ///   <item><c>AmbreReconciliationUpdater.Preparation.cs</c> — staging des nouvelles lignes (DWINGS + Free API) et KPIs.</item>
    ///   <item><c>AmbreReconciliationUpdater.Rules.cs</c> — évaluation des truth-table rules, hooks et règles hard-codées.</item>
    ///   <item><c>AmbreReconciliationUpdater.Persistence.cs</c> — INSERT/UPDATE/archive dans la base country + back-fill DWINGS refs.</item>
    /// </list>
    /// Ce fichier n'héberge plus que les champs, le constructeur et le point d'entrée <see cref="UpdateReconciliationTableAsync"/>.
    /// </summary>
    public partial class AmbreReconciliationUpdater
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

        // [ GetUnlinkedReconciliationIdsAsync / LoadAmbreRowsByIdsAsync moved to .Persistence.cs ]

        // [ PrepareReconciliationsAsync / CreateReconciliationAsync / CalculatePaymentReferenceForPivot moved to .Preparation.cs ]

        // [ BuildRuleContext / ApplyDirectDebitCollectionRule moved to .Rules.cs ]

        // [ ApplyReconciliationChangesAsync / UpdateDwingsReferencesForUpdatesAsync moved to .Persistence.cs ]

        // [ AutoSetReasonNonRisky / EnforceItIssueAction / ApplyFallbackRule moved to .Rules.cs ]

        // [ ApplyRulesToExistingRecordsAsync / MapReconciliationFromReader moved to .Rules.cs ]

        // [ UnarchiveRecordsAsync / ArchiveRecordsAsync / InsertReconciliationsAsync / GetExistingIdsAsync moved to .Persistence.cs ]

        // [ CreateInsertCommand removed — dead code superseded by the prepared command inside InsertReconciliationsAsync ]

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