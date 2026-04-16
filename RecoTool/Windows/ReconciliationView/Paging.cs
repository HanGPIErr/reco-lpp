using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RecoTool.UI.Helpers;

namespace RecoTool.Windows
{
    // Partial: Paging and scroll handling for ReconciliationView
    public partial class ReconciliationView
    {
        // Wire the SfDataGrid's ScrollViewer for incremental loading
        private void TryHookResultsGridScroll(Syncfusion.UI.Xaml.Grid.SfDataGrid sfGrid)
        {
            try
            {
                if (_scrollHooked || sfGrid == null) return;
                _resultsScrollViewer = VisualTreeHelpers.FindDescendant<ScrollViewer>(sfGrid);
                if (_resultsScrollViewer != null)
                {
                    _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
                    _resultsScrollViewer.ScrollChanged += ResultsScrollViewer_ScrollChanged;
                    _scrollHooked = true;
                }
                // Cache the footer button once
                if (_loadMoreFooterButton == null)
                {
                    _loadMoreFooterButton = this.FindName("LoadMoreFooterButton") as Button;
                }
            }
            catch { }
        }

        private void ResultsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            try
            {
                var sv = sender as ScrollViewer;
                if (sv == null) return;

                // ── Vertical scroll: incremental loading at bottom ──
                if (e.VerticalChange != 0)
                {
                    bool atBottom = sv.ScrollableHeight > 0 && sv.VerticalOffset >= (sv.ScrollableHeight * 0.9);
                    if (atBottom && !_isLoadingMore)
                    {
                        LoadMorePage();
                    }
                }
            }
            catch { }
        }

        private void LoadMorePage()
        {
            if (_isLoadingMore) return;
            _isLoadingMore = true;
            try
            {
                var pagedSource = GetEffectivePagedData();
                if (pagedSource == null) return;
                int remaining = pagedSource.Count - _loadedCount;
                if (remaining <= 0) return;
                int take = Math.Min(InitialPageSize, remaining);

                // PERF: Batch-add under BeginInit/EndInit to avoid N+1 CollectionChanged events
                // (each event triggers a full SfDataGrid layout pass — extremely expensive with 500 items).
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                try { sfGrid?.View?.BeginInit(); } catch { }
                try
                {
                    foreach (var item in pagedSource.Skip(_loadedCount).Take(take))
                    {
                        ViewData.Add(item);
                    }
                }
                finally
                {
                    try { sfGrid?.View?.EndInit(); } catch { }
                }

                _loadedCount += take;
                UpdateStatusInfo($"{ViewData.Count} / {pagedSource.Count} lines displayed");
                // After load, hide footer if no more data, otherwise keep visible when still at bottom
                if (_loadMoreFooterButton == null)
                {
                    _loadMoreFooterButton = this.FindName("LoadMoreFooterButton") as Button;
                }
                if (_loadMoreFooterButton != null)
                {
                    int newRemaining = pagedSource.Count - _loadedCount;
                    if (newRemaining <= 0 && _loadMoreFooterButton.Visibility != Visibility.Collapsed)
                        _loadMoreFooterButton.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
            finally
            {
                _isLoadingMore = false;
            }
        }

        // Footer button click handler to load the next page of rows
        private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadMorePage();
                e.Handled = true;
            }
            catch { }
        }
    }
}
