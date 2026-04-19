using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RecoTool.Services.DTOs;
using RecoTool.Services.Snapshots;
using Syncfusion.UI.Xaml.Grid;

namespace RecoTool.Windows
{
    /// <summary>
    /// Partial: wires the <c>ReconciliationView</c> grid rows to the snapshot diff so rows that
    /// changed since the last import render with a left-edge indicator + per-row tooltip. One DB
    /// round-trip per load; results are applied on the dispatcher thread so WPF bindings see the
    /// flips safely.
    /// </summary>
    public partial class ReconciliationView
    {
        // Cached for the flyout panel — the same diff drives both the row markers and the
        // "Import history" popup, so computing it once per load saves a second round-trip.
        private Dictionary<string, List<RowChange>> _lastRunChangesByRow;

        /// <summary>
        /// Compares the live reconciliation data against the most recent snapshot, flips
        /// <see cref="ReconciliationViewData.HasRecentActivity"/> on every row that differs and
        /// attaches the per-row <see cref="ReconciliationViewData.RecentChanges"/> list so the
        /// tooltip template can render without further queries.
        /// </summary>
        /// <remarks>
        /// Safe to call fire-and-forget after a data load — never throws, and no-ops when no
        /// snapshot exists yet (fresh country, first use, etc.). The cached
        /// <see cref="_lastRunChangesByRow"/> is reused by the "What changed" flyout to avoid a
        /// duplicate cross-DB diff.
        /// </remarks>
        private async Task ApplyRecentActivityAsync()
        {
            if (_offlineFirstService == null || string.IsNullOrWhiteSpace(_currentCountryId))
                return;

            Dictionary<string, List<RowChange>> byRow;
            try
            {
                var snapshots = new SnapshotService(_offlineFirstService);
                var comparison = new SnapshotComparisonService(_offlineFirstService, snapshots);
                byRow = await comparison.GetChangesByRowSinceLastRunAsync(_currentCountryId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Activity] diff failed: {ex.Message}");
                return;
            }

            _lastRunChangesByRow = byRow;
            if (byRow == null || byRow.Count == 0) return;

            // Marshal the flips to the UI thread so INotifyPropertyChanged is raised from the
            // dispatcher (WPF bindings expect that on UI-owned properties). DispatcherPriority
            // .Background keeps the initial paint responsive — the markers appear a frame later.
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                var rows = _allViewData;
                if (rows == null) return;
                int matched = 0;
                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.ID)) continue;
                    if (byRow.TryGetValue(r.ID, out var changes))
                    {
                        r.RecentChanges = changes;
                        r.HasRecentActivity = true;
                        matched++;
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[Activity] marked {matched}/{byRow.Count} rows as changed since last import");
            }), DispatcherPriority.Background);
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // "Import History" flyout panel
        // ──────────────────────────────────────────────────────────────────────────────────────

        // Collection backing the ListBox in the popup. Kept as a field so the click handler and
        // the selection handler both see the same instance (avoid re-binding on every open).
        private readonly ObservableCollection<ImportRunSummary> _importHistoryRuns = new ObservableCollection<ImportRunSummary>();

        /// <summary>
        /// Toolbar handler. Opens the popup, refreshes the list of recent runs on every open so
        /// a just-finished import is visible without closing/reopening the view.
        /// </summary>
        private async void ImportHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var popup = FindName("ImportHistoryPopup") as Popup;
            if (popup == null) return;

            // Toggle: second click closes. StaysOpen="False" already closes on outside click,
            // but users expect the same button to act as a toggle.
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                return;
            }

            // Populate the list before opening so the user doesn't see an empty frame.
            try { await LoadImportHistoryRunsAsync().ConfigureAwait(true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ImportHistory] load failed: {ex.Message}"); }

            popup.IsOpen = true;
        }

        private async Task LoadImportHistoryRunsAsync()
        {
            if (_offlineFirstService == null || string.IsNullOrWhiteSpace(_currentCountryId))
                return;

            var snapshots = new SnapshotService(_offlineFirstService);
            // ListRuns is a synchronous JSON scan; wrap in Task.Run so we don't stall on a slow
            // network drive if SnapshotsDirectory is overridden to a share.
            var runs = await Task.Run(() => snapshots.ListRuns(_currentCountryId, 20));

            var listBox = FindName("ImportHistoryRunsList") as ListBox;
            if (listBox == null) return;

            _importHistoryRuns.Clear();
            foreach (var r in runs) _importHistoryRuns.Add(r);

            if (listBox.ItemsSource == null) listBox.ItemsSource = _importHistoryRuns;

            // Auto-select the newest run so the user gets details without a second click.
            if (_importHistoryRuns.Count > 0)
                listBox.SelectedIndex = 0;
            else
                ResetImportHistoryDetails("No snapshots yet. An import will create the first one.");
        }

        /// <summary>
        /// When the user picks a run, load its impact report and bind the stats + alerts.
        /// Runs off the UI thread to keep the popup responsive for large runs.
        /// </summary>
        private async void ImportHistoryRun_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var run = (sender as ListBox)?.SelectedItem as ImportRunSummary;
            if (run == null || _offlineFirstService == null) return;

            ResetImportHistoryDetails("Loading impact…");

            RunImpactReport report;
            try
            {
                var snapshots = new SnapshotService(_offlineFirstService);
                var comparison = new SnapshotComparisonService(_offlineFirstService, snapshots);
                report = await comparison.GetImpactForRunAsync(_currentCountryId, run.ImportRunId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ResetImportHistoryDetails($"Impact load failed: {ex.Message}");
                return;
            }

            ApplyImpactReportToPanel(report);
        }

        /// <summary>
        /// Click on an alert row or a field-impact row: resolve the attached RowIds list (via
        /// the element's Tag) and highlight the matching rows in the main grid.
        /// </summary>
        private void ImportHistoryImpactRow_Click(object sender, MouseButtonEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var ids = fe?.Tag as IEnumerable<string>;
            if (ids == null) return;

            // Close the popup so the user can see the grid immediately.
            (FindName("ImportHistoryPopup") as Popup)?.SetValue(Popup.IsOpenProperty, false);

            HighlightRowsByIds(ids);
        }

        /// <summary>
        /// Selects the given rows in the grid and scrolls to the first one. Non-destructive —
        /// doesn't apply a filter, so the user can still see the surrounding context.
        /// </summary>
        private void HighlightRowsByIds(IEnumerable<string> ids)
        {
            if (ids == null) return;
            var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            if (idSet.Count == 0) return;

            var sfGrid = FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
            if (sfGrid == null || ViewData == null) return;

            // Pick matching rows in the current visible collection.
            var matches = ViewData.Where(r => r?.ID != null && idSet.Contains(r.ID)).ToList();
            if (matches.Count == 0)
            {
                ShowToast($"{idSet.Count} row(s) are not in the current filter.");
                return;
            }

            // Clear + reselect via the grid's SelectedItems so the standard visual feedback kicks in.
            try { sfGrid.SelectedItems.Clear(); } catch { }
            foreach (var r in matches)
            {
                try { sfGrid.SelectedItems.Add(r); } catch { /* capacity or threading — best-effort */ }
            }

            // Scroll to the first match so it's visible.
            try
            {
                var first = matches[0];
                var rowIdx = sfGrid.ResolveToRowIndex(ViewData.IndexOf(first));
                if (rowIdx >= 0) sfGrid.ScrollInView(new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIdx, 0));
            }
            catch { }

            ShowToast($"Selected {matches.Count} row(s)" +
                      (matches.Count < idSet.Count ? $" ({idSet.Count - matches.Count} outside current filter)" : string.Empty));
        }

        private void ApplyImpactReportToPanel(RunImpactReport report)
        {
            var placeholder = FindName("ImportHistoryPlaceholder") as TextBlock;
            var alertsHeader = FindName("ImportHistoryAlertsHeader") as TextBlock;
            var alerts = FindName("ImportHistoryAlerts") as ItemsControl;
            var fieldsHeader = FindName("ImportHistoryFieldsHeader") as TextBlock;
            var fields = FindName("ImportHistoryFields") as ItemsControl;
            if (placeholder == null || alerts == null || fields == null) return;

            bool hasAlerts = report?.Alerts != null && report.Alerts.Count > 0;
            bool hasFields = report?.MaterialChanges != null && report.MaterialChanges.Count > 0;

            placeholder.Visibility = (hasAlerts || hasFields) ? Visibility.Collapsed : Visibility.Visible;
            placeholder.Text = (hasAlerts || hasFields) ? string.Empty : "No detectable changes on the reconciliation fields for this run.";

            if (alertsHeader != null) alertsHeader.Visibility = hasAlerts ? Visibility.Visible : Visibility.Collapsed;
            alerts.ItemsSource = hasAlerts ? report.Alerts : null;

            if (fieldsHeader != null) fieldsHeader.Visibility = hasFields ? Visibility.Visible : Visibility.Collapsed;
            fields.ItemsSource = hasFields ? report.MaterialChanges : null;
        }

        private void ResetImportHistoryDetails(string placeholderText)
        {
            var placeholder = FindName("ImportHistoryPlaceholder") as TextBlock;
            var alertsHeader = FindName("ImportHistoryAlertsHeader") as TextBlock;
            var alerts = FindName("ImportHistoryAlerts") as ItemsControl;
            var fieldsHeader = FindName("ImportHistoryFieldsHeader") as TextBlock;
            var fields = FindName("ImportHistoryFields") as ItemsControl;

            if (placeholder != null)
            {
                placeholder.Text = placeholderText ?? string.Empty;
                placeholder.Visibility = string.IsNullOrEmpty(placeholderText) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (alertsHeader != null) alertsHeader.Visibility = Visibility.Collapsed;
            if (alerts != null) alerts.ItemsSource = null;
            if (fieldsHeader != null) fieldsHeader.Visibility = Visibility.Collapsed;
            if (fields != null) fields.ItemsSource = null;
        }
    }
}
