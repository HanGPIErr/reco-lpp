using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Data;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;

namespace RecoTool.Windows
{
    // Partial: Filtering (apply + clear) for ReconciliationView
    public partial class ReconciliationView
    {
        // Applique les filtres aux données
        private void ApplyFilters()
        {
            if (_allViewData == null) return;
            var sw = Stopwatch.StartNew();

            // PERF (QW-4): when no TransactionType filter is active the "exclude TxType" pass
            // produces exactly the same set as the full pass. Skip the redundant scan in that case
            // (the common path) and reuse the filtered list to refresh combo options.
            List<ReconciliationViewData> filteredList;
            bool hasTxTypeFilter = VM?.CurrentFilter?.TransactionTypeId.HasValue == true;
            if (hasTxTypeFilter)
            {
                // The combo must keep showing TransactionTypes available with OTHER filters applied,
                // so the user can switch values — hence the dedicated pass without TxType.
                var preTransactionType = VM.ApplyFilters(_allViewData, excludeTransactionType: true);
                VM.UpdateTransactionTypeOptionsForData(preTransactionType);
                filteredList = VM.ApplyFilters(_allViewData);
            }
            else
            {
                filteredList = VM.ApplyFilters(_allViewData);
                VM.UpdateTransactionTypeOptionsForData(filteredList);
            }

            // Apply status filter if active (client-side only)
            if (!string.IsNullOrEmpty(_activeStatusFilter))
            {
                filteredList = filteredList.Where(row => MatchesStatusFilter(row)).ToList();
            }

            // PERF: the only row-level work that genuinely belongs here is the cross-account
            // "G" flag (IsMatchedAcrossAccounts) and the alternating group colors — neither is
            // computed by the service. MissingAmount / CounterpartTotalAmount / CounterpartCount
            // and GroupBalance are ALREADY computed upstream on a background thread by the service
            // (BuildReconciliationViewAsyncCore on cache miss, ReapplyEnrichmentsAsync on cache hit)
            // before LoadReconciliationDataAsync sets _allViewDataDirty. Re-running
            // CalculateMissingAmounts + ComputeAndApplyGroupBalances here was a redundant pair of
            // O(N) group-by passes executed on the UI thread on every open — the main cause of the
            // freeze when opening a list whose data is already cached.
            // Invariant: _allViewDataDirty is set ONLY by LoadReconciliationDataAsync, which always
            // sources the list from the service, so the amounts above are always present here.
            if (_allViewDataDirty)
            {
                try
                {
                    // Reset only the cross-account flag (recomputed just below). Do NOT null out
                    // MissingAmount/Counterpart* — those carry the service-computed values.
                    foreach (var r in _allViewData)
                        r.IsMatchedAcrossAccounts = false;

                    // Recompute IsMatchedAcrossAccounts on ALL rows (both account sides).
                    // CRITICAL: must use _allViewData (not filteredList) — filteredList may
                    // contain only one account side and ComputeMatchedAcrossAccounts needs both
                    // P and R rows to detect cross-account matches.
                    AccountSideCalculator.ComputeMatchedAcrossAccounts(_allViewData,
                        r => r.AccountSide,
                        r => r.DWINGS_InvoiceID,
                        r => r.InternalInvoiceReference,
                        (r, matched) => r.IsMatchedAcrossAccounts = matched,
                        r => AccountSideCalculator.ExtractFallbackBgiKey(
                            r.DWINGS_InvoiceID, r.Receivable_InvoiceFromAmbre,
                            r.Reconciliation_Num, r.Comments, r.RawLabel,
                            r.Receivable_DWRefFromAmbre, r.InternalInvoiceReference));

                    // Assign alternating colors to rows sharing the same InternalInvoiceReference
                    ReconciliationViewEnricher.AssignInvoiceGroupColors(_allViewData);

                    // Refresh pre-calculated display caches (StatusColor, MissingAmount colors,
                    // G visibility, etc.) so the grid reads final values without re-computing on scroll.
                    // This is the single PreCalculate pass for a fresh load — ViewDataEnricher.EnrichAll
                    // deliberately skips it (preCalculate:false) to avoid doing the O(N) work twice.
                    foreach (var r in _allViewData)
                        r.PreCalculateDisplayProperties();
                }
                catch { }
                finally
                {
                    _allViewDataDirty = false;
                }
            }

            // 1️⃣  Sauvegarde du tri actuel
            var view = CollectionViewSource.GetDefaultView(_viewData);
            var savedSort = view?.SortDescriptions?.ToList() ?? new List<SortDescription>();

            // 2️⃣  Mettre à jour la même ObservableCollection (pas de nouvelle instance)
            _filteredData = filteredList;
            _loadedCount = Math.Min(InitialPageSize, _filteredData.Count);

            // PERF: Suspend SfDataGrid layout while we swap the page data.
            // Without BeginInit/EndInit, each Clear+Add triggers a full layout pass
            // (N+1 CollectionChanged events → N+1 relayout).
            var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
            try { sfGrid?.View?.BeginInit(); } catch { }
            try
            {
                _viewData.Clear();
                foreach (var item in _filteredData.Take(_loadedCount))
                    _viewData.Add(item);
            }
            finally
            {
                try { sfGrid?.View?.EndInit(); } catch { }
            }

            // 3️⃣  Restaurer le tri
            var newView = CollectionViewSource.GetDefaultView(_viewData);
            newView.SortDescriptions.Clear();
            foreach (var sd in savedSort)
                newView.SortDescriptions.Add(sd);


            // Re-apply quick search on top of newly filtered data (if active)
            if (!string.IsNullOrWhiteSpace(_quickSearchTerm))
            {
                ApplyQuickSearch();
            }
            else
            {
                UpdateKpis(_filteredData); // totals on entire set
                UpdateStatusInfo($"{ViewData.Count} / {_filteredData.Count} lines displayed");
            }
            var acc = VM.CurrentFilter?.AccountId ?? "All";
            var stat = VM.CurrentFilter?.Status ?? "All";
            LogAction("ApplyFilters", $"{ViewData.Count} / {_filteredData.Count} displayed | Account={acc} | Status={stat}");
            sw.Stop();
            try { LogPerf("ApplyFilters", $"source={_allViewData.Count} | displayed={ViewData.Count} | ms={sw.ElapsedMilliseconds}"); } catch { }
        }

        // Réinitialise tous les filtres
        private void ClearFilters()
        {
            // Preserve Account filter (managed by parent) and Status
            // Reset all other filters through the property bridge to update VM and UI
            try
            {
                FilterCurrency = null;
                _filterCountry = null; // informational only
                FilterFromDate = null;
                FilterGuaranteeType = null;
                FilterTransactionType = null;
                FilterTransactionTypeId = null;
                FilterGuaranteeStatus = null;
                FilterComments = null;
                FilterActionId = null;
                FilterKpiId = null;
                FilterIncidentTypeId = null;
                FilterAssigneeId = null;
                FilterPotentialDuplicates = false;
                FilterUnmatched = false;
                FilterNewLines = false;
                FilterActionDone = null;
                FilterDwGuaranteeId = null;
                FilterDwCommissionId = null;
                FilterReconciliationNum = null;
                FilterRawLabel = null;
                FilterEventNum = null;
                FilterClient = null;
                // Keep Status as-is
            }
            catch { }

            // Optionally clear any UI controls with explicit names (legacy)
            ClearFilterControls();
            ApplyFilters();
        }

        // Efface les contrôles de filtre dans l'UI
        private void ClearFilterControls()
        {
            try
            {
                // Effacer les TextBox de filtres (noms basés sur le XAML)
                // Do NOT clear AccountId control to preserve Account filter from parent page
                ClearTextBox("CurrencyFilterTextBox");
                ClearTextBox("CountryFilterTextBox");
                ClearTextBox("MinAmountFilterTextBox");
                ClearTextBox("MaxAmountFilterTextBox");
                ClearDatePicker("FromDatePicker");
                ClearDatePicker("ToDatePicker");
                ClearComboBox("ActionComboBox");
                ClearComboBox("KPIComboBox");
                ClearComboBox("IncidentTypeComboBox");
                ClearComboBox("AssigneeComboBox");
                // New ComboBoxes in Ambre Filters
                ClearComboBox("TypeComboBox");
                ClearComboBox("TransactionTypeComboBox");
                ClearComboBox("GuaranteeStatusComboBox");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error clearing filters: {ex.Message}");
            }
        }
    }
}
