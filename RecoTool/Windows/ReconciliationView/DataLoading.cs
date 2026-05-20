using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using RecoTool.Services;

namespace RecoTool.Windows
{
    // Partial: Initialization, data loading, service sync for ReconciliationView
    public partial class ReconciliationView
    {
        /// <summary>
        /// Initialise les données par défaut
        /// </summary>
        private void InitializeData()
        {
            _viewData = new ObservableCollection<ReconciliationViewData>();
            _allViewData = new List<ReconciliationViewData>();
            // Keep VM in sync with initial collection used across the view
            try { VM.ViewData = _viewData; } catch { }
        }

        /// <summary>
        /// Initialise les données depuis les services
        /// </summary>
        private async void InitializeFromServices()
        {
            try
            {
                if (_offlineFirstService != null)
                {
                    // Synchroniser avec la country courante
                    var currentCountry = _offlineFirstService.CurrentCountry;
                    if (currentCountry != null)
                    {
                        _currentCountryId = currentCountry.CNT_Id;
                        _filterCountry = currentCountry.CNT_Name;
                        // Mettre à jour l'entête Pivot/Receivable selon le référentiel pays
                        UpdateCountryPivotReceivableInfo();
                    }
                    // Référentiels: prévenir l'UI que UserFields/Country sont prêts
                    OnPropertyChanged(nameof(AllUserFields));
                    OnPropertyChanged(nameof(CurrentCountryObject));

                    // Peupler les options pour les ComboBox référentielles (Action/KPI/Incident Type)
                    PopulateReferentialOptions();
                    // PERF: Lancer les 4 SELECT DISTINCT en parallèle au lieu de les sérialiser.
                    // Sur des bases volumineuses cela divise le coût d'init des combos par ~3-4.
                    await Task.WhenAll(
                        LoadAssigneeOptionsAsync(),
                        LoadGuaranteeStatusOptionsAsync(),
                        LoadGuaranteeTypeOptionsAsync(),
                        LoadCurrencyOptionsAsync()
                    );
                    // Charger les types de transaction (enum) dans le VM
                    VM.LoadTransactionTypeOptions();
                }
                // Ne pas effectuer de chargement automatique ici; la page parente appliquera
                // les filtres et la mise en page, puis appellera explicitement Refresh().
            }
            catch (Exception ex)
            {
                // Log l'erreur si nécessaire
                System.Diagnostics.Debug.WriteLine($"Error initializing services: {ex.Message}");
            }
        }

        private void ReconciliationView_Loaded(object sender, RoutedEventArgs e)
        {
            // Make sure we are subscribed (handles re-loads)
            SubscribeToSyncEvents();
            if (_initialLoaded) return;
            if (!string.IsNullOrEmpty(_currentCountryId))
            {
                // Rafraîchir l'affichage Pivot/Receivable à l'ouverture
                UpdateCountryPivotReceivableInfo();
                // Marquer comme initialisé pour éviter les chargements implicites multiples.
                // La page hôte déclenchera Refresh() après avoir appliqué les filtres/présélections.
                _initialLoaded = true;
            }
        }

        public void Refresh()
        {
            _ = RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            // Don't check CanRefresh if IsLoading is already true (initial load)
            if (!IsLoading && !CanRefresh) return;

            try
            {
                if (!IsLoading) IsLoading = true; // Set only if not already loading;

                UpdateCountryPivotReceivableInfo();
                RefreshStarted?.Invoke(this, EventArgs.Empty);
                var sw = Stopwatch.StartNew();
                await LoadReconciliationDataAsync();
                sw.Stop();
                try { LogPerf("RefreshAsync", $"country={_currentCountryId} | totalMs={sw.ElapsedMilliseconds}"); } catch { }
            }
            finally
            {
                IsLoading = false;
                RefreshCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Targeted, in-place refresh for a small set of rows after the user edited them in the
        /// detail dialog (or any single-row save flow). Unlike <see cref="RefreshAsync"/> this does
        /// NOT re-fetch the whole grid, re-run the enrichment pipeline, or rebuild filters — it
        /// simply re-reads each row's <see cref="Reconciliation"/> persistence state from the DB,
        /// copies the editable fields onto the existing <see cref="ReconciliationViewData"/>
        /// instance (so INPC fires and the grid cell repaints in place), then re-resolves the
        /// DWINGS-derived <c>I_*</c>/<c>G_*</c> columns and the per-row display brushes.
        /// <para>
        /// The in-memory list ordering and the user's scroll position are preserved. Failures on
        /// an individual row are swallowed so other rows still update. Always returns on the UI
        /// thread — safe to call from a background context (dispatcher dispatch is internal).
        /// </para>
        /// </summary>
        /// <param name="rowIds">Reconciliation IDs (== AMBRE line IDs) that need to be refreshed.</param>
        /// <param name="ct">Optional cancellation — checked between rows; in-flight DB reads are
        /// allowed to complete (they're cheap single-row lookups).</param>
        public async Task RefreshRowsAsync(IReadOnlyList<string> rowIds, CancellationToken ct = default)
        {
            if (rowIds == null || rowIds.Count == 0) return;
            if (_reconciliationService == null) return;

            // Ensure we're on the UI thread for any property mutation (INPC) and brush recomputation.
            if (!Dispatcher.CheckAccess())
            {
                var op = Dispatcher.InvokeAsync(() => RefreshRowsAsync(rowIds, ct));
                await op.Task.Unwrap().ConfigureAwait(false);
                return;
            }

            if (_allViewData == null) return;

            // Build a quick lookup once instead of N linear scans.
            var byId = _allViewData
                .Where(r => !string.IsNullOrWhiteSpace(r?.ID))
                .GroupBy(r => r.ID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var id in rowIds)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!byId.TryGetValue(id, out var viewRow) || viewRow == null) continue;

                try
                {
                    // Pull the freshest persisted state. GetOrCreateReconciliationAsync is a single
                    // primary-key lookup against the local Access DB — ~1-5ms typical, vs 2-3s for
                    // the full RefreshAsync pipeline.
                    var reco = await _reconciliationService.GetOrCreateReconciliationAsync(id).ConfigureAwait(true);
                    if (reco == null) continue;

                    // Copy editable Reconciliation fields back onto the view row. Each setter raises
                    // PropertyChanged so the SfDataGrid cell repaints in place.
                    CopyReconciliationFieldsToViewRow(reco, viewRow);

                    // Re-resolve I_*/G_* enrichment columns from the DWINGS caches against the
                    // (possibly new) BGI/Guarantee references we just copied in. This raises
                    // PropertyChanged for every DWINGS-derived column in one batch.
                    try { viewRow.RefreshDwingsData(); } catch { }

                    // Recompute display brushes / cached strings so the row's visual state matches
                    // the new business state (status badge, action color, comments preview, etc.).
                    try { viewRow.PreCalculateDisplayProperties(); } catch { }
                }
                catch (Exception exRow)
                {
                    // Best-effort: a failure on one row must not stop the others.
                    System.Diagnostics.Debug.WriteLine($"[RefreshRowsAsync] row '{id}' failed: {exRow.Message}");
                }
            }
        }

        /// <summary>
        /// Copies the persisted <see cref="Reconciliation"/> fields onto the existing
        /// <see cref="ReconciliationViewData"/> instance, mirroring the field-by-field mapping
        /// used by the rules catch-up block in <see cref="LoadReconciliationDataAsync"/>.
        /// Only writes when the source has a meaningful value so we don't clobber INPC-set state
        /// with stale nulls from a partial save.
        /// </summary>
        private static void CopyReconciliationFieldsToViewRow(Reconciliation reco, ReconciliationViewData viewRow)
        {
            if (reco == null || viewRow == null) return;

            // DWINGS references — these can be cleared (e.g. Unlink), so always copy including nulls.
            viewRow.DWINGS_InvoiceID   = reco.DWINGS_InvoiceID;
            viewRow.DWINGS_GuaranteeID = reco.DWINGS_GuaranteeID;
            viewRow.DWINGS_BGPMT       = reco.DWINGS_BGPMT;

            // User-edited fields. We copy unconditionally — the dialog Save path persists the full
            // edit state, so the DB is authoritative for these.
            viewRow.Action          = reco.Action;
            viewRow.ActionStatus    = reco.ActionStatus;
            viewRow.ActionDate      = reco.ActionDate;
            viewRow.KPI             = reco.KPI;
            viewRow.IncidentType    = reco.IncidentType;
            viewRow.RiskyItem       = reco.RiskyItem ?? false;
            viewRow.ReasonNonRisky  = reco.ReasonNonRisky;
            viewRow.Assignee        = reco.Assignee;
            viewRow.Comments        = reco.Comments;
            viewRow.InternalInvoiceReference = reco.InternalInvoiceReference;
            viewRow.FirstClaimDate  = reco.FirstClaimDate;
            viewRow.LastClaimDate   = reco.LastClaimDate;
            viewRow.ToRemind        = reco.ToRemind;
            viewRow.ToRemindDate    = reco.ToRemindDate;
            viewRow.ACK             = reco.ACK;
            viewRow.SwiftCode       = reco.SwiftCode;
            viewRow.PaymentReference = reco.PaymentReference;
            viewRow.MbawData        = reco.MbawData;
            viewRow.SpiritData      = reco.SpiritData;
            viewRow.IncNumber       = reco.IncNumber;
            viewRow.TriggerDate     = reco.TriggerDate;
            viewRow.RemainingAmount = reco.RemainingAmount;
        }

        /// <summary>
        /// Charge les données initiales
        /// </summary>
        private async void LoadInitialData()
        {
            try
            {
                // Charger les données de démonstration ou les dernières données disponibles
                await LoadReconciliationDataAsync();
            }
            catch (Exception ex)
            {
                ShowError($"Error during initial load: {ex.Message}");
            }
        }

        /// <summary>
        /// Charge les données de réconciliation depuis le service
        /// </summary>
        private async Task LoadReconciliationDataAsync()
        {
            var previousCursor = this.Cursor;
            try
            {
                // Show hourglass cursor during loading
                this.Cursor = System.Windows.Input.Cursors.Wait;
                
                UpdateStatusInfo("Loading data...");
                var swTotal = Stopwatch.StartNew();
                var swDb = Stopwatch.StartNew();
                List<ReconciliationViewData> viewList;
                bool usedPreloaded = false;
                if (_preloadedAllData != null)
                {
                    // Utiliser les données préchargées (ne pas toucher au service).
                    // PERF (QW-2): _preloadedAllData was populated by ConfigureAndPreloadView via
                    // localSvc.GetReconciliationViewAsync, which already runs the full enrichment
                    // pipeline (EnrichWithDwingsInvoices, EnrichRowsWithDwingsProperties,
                    // CalculateMissingAmounts, ComputeAndApplyGroupBalances, AssignAccountSides,
                    // ComputeMatchedAcrossAccounts) — and ReapplyEnrichmentsAsync for cache hits.
                    // No need to re-run any of that here.
                    viewList = _preloadedAllData.ToList();
                    usedPreloaded = true;
                    swDb.Stop();
                }
                else
                {
                    // Charger la vue combinée avec filtre backend éventuel
                    // Include deleted rows when Status=Archived or when filtering by DeletedDate
                    bool includeDeleted = false;
                    try
                    {
                        var status = VM?.CurrentFilter?.Status;
                        includeDeleted = (!string.IsNullOrWhiteSpace(status) && string.Equals(status, "Archived", StringComparison.OrdinalIgnoreCase))
                                         || (VM?.CurrentFilter?.DeletedDate != null);
                    }
                    catch { }
                    // PERF (QW-2): GetReconciliationViewAsync internally runs ReapplyEnrichmentsAsync
                    // on cache hits and the full BuildReconciliationViewAsyncCore enrichment on cache
                    // misses. Both paths leave the list with up-to-date DWINGS fields, MissingAmount,
                    // and IsMatchedAcrossAccounts. Calling ReapplyEnrichmentsToListAsync here would be
                    // a redundant 4th group-by pass over the data — removed.
                    viewList = await _reconciliationService.GetReconciliationViewAsync(_currentCountryId, _backendFilterSql, includeDeleted);
                    swDb.Stop();
                }
                int totalRows = viewList?.Count ?? 0;
                _preloadedAllData = null;

                // Stocker toutes les données pour le filtrage
                _allViewData = viewList ?? new List<ReconciliationViewData>();

                // PERFORMANCE: Enrichir les libellés (Action/KPI/Incident/Assignee...) mais NE PAS
                // pré-calculer les propriétés d'affichage ici. ApplyFilters() ci-dessous (déclenché
                // par _allViewDataDirty) recalcule IsMatchedAcrossAccounts/MissingAmount/couleurs de
                // groupe PUIS appelle PreCalculateDisplayProperties() sur chaque ligne. Faire la passe
                // ici serait O(N) gaspillé sur le thread UI (résultat aussitôt écrasé).
                var swEnrich = Stopwatch.StartNew();
                ViewDataEnricher.EnrichAll(_allViewData, AllUserFields, AssigneeOptions, preCalculate: false);
                swEnrich.Stop();
                System.Diagnostics.Debug.WriteLine($"[ViewDataEnricher] Enriched {totalRows} rows in {swEnrich.ElapsedMilliseconds}ms");

                // Snapshot diff: flag rows that changed since the last import. Fire-and-forget so
                // the initial render is not blocked by the cross-DB read; the edge markers appear
                // a few hundred ms later via a dispatcher post.
                _ = ApplyRecentActivityAsync();

                // ── Rules catch-up: apply rules for rows that gained a DWINGS link but have no Action yet ──
                // Scenario: DWINGS data wasn't available on day N → rows loaded without BGI → rules couldn't match.
                // On day N+1, DWINGS data is available → BGI is filled → re-run rules ONLY for rows where Action is still null.
                // Safeguard: never touch rows where user already set an Action (prevents overwriting manual choices).
                //
                // PERF: Run as fire-and-forget on the dispatcher (Background priority). Previously this
                // block was awaited synchronously inside LoadReconciliationDataAsync — blocking the
                // initial grid render by N round-trips to ApplyRulesNowAsync + GetOrCreateReconciliationAsync.
                // Now the grid materialises immediately with the loaded rows; rules-applied rows update
                // a few hundred ms later (via property change notifications already raised on each setter).
                _ = Dispatcher?.InvokeAsync(async () =>
                {
                    try
                    {
                        if (_reconciliationService == null || _allViewData == null) return;
                        var catchUpIds = _allViewData
                            .Where(r => !r.IsDeleted
                                        && !r.Action.HasValue
                                        && (!string.IsNullOrWhiteSpace(r.DWINGS_InvoiceID)
                                            || !string.IsNullOrWhiteSpace(r.DWINGS_GuaranteeID)
                                            || !string.IsNullOrWhiteSpace(r.DWINGS_BGPMT)))
                            .Select(r => r.ID)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToList();

                        if (catchUpIds.Count == 0) return;
                        System.Diagnostics.Debug.WriteLine($"[RulesCatchUp] {catchUpIds.Count} rows have DWINGS link but no Action — running rules...");
                        var applied = await _reconciliationService.ApplyRulesNowAsync(catchUpIds, "Linking");
                        if (applied <= 0) return;

                        System.Diagnostics.Debug.WriteLine($"[RulesCatchUp] Rules applied to {applied} rows. Scheduling push.");
                        // Reload affected rows from DB so the grid shows updated Action/KPI
                        try
                        {
                            foreach (var id in catchUpIds)
                            {
                                var viewRow = _allViewData.FirstOrDefault(r => string.Equals(r.ID, id, StringComparison.OrdinalIgnoreCase));
                                if (viewRow == null) continue;
                                var reco = await _reconciliationService.GetOrCreateReconciliationAsync(id);
                                if (reco == null) continue;
                                if (reco.Action.HasValue) viewRow.Action = reco.Action;
                                if (reco.ActionStatus.HasValue) viewRow.ActionStatus = reco.ActionStatus;
                                if (reco.KPI.HasValue) viewRow.KPI = reco.KPI;
                                if (reco.IncidentType.HasValue) viewRow.IncidentType = reco.IncidentType;
                                if (reco.RiskyItem.HasValue) viewRow.RiskyItem = reco.RiskyItem.Value;
                                if (reco.ReasonNonRisky.HasValue) viewRow.ReasonNonRisky = reco.ReasonNonRisky;
                                // Re-enrich ONLY this changed row (caches were already primed by the
                                // initial EnrichAll). Avoids an O(N) re-enrich of the whole grid.
                                try { ViewDataEnricher.EnrichRow(viewRow); } catch { }
                            }
                        }
                        catch { }
                        try { ScheduleBulkPushDebounced(); } catch { }
                    }
                    catch (Exception exRules)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RulesCatchUp] Error: {exRules.Message}");
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);

                // Resolve UIDs in comments to display names for DataGrid display
                // PERF: Fast-path skip rows whose Comments contain neither '[' (timestamp header)
                // nor '@' (mention) — they cannot match either of the resolver regexes, so we avoid
                // the regex-compilation + 2 Regex.Replace calls on the vast majority of rows.
                try
                {
                    foreach (var r in _allViewData)
                    {
                        var c = r.Comments;
                        if (string.IsNullOrWhiteSpace(c)) continue;
                        if (c.IndexOf('[') < 0 && c.IndexOf('@') < 0) continue;
                        r.Comments = ResolveCommentsForDisplay(c);
                    }
                }
                catch { }

                // Populate Category filter ComboBox with distinct values from data
                try
                {
                    var categories = _allViewData
                        .Select(r => r.CategoryLabel)
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(c => c)
                        .ToList();
                    var combo = FindName("FilterCategoryCombo") as System.Windows.Controls.ComboBox;
                    if (combo != null)
                    {
                        combo.ItemsSource = categories;
                    }
                }
                catch { }

                // PERF (QW-2): the heavy grouping/PreCalculate block in ApplyFilters is gated
                // by _allViewDataDirty. After a fresh load we DO want it to run once so
                // AssignInvoiceGroupColors + per-row PreCalculateDisplayProperties are applied.
                // For all subsequent filter clicks the flag stays false and ApplyFilters is filter-only.
                _allViewDataDirty = true;

                // Appliquer les filtres courants (ex: compte/Status) si déjà définis par la page parente
                var swFilter = Stopwatch.StartNew();
                ApplyFilters();
                swFilter.Stop();

                // Refresh @mention badge after data load
                try { RefreshMentionBadge(); } catch { }

                swTotal.Stop();
                UpdateStatusInfo($"{ViewData?.Count ?? 0} lines loaded");
                try
                {
                    LogPerf(
                        "LoadReconciliationData",
                        $"country={_currentCountryId} | backendFilterLen={( _backendFilterSql?.Length ?? 0)} | source={(usedPreloaded ? "preloaded" : "service")} | dbMs={swDb.ElapsedMilliseconds} | filterMs={swFilter.ElapsedMilliseconds} | totalMs={swTotal.ElapsedMilliseconds} | totalRows={totalRows} | displayed={ViewData?.Count ?? 0}"
                    );
                }
                catch { }
            }
            catch (Exception ex)
            {
                ShowError($"Error loading data: {ex.Message}");
                UpdateStatusInfo("Load error");
            }
            finally
            {
                // Restore previous cursor
                this.Cursor = previousCursor;
            }
        }

        /// <summary>
        /// Fournit des données préchargées par la page. Bypass le fetch service.
        /// </summary>
        public void InitializeWithPreloadedData(IReadOnlyList<ReconciliationViewData> allData, string backendFilterSql)
        {
            try
            {
                _preloadedAllData = allData ?? Array.Empty<ReconciliationViewData>();
                _backendFilterSql = backendFilterSql; // garder la trace du filtre appliqué côté service
            }
            catch { }
        }

        /// <summary>
        /// Met à jour l'affichage des filtres externes provenant de ReconciliationPage
        /// </summary>
        public void UpdateExternalFilters(string account, string status)
        {
            try
            {
                var acc = string.IsNullOrWhiteSpace(account) ? "All" : account;
                var stat = string.IsNullOrWhiteSpace(status) ? "All" : status; // Expected: All/Active/Deleted
                
                // Don't update AccountInfoText here - it's managed by UpdateStatusInfo
                // Account type is shown on line 3 via AccountTypeText

                // Appliquer sur les filtres internes pour la vue
                FilterAccountId = string.Equals(acc, "All", StringComparison.OrdinalIgnoreCase) ? null : ResolveAccountIdForFilter(acc);
                // Status = All/Active/Deleted (use IsDeleted)
                FilterStatus = stat;
                ApplyFilters();
                // Mettre à jour le titre pour refléter le nouvel état
                UpdateViewTitle();
            }
            catch { /* best effort UI update */ }
        }

        /// <summary>
        /// Met à jour le sous-titre affichant Pivot/Receivable depuis le référentiel du pays sélectionné
        /// </summary>
        private void UpdateCountryPivotReceivableInfo()
        {
            try
            {
                var accountTypeText = this.FindName("AccountTypeText") as TextBlock;
                if (accountTypeText == null) return;

                var country = _offlineFirstService?.CurrentCountry;
                if (country == null)
                {
                    accountTypeText.Text = "";
                    return;
                }

                // Determine which account is filtered
                var filteredAccount = VM.FilterAccountId;
                string accountType = "";
                
                if (!string.IsNullOrWhiteSpace(filteredAccount))
                {
                    if (string.Equals(filteredAccount, country.CNT_AmbrePivot, StringComparison.OrdinalIgnoreCase))
                        accountType = "Pivot";
                    else if (string.Equals(filteredAccount, country.CNT_AmbreReceivable, StringComparison.OrdinalIgnoreCase))
                        accountType = "Receivable";
                }
                else
                {
                    // No filter = show both by default, pick Pivot
                    accountType = "Pivot";
                }
                
                accountTypeText.Text = accountType;
            }
            catch { }
        }

        /// <summary>
        /// Résout l'ID de compte réel à partir d'un libellé d'affichage (ex: "Pivot (ID)" ou "Pivot")
        /// </summary>
        private string ResolveAccountIdForFilter(string display)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(display)) return display;

                // If format "Label (ID)", extract inner ID
                var open = display.LastIndexOf('(');
                var close = display.LastIndexOf(')');
                if (open >= 0 && close > open)
                {
                    var inner = display.Substring(open + 1, (close - open - 1)).Trim();
                    if (!string.IsNullOrWhiteSpace(inner)) return inner;
                }

                // Map bare Pivot/Receivable to repository values
                var country = _offlineFirstService?.CurrentCountry;
                if (country != null)
                {
                    if (string.Equals(display, "Pivot", StringComparison.OrdinalIgnoreCase))
                        return country.CNT_AmbrePivot;
                    if (string.Equals(display, "Receivable", StringComparison.OrdinalIgnoreCase))
                        return country.CNT_AmbreReceivable;
                }

                // Fallback to raw
                return display;
            }
            catch { return display; }
        }

        /// <summary>
        /// Synchronise le pays courant depuis le service et rafraîchit la vue et l'entête.
        /// Appelé par la page lorsque la sélection de pays change.
        /// </summary>
        public void SyncCountryFromService(bool refresh = true)
        {
            try
            {
                var cc = _offlineFirstService?.CurrentCountry;
                var cid = cc?.CNT_Id;
                if (string.IsNullOrWhiteSpace(cid))
                {
                    // Fallback to CurrentCountryId if CurrentCountry object isn't yet populated
                    try { cid = _offlineFirstService?.CurrentCountryId; } catch { }
                }
                _currentCountryId = cid;
                _filterCountry = cc?.CNT_Name;
                UpdateCountryPivotReceivableInfo();

                // Start/restart presence engine for the new country
                try { StartPresenceEngine(); } catch { }

                // Recharger les options dépendantes du pays en parallèle (fire-and-forget)
                _ = Task.WhenAll(
                    LoadCurrencyOptionsAsync(),
                    LoadGuaranteeTypeOptionsAsync(),
                    LoadGuaranteeStatusOptionsAsync()
                );
                if (refresh)
                    Refresh();
            }
            catch { }
        }
    }
}
