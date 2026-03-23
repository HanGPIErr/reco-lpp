using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Activity Log panel for ReconciliationView
    public partial class ReconciliationView
    {
        private bool _activityLogVisible;

        /// <summary>
        /// DTO for displaying a single activity log entry.
        /// </summary>
        public class ActivityLogItem
        {
            public string User { get; set; }
            public DateTime? Timestamp { get; set; }
            public string TimeAgo { get; set; }
            public string RecordRef { get; set; }
            public string Label { get; set; }
            public string AccountSide { get; set; }
            public string Operation { get; set; }
            public ReconciliationViewData SourceRow { get; set; }
        }

        private static string FormatTimeAgo(DateTime? utcTime)
        {
            if (!utcTime.HasValue) return "";
            var local = utcTime.Value.Kind == DateTimeKind.Utc ? utcTime.Value.ToLocalTime() : utcTime.Value;
            var diff = DateTime.Now - local;
            if (diff.TotalMinutes < 1) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 2) return "yesterday";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return local.ToString("MMM dd");
        }

        private List<ActivityLogItem> BuildActivityLog(int maxItems = 200)
        {
            var source = _filteredData ?? _allViewData;
            if (source == null || source.Count == 0) return new List<ActivityLogItem>();

            return source
                .Where(r => r.Reco_LastModified.HasValue && !string.IsNullOrWhiteSpace(r.Reco_ModifiedBy))
                .OrderByDescending(r => r.Reco_LastModified)
                .Take(maxItems)
                .Select(r => new ActivityLogItem
                {
                    User = ResolveUserDisplayName(r.Reco_ModifiedBy),
                    Timestamp = r.Reco_LastModified,
                    TimeAgo = FormatTimeAgo(r.Reco_LastModified),
                    RecordRef = r.Reconciliation_Num ?? r.ID,
                    Label = Truncate(r.RawLabel, 60),
                    AccountSide = r.AccountSide == "P" ? "Pivot" : r.AccountSide == "R" ? "Receivable" : "",
                    Operation = r.Reco_CreationDate.HasValue && r.Reco_LastModified.HasValue
                        && (r.Reco_LastModified.Value - r.Reco_CreationDate.Value).TotalSeconds < 5
                        ? "Created" : "Modified",
                    SourceRow = r
                })
                .ToList();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen - 1) + "…";
        }

        /// <summary>
        /// Stamps the affected in-memory rows with current timestamp and user after a save,
        /// so the Activity Log reflects recent edits without requiring a full data reload.
        /// </summary>
        private void StampRowsModified(IEnumerable<ReconciliationViewData> rows)
        {
            if (rows == null) return;
            var now = DateTime.Now;
            var user = _reconciliationService?.CurrentUser ?? Environment.UserName;
            foreach (var r in rows)
            {
                if (r == null) continue;
                r.Reco_LastModified = now;
                r.Reco_ModifiedBy = user;
            }
        }

        /// <summary>
        /// Refreshes the Activity Log panel if it is currently visible.
        /// Call after any data modification to keep the log up-to-date.
        /// </summary>
        private void RefreshActivityLog()
        {
            if (!_activityLogVisible) return;
            try
            {
                var listBox = this.FindName("ActivityLogListBox") as ListBox;
                if (listBox != null)
                    listBox.ItemsSource = BuildActivityLog();
            }
            catch { }
        }

        private void ToggleActivityLog_Click(object sender, RoutedEventArgs e)
        {
            _activityLogVisible = !_activityLogVisible;
            var panel = this.FindName("ActivityLogPanel") as Border;
            if (panel == null) return;

            if (_activityLogVisible)
            {
                var items = BuildActivityLog();
                var listBox = this.FindName("ActivityLogListBox") as ListBox;
                if (listBox != null)
                    listBox.ItemsSource = items;
                panel.Visibility = Visibility.Visible;
            }
            else
            {
                panel.Visibility = Visibility.Collapsed;
            }
        }

        private void ActivityLogClose_Click(object sender, RoutedEventArgs e)
        {
            _activityLogVisible = false;
            var panel = this.FindName("ActivityLogPanel") as Border;
            if (panel != null) panel.Visibility = Visibility.Collapsed;
        }

        private void ActivityLogItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                var item = fe?.DataContext as ActivityLogItem;
                if (item?.SourceRow == null) return;

                // Close panel
                _activityLogVisible = false;
                var panel = this.FindName("ActivityLogPanel") as Border;
                if (panel != null) panel.Visibility = Visibility.Collapsed;

                // Navigate to the row in the DataGrid
                var dg = this.FindName("ResultsDataGrid") as DataGrid;
                if (dg == null) return;

                // If the row is in the current view, select and scroll to it
                var idx = ViewData.IndexOf(item.SourceRow);
                if (idx >= 0)
                {
                    dg.SelectedItem = item.SourceRow;
                    dg.ScrollIntoView(item.SourceRow);
                }
                else
                {
                    // Row might not be in current page; try opening the detail popup
                    try { OpenSingleReconciliationPopup(item.SourceRow.ID); } catch { }
                }
            }
            catch { }
        }
    }
}
