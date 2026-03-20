using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Quick Search (Ctrl+F) for ReconciliationView
    public partial class ReconciliationView
    {
        /// <summary>
        /// Static command used by the XAML KeyBinding (Ctrl+F) to focus the quick search box.
        /// </summary>
        public static readonly RoutedCommand FocusQuickSearchCommand = new RoutedCommand("FocusQuickSearch", typeof(ReconciliationView));

        private void InitializeQuickSearchCommand()
        {
            CommandBindings.Add(new CommandBinding(FocusQuickSearchCommand, (s, e) => FocusQuickSearch()));
        }

        private string _quickSearchTerm;

        // Cached string property getters for ReconciliationViewData (built once, reused)
        private static Func<ReconciliationViewData, string>[] _searchableGetters;

        private static Func<ReconciliationViewData, string>[] GetSearchableGetters()
        {
            if (_searchableGetters != null) return _searchableGetters;

            var props = typeof(ReconciliationViewData)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.CanRead)
                .ToArray();

            _searchableGetters = props
                .Select(p =>
                {
                    // Build a fast delegate for each property
                    var getter = (Func<ReconciliationViewData, string>)(row =>
                    {
                        try { return (string)p.GetValue(row); }
                        catch { return null; }
                    });
                    return getter;
                })
                .ToArray();

            return _searchableGetters;
        }

        /// <summary>
        /// Returns true if any string property of <paramref name="row"/> contains <paramref name="term"/> (case-insensitive).
        /// Also checks numeric/date fields via their ToString() representation.
        /// </summary>
        private static bool RowMatchesSearch(ReconciliationViewData row, string term)
        {
            if (row == null || string.IsNullOrEmpty(term)) return true;

            var getters = GetSearchableGetters();
            foreach (var getter in getters)
            {
                var val = getter(row);
                if (val != null && val.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            // Also check common non-string fields that users might search for
            if (row.SignedAmount.ToString("0.00").Contains(term)) return true;
            if (row.Operation_Date?.ToString("yyyy-MM-dd").Contains(term) == true) return true;
            if (row.Value_Date?.ToString("yyyy-MM-dd").Contains(term) == true) return true;

            return false;
        }

        /// <summary>
        /// Applies the quick search term on top of the currently filtered data and refreshes the grid.
        /// </summary>
        private void ApplyQuickSearch()
        {
            var source = _filteredData;
            if (source == null) source = _allViewData;
            if (source == null) return;

            List<ReconciliationViewData> result;
            if (string.IsNullOrWhiteSpace(_quickSearchTerm))
            {
                result = source;
            }
            else
            {
                result = source.Where(r => RowMatchesSearch(r, _quickSearchTerm)).ToList();
            }

            // Preserve sort
            var view = CollectionViewSource.GetDefaultView(_viewData);
            var savedSort = view?.SortDescriptions?.ToList() ?? new List<SortDescription>();

            // Paginate
            _loadedCount = Math.Min(InitialPageSize, result.Count);
            _viewData.Clear();
            foreach (var item in result.Take(_loadedCount))
                _viewData.Add(item);

            // Restore sort
            var newView = CollectionViewSource.GetDefaultView(_viewData);
            newView.SortDescriptions.Clear();
            foreach (var sd in savedSort)
                newView.SortDescriptions.Add(sd);

            // Update KPIs on the search-filtered set
            UpdateKpis(result);

            // Status info
            var searchInfo = string.IsNullOrWhiteSpace(_quickSearchTerm)
                ? ""
                : $" | Search: \"{_quickSearchTerm}\"";
            UpdateStatusInfo($"{ViewData.Count} / {result.Count} lines displayed{searchInfo}");

            // Store the search-filtered list so paging works correctly
            // We temporarily replace _filteredData with the search result for paging,
            // but keep the original for when the search is cleared.
            // To avoid breaking the filter pipeline, we use a separate field.
            _quickSearchResultData = result;
        }

        // Search-filtered data for paging support
        private List<ReconciliationViewData> _quickSearchResultData;

        /// <summary>
        /// Returns the effective data list for paging: quick-search result if active, otherwise _filteredData.
        /// </summary>
        private List<ReconciliationViewData> GetEffectivePagedData()
        {
            if (!string.IsNullOrWhiteSpace(_quickSearchTerm) && _quickSearchResultData != null)
                return _quickSearchResultData;
            return _filteredData;
        }

        // ---- UI Event Handlers ----

        private void QuickSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteQuickSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearQuickSearch();
                e.Handled = true;
            }
        }

        private void QuickSearchButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteQuickSearch();
        }

        private void QuickSearchClear_Click(object sender, RoutedEventArgs e)
        {
            ClearQuickSearch();
        }

        private void ExecuteQuickSearch()
        {
            var searchBox = this.FindName("QuickSearchTextBox") as TextBox;
            if (searchBox == null) return;

            _quickSearchTerm = searchBox.Text?.Trim();
            ApplyQuickSearch();

            // Update clear button visibility
            var clearBtn = this.FindName("QuickSearchClearButton") as Button;
            if (clearBtn != null)
                clearBtn.Visibility = string.IsNullOrWhiteSpace(_quickSearchTerm) ? Visibility.Collapsed : Visibility.Visible;

            // Update result count badge
            var badge = this.FindName("QuickSearchResultBadge") as TextBlock;
            if (badge != null)
            {
                if (!string.IsNullOrWhiteSpace(_quickSearchTerm) && _quickSearchResultData != null)
                {
                    badge.Text = $"{_quickSearchResultData.Count} result(s)";
                    badge.Visibility = Visibility.Visible;
                }
                else
                {
                    badge.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void ClearQuickSearch()
        {
            var searchBox = this.FindName("QuickSearchTextBox") as TextBox;
            if (searchBox != null)
                searchBox.Text = string.Empty;

            _quickSearchTerm = null;
            _quickSearchResultData = null;

            // Restore display from _filteredData
            ApplyQuickSearch();

            var clearBtn = this.FindName("QuickSearchClearButton") as Button;
            if (clearBtn != null)
                clearBtn.Visibility = Visibility.Collapsed;

            var badge = this.FindName("QuickSearchResultBadge") as TextBlock;
            if (badge != null)
                badge.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Focuses the quick search box. Called from Ctrl+F keybinding.
        /// </summary>
        private void FocusQuickSearch()
        {
            var searchBox = this.FindName("QuickSearchTextBox") as TextBox;
            if (searchBox != null)
            {
                // Make sure the search bar is visible
                var searchBar = this.FindName("QuickSearchBar") as Border;
                if (searchBar != null)
                    searchBar.Visibility = Visibility.Visible;

                searchBox.Focus();
                searchBox.SelectAll();
            }
        }
    }
}
