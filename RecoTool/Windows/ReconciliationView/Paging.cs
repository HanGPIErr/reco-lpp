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
        // Wire the DataGrid's ScrollViewer for incremental loading
        private void TryHookResultsGridScroll(DataGrid dg)
        {
            try
            {
                if (_scrollHooked || dg == null) return;
                _resultsScrollViewer = VisualTreeHelpers.FindDescendant<ScrollViewer>(dg);
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
                // PERF: Early-exit for horizontal-only scroll BEFORE any work
                if (e != null && Math.Abs(e.VerticalChange) < double.Epsilon
                             && Math.Abs(e.ExtentHeightChange) < double.Epsilon
                             && Math.Abs(e.ViewportHeightChange) < double.Epsilon)
                    return;

                var sv = sender as ScrollViewer;
                if (sv == null) return;

                // Show/hide footer button when near bottom (zero-allocation path)
                var pagedSource = GetEffectivePagedData();
                if (pagedSource == null || pagedSource.Count == 0) return;

                bool atBottom = sv.ScrollableHeight > 0 && sv.VerticalOffset >= (sv.ScrollableHeight * 0.9);
                int remaining = pagedSource.Count - _loadedCount;
                if (_loadMoreFooterButton != null)
                {
                    var desired = (atBottom && remaining > 0) ? Visibility.Visible : Visibility.Collapsed;
                    if (_loadMoreFooterButton.Visibility != desired)
                        _loadMoreFooterButton.Visibility = desired;
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
                foreach (var item in pagedSource.Skip(_loadedCount).Take(take))
                {
                    ViewData.Add(item);
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
