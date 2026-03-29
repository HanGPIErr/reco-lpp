using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RecoTool.Services.DTOs;
using Syncfusion.UI.Xaml.Grid;

namespace RecoTool.Windows
{
    // Partial: @Mention notifications for ReconciliationView
    public partial class ReconciliationView
    {
        private List<MentionItem> _currentMentions = new List<MentionItem>();
        private HashSet<string> _seenMentionKeys = LoadSeenMentionKeys();

        #region Per-mention persistence (%APPDATA%/RecoTool/mention_seen.txt)

        private static string SeenMentionsFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecoTool", "mention_seen.txt");

        private static HashSet<string> LoadSeenMentionKeys()
        {
            try
            {
                var path = SeenMentionsFilePath;
                if (File.Exists(path))
                    return new HashSet<string>(File.ReadAllLines(path)
                        .Where(l => !string.IsNullOrWhiteSpace(l)), StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveSeenMentionKeys(HashSet<string> keys)
        {
            try
            {
                var path = SeenMentionsFilePath;
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // Keep only the most recent 500 keys to avoid unbounded growth
                var toWrite = keys.Count > 500
                    ? keys.OrderByDescending(k => k).Take(500)
                    : (IEnumerable<string>)keys;
                File.WriteAllLines(path, toWrite);
            }
            catch { }
        }

        /// <summary>Builds a unique key for a mention: rowId|timestamp|author</summary>
        private static string BuildMentionKey(string rowId, DateTime? ts, string author)
        {
            var tsStr = ts.HasValue ? ts.Value.ToString("yyyy-MM-dd HH:mm") : "?";
            return $"{rowId}|{tsStr}|{author}";
        }

        #endregion

        /// <summary>
        /// DTO for displaying a single mention notification.
        /// </summary>
        public class MentionItem
        {
            public string MentionedBy { get; set; }
            public DateTime? Timestamp { get; set; }
            public string TimeAgo { get; set; }
            public string CommentSnippet { get; set; }
            public string RecordRef { get; set; }
            public ReconciliationViewData SourceRow { get; set; }
            /// <summary>True if this mention has not been clicked/acknowledged yet.</summary>
            public bool IsNew { get; set; }
            /// <summary>Unique key used for seen/unseen persistence.</summary>
            public string Key { get; set; }
        }

        /// <summary>
        /// Scans filtered data for comments that mention the current user.
        /// Badge shows unread count (red) or total count (grey).
        /// </summary>
        public void RefreshMentionBadge()
        {
            try
            {
                var currentUser = _reconciliationService?.CurrentUser ?? Environment.UserName;
                if (string.IsNullOrWhiteSpace(currentUser)) return;

                _currentMentions = ScanMentions(currentUser);

                int newCount = _currentMentions.Count(m => m.IsNew);

                var badge = this.FindName("MentionBadge") as Border;
                var badgeText = this.FindName("MentionBadgeText") as TextBlock;
                if (badge == null || badgeText == null) return;

                if (_currentMentions.Count > 0)
                {
                    int displayCount = newCount > 0 ? newCount : _currentMentions.Count;
                    badgeText.Text = displayCount > 99 ? "99+" : displayCount.ToString();
                    badge.Background = newCount > 0
                        ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                        : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                    badge.Visibility = Visibility.Visible;
                }
                else
                {
                    badge.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        /// <summary>Resolves a USR_ID to display name via AssigneeOptions. Returns original value if not found.</summary>
        private string ResolveUserDisplayName(string userIdOrName)
        {
            if (string.IsNullOrWhiteSpace(userIdOrName)) return userIdOrName;
            try
            {
                var match = AssigneeOptions?.FirstOrDefault(u =>
                    string.Equals(u.Id, userIdOrName, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.Name))
                    return match.Name;
            }
            catch { }
            return userIdOrName;
        }

        /// <summary>Resolves all USR_IDs in a full comment block: header authors and @mentions become display names.</summary>
        private string ResolveCommentsForDisplay(string comments)
        {
            if (string.IsNullOrWhiteSpace(comments) || AssigneeOptions == null || AssigneeOptions.Count == 0) return comments;
            try
            {
                // Replace header authors: [2024-01-01 12:00] USR_ID: → [2024-01-01 12:00] DisplayName:
                var resolved = Regex.Replace(comments, @"(\[\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\])\s*([^:\r\n]+):", m =>
                {
                    var datePart = m.Groups[1].Value;
                    var authorRaw = m.Groups[2].Value.Trim();
                    var authorDisplay = ResolveUserDisplayName(authorRaw);
                    return $"{datePart} {authorDisplay}:";
                });
                // Also resolve @USR_ID tokens
                resolved = ResolveAtMentionsForDisplay(resolved);
                return resolved;
            }
            catch { return comments; }
        }

        /// <summary>Replaces @USR_ID tokens in a text with @DisplayName for human-friendly display.</summary>
        private string ResolveAtMentionsForDisplay(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || AssigneeOptions == null || AssigneeOptions.Count == 0) return text;
            try
            {
                return Regex.Replace(text, @"@(\S+)", m =>
                {
                    var id = m.Groups[1].Value;
                    var user = AssigneeOptions.FirstOrDefault(u =>
                        string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (user != null && !string.IsNullOrWhiteSpace(user.Name) &&
                        !string.Equals(user.Id, user.Name, StringComparison.OrdinalIgnoreCase))
                        return $"@{user.Name}";
                    return m.Value;
                });
            }
            catch { return text; }
        }

        private List<MentionItem> ScanMentions(string currentUser)
        {
            var source = _filteredData ?? _allViewData;
            if (source == null || source.Count == 0) return new List<MentionItem>();

            // Build regex that matches both @USR_ID and @DisplayName
            var patterns = new List<string> { Regex.Escape(currentUser) };
            var displayName = ResolveUserDisplayName(currentUser);
            if (!string.Equals(displayName, currentUser, StringComparison.OrdinalIgnoreCase))
                patterns.Add(Regex.Escape(displayName));
            var mentionPattern = $@"@({string.Join("|", patterns)})";
            var regex = new Regex(mentionPattern, RegexOptions.IgnoreCase);

            var results = new List<MentionItem>();

            foreach (var row in source)
            {
                if (string.IsNullOrWhiteSpace(row.Comments)) continue;
                if (!regex.IsMatch(row.Comments)) continue;

                var lines = row.Comments.Replace("\r\n", "\n").Split('\n');
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (!regex.IsMatch(line)) continue;

                    string author = "Unknown";
                    DateTime? ts = null;
                    var headerMatch = Regex.Match(line, @"^\[(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2})\]\s*([^:]+):");
                    if (headerMatch.Success)
                    {
                        if (DateTime.TryParse(headerMatch.Groups[1].Value, out var parsed))
                            ts = parsed;
                        author = headerMatch.Groups[2].Value.Trim();
                    }

                    // Resolve author USR_ID to display name
                    var authorDisplay = ResolveUserDisplayName(author);

                    var rowId = row.ID ?? row.Reconciliation_Num ?? "";
                    var key = BuildMentionKey(rowId, ts, author);

                    // Build a display-friendly snippet: resolve header author and @mentions to names
                    var snippet = line.Trim();
                    if (headerMatch.Success && !string.Equals(author, authorDisplay, StringComparison.Ordinal))
                        snippet = snippet.Replace(headerMatch.Groups[2].Value.Trim(), authorDisplay);
                    snippet = ResolveAtMentionsForDisplay(snippet);

                    results.Add(new MentionItem
                    {
                        MentionedBy = authorDisplay,
                        Timestamp = ts,
                        TimeAgo = FormatTimeAgo(ts),
                        CommentSnippet = Truncate(snippet, 120),
                        RecordRef = row.Reconciliation_Num ?? row.ID,
                        SourceRow = row,
                        Key = key,
                        IsNew = !_seenMentionKeys.Contains(key)
                    });
                }
            }

            return results.OrderByDescending(m => m.Timestamp).ToList();
        }

        private void MentionBadge_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var popup = this.FindName("MentionPopup") as Popup;
                if (popup == null) return;

                var listBox = this.FindName("MentionListBox") as ListBox;
                if (listBox != null)
                    listBox.ItemsSource = _currentMentions;

                popup.IsOpen = !popup.IsOpen;
            }
            catch { }
        }

        private void MentionItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var fe = sender as FrameworkElement;
                var item = fe?.DataContext as MentionItem;
                if (item?.SourceRow == null) return;

                // Mark this specific mention as read
                if (item.IsNew && !string.IsNullOrEmpty(item.Key))
                {
                    item.IsNew = false;
                    _seenMentionKeys.Add(item.Key);
                    SaveSeenMentionKeys(_seenMentionKeys);

                    // Refresh badge count immediately
                    int newCount = _currentMentions.Count(m => m.IsNew);
                    var badge = this.FindName("MentionBadge") as Border;
                    var badgeText = this.FindName("MentionBadgeText") as TextBlock;
                    if (badge != null && badgeText != null)
                    {
                        int displayCount = newCount > 0 ? newCount : _currentMentions.Count;
                        badgeText.Text = displayCount > 99 ? "99+" : displayCount.ToString();
                        badge.Background = newCount > 0
                            ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                            : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                    }

                    // Update the visual for this item (remove blue dot / highlight)
                    if (fe is Border border)
                    {
                        border.Background = Brushes.Transparent;
                        var dot = FindVisualChild<System.Windows.Shapes.Ellipse>(border);
                        if (dot != null) dot.Visibility = Visibility.Collapsed;
                    }
                }

                // Close popup
                var popup = this.FindName("MentionPopup") as Popup;
                if (popup != null) popup.IsOpen = false;

                // Navigate to the row
                var sfGrid = this.FindName("ResultsDataGrid") as Syncfusion.UI.Xaml.Grid.SfDataGrid;
                if (sfGrid == null) return;

                var idx = ViewData.IndexOf(item.SourceRow);
                if (idx >= 0)
                {
                    sfGrid.SelectedItem = item.SourceRow;
                    var rowIdx = sfGrid.ResolveToRowIndex(idx);
                    if (rowIdx >= 0) sfGrid.ScrollInView(new Syncfusion.UI.Xaml.ScrollAxis.RowColumnIndex(rowIdx, 0));
                }
                else
                {
                    try { OpenSingleReconciliationPopup(item.SourceRow.ID); } catch { }
                }
            }
            catch { }
        }

        private void MarkAllMentionsRead_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_currentMentions == null || _currentMentions.Count == 0) return;

                bool anyNew = false;
                foreach (var m in _currentMentions)
                {
                    if (m.IsNew && !string.IsNullOrEmpty(m.Key))
                    {
                        m.IsNew = false;
                        _seenMentionKeys.Add(m.Key);
                        anyNew = true;
                    }
                }

                if (anyNew)
                {
                    SaveSeenMentionKeys(_seenMentionKeys);

                    // Update badge to grey (all read)
                    var badge = this.FindName("MentionBadge") as Border;
                    var badgeText = this.FindName("MentionBadgeText") as TextBlock;
                    if (badge != null && badgeText != null)
                    {
                        badgeText.Text = _currentMentions.Count > 99 ? "99+" : _currentMentions.Count.ToString();
                        badge.Background = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                    }

                    // Refresh listbox to remove blue dots
                    var listBox = this.FindName("MentionListBox") as ListBox;
                    if (listBox != null)
                    {
                        listBox.ItemsSource = null;
                        listBox.ItemsSource = _currentMentions;
                    }
                }
            }
            catch { }
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
