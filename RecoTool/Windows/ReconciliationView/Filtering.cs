using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Data;
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

            // Refresh TransactionType options with all other filters applied
            var preTransactionType = VM.ApplyFilters(_allViewData, excludeTransactionType: true);
            VM.UpdateTransactionTypeOptionsForData(preTransactionType);

            // Apply full filter set
            var filteredList = VM.ApplyFilters(_allViewData);

            // Apply status filter if active (client-side only)
            if (!string.IsNullOrEmpty(_activeStatusFilter))
            {
                filteredList = filteredList.Where(row => MatchesStatusFilter(row)).ToList();
            }

            // Recalculate grouping flags and MissingAmount on ALL data (both account sides)
            // so that IsMatchedAcrossAccounts and MissingAmount are always correct.
            // CRITICAL: Must use _allViewData, NOT filteredList, because filteredList may
            // contain only one account side (e.g. Pivot only) and ComputeMatchedAcrossAccounts
            // needs both P and R rows to detect cross-account matches.
            // Since filteredList items are the same object references, computed values propagate.
            try
            {
                var country = CurrentCountryObject;
                if (country != null)
                {
                    // Reset grouping + MissingAmount for ALL rows first
                    foreach (var r in _allViewData)
                    {
                        r.IsMatchedAcrossAccounts = false;
                        r.MissingAmount = null;
                        r.CounterpartTotalAmount = null;
                        r.CounterpartCount = null;
                    }

                    // Recompute IsMatchedAcrossAccounts on ALL rows (both account sides)
                    AccountSideCalculator.ComputeMatchedAcrossAccounts(_allViewData,
                        r => r.AccountSide,
                        r => r.DWINGS_InvoiceID,
                        r => r.InternalInvoiceReference,
                        (r, matched) => r.IsMatchedAcrossAccounts = matched,
                        r => AccountSideCalculator.ExtractFallbackBgiKey(
                            r.DWINGS_InvoiceID, r.Receivable_InvoiceFromAmbre,
                            r.Reconciliation_Num, r.Comments, r.RawLabel,
                            r.Receivable_DWRefFromAmbre, r.InternalInvoiceReference));

                    // Recompute MissingAmount on ALL rows (needs both sides for correct calculation)
                    ReconciliationViewEnricher.CalculateMissingAmounts(
                        _allViewData, country.CNT_AmbreReceivable, country.CNT_AmbrePivot);

                    // Assign alternating colors to rows sharing the same InternalInvoiceReference
                    ReconciliationViewEnricher.AssignInvoiceGroupColors(_allViewData);

                    // Refresh all pre-calculated display caches (StatusColor, MissingAmount colors,
                    // G visibility, etc.) so the grid reads final values without re-computing on scroll
                    foreach (var r in _allViewData)
                        r.PreCalculateDisplayProperties();
                }
            }
            catch { }

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
