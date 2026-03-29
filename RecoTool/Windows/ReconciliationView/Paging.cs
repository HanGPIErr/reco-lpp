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

        private System.Diagnostics.Stopwatch _scrollStopwatch;
        private int _scrollEventCount;
        private long _lastScrollMs;

        private void ResultsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            try
            {
                if (_scrollStopwatch == null) _scrollStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _scrollEventCount++;
                var t0 = _scrollStopwatch.ElapsedMilliseconds;
                var frameDelta = t0 - _lastScrollMs; // time since last scroll event

                bool isHorizontal = e != null && Math.Abs(e.HorizontalChange) > double.Epsilon;
                bool isVertical = e != null && Math.Abs(e.VerticalChange) > double.Epsilon;

                var sv = sender as ScrollViewer;

                // ── Vertical-only logic: footer button visibility ──
                if (isVertical && sv != null)
                {
                    var pagedSource = GetEffectivePagedData();
                    if (pagedSource != null && pagedSource.Count > 0)
                    {
                        bool atBottom = sv.ScrollableHeight > 0 && sv.VerticalOffset >= (sv.ScrollableHeight * 0.9);
                        int remaining = pagedSource.Count - _loadedCount;
                        if (_loadMoreFooterButton != null)
                        {
                            var desired = (atBottom && remaining > 0) ? Visibility.Visible : Visibility.Collapsed;
                            if (_loadMoreFooterButton.Visibility != desired)
                                _loadMoreFooterButton.Visibility = desired;
                        }
                    }
                }

                // ── Horizontal scroll stutter diagnostics ──
                if (isHorizontal && sv != null)
                {
                    var elapsed = _scrollStopwatch.ElapsedMilliseconds - t0;
                    // Identify visible column range at current horizontal offset
                    string colInfo = "";
                    try
                    {
                        var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                        if (sfGrid != null)
                        {
                            double hOffset = sv.HorizontalOffset;
                            double vpRight = hOffset + sv.ViewportWidth;
                            int frozenCount = sfGrid.FrozenColumnCount;
                            double accum = 0;
                            string firstCol = null, lastCol = null;
                            var templateCols = new System.Collections.Generic.List<string>();
                            int idx = 0;
                            foreach (var col in sfGrid.Columns)
                            {
                                idx++;
                                // Skip frozen columns — they don't scroll horizontally
                                if (idx <= frozenCount) continue;

                                double w = col.ActualWidth > 0 ? col.ActualWidth : col.Width;
                                double colLeft = accum;
                                double colRight = accum + w;

                                // Column is visible if it overlaps the viewport
                                if (colRight > hOffset && colLeft < vpRight)
                                {
                                    var name = col.MappingName ?? col.HeaderText ?? $"col{idx}";
                                    if (firstCol == null) firstCol = name;
                                    lastCol = name;
                                    // Mark TemplateColumns (heavier rendering)
                                    if (col is Syncfusion.UI.Xaml.Grid.GridTemplateColumn)
                                        templateCols.Add(name);
                                }
                                accum += w;
                            }
                            colInfo = $" cols=[{firstCol ?? "?"}..{lastCol ?? "?"}]";
                            if (templateCols.Count > 0)
                                colInfo += $" tpl={string.Join(",", templateCols)}";
                        }
                    }
                    catch { }

                    // Log EVERY horizontal scroll that shows stutter (frameDelta > 16ms = dropped frame)
                    // or every 10th horizontal event for baseline
                    bool isStutter = frameDelta > 16;
                    if (isStutter || _scrollEventCount % 10 == 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[HScroll#{_scrollEventCount}] {(isStutter ? "⚠️STUTTER " : "")}" +
                            $"frameDelta={frameDelta}ms handler={elapsed}ms " +
                            $"| hOffset={sv.HorizontalOffset:F0}/{sv.ScrollableWidth:F0} hChange={e.HorizontalChange:F0}" +
                            $"{colInfo}" +
                            $" | vOffset={sv.VerticalOffset:F0} loaded={_loadedCount}");
                    }
                }

                _lastScrollMs = t0;
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
