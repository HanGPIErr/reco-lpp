using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using RecoTool.Services.Sync;

namespace RecoTool.Windows
{
    // Partial: Real-time collaborative presence (who is working on which row)
    public partial class ReconciliationView
    {
        private BackgroundSyncEngine _syncEngine;
        private PresenceFile.PresenceData _lastPresenceData;

        // Cache login -> display name (resolved once per session via AD/GetUserNameEx)
        private static readonly Dictionary<string, string> _displayNameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetUserNameExW(int nameFormat, StringBuilder lpNameBuffer, ref int lpnSize);

        /// <summary>
        /// Resolves a Windows login name to a human-readable display name (e.g. "GDUPONT" → "Gianni Dupont").
        /// Uses GetUserNameExW for the current user, falls back to formatting the login name for others.
        /// </summary>
        private static string ResolveDisplayName(string loginName)
        {
            if (string.IsNullOrWhiteSpace(loginName)) return loginName;
            if (_displayNameCache.TryGetValue(loginName, out var cached)) return cached;

            string resolved = null;

            // For the current user, try the Win32 API (NameDisplay = 3)
            if (string.Equals(loginName, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var sb = new StringBuilder(256);
                    int size = sb.Capacity;
                    if (GetUserNameExW(3, sb, ref size) != 0 && sb.Length > 0)
                        resolved = sb.ToString();
                }
                catch { }
            }

            // Fallback: format the login name nicely ("gianni.dupont" → "Gianni Dupont")
            if (string.IsNullOrWhiteSpace(resolved))
            {
                var parts = loginName.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    resolved = string.Join(" ", parts.Select(p => char.ToUpper(p[0]) + (p.Length > 1 ? p.Substring(1).ToLower() : "")));
                else
                    resolved = loginName;
            }

            _displayNameCache[loginName] = resolved;
            return resolved;
        }

        // Observable list of active users for the header panel
        private ObservableCollection<PresenceFile.UserPresence> _activeUsers = new ObservableCollection<PresenceFile.UserPresence>();
        public ObservableCollection<PresenceFile.UserPresence> ActiveUsers => _activeUsers;

        /// <summary>
        /// Starts the background sync engine for presence and change detection.
        /// Called after country is set and data is loaded.
        /// </summary>
        private void StartPresenceEngine()
        {
            try
            {
                StopPresenceEngine();

                if (_offlineFirstService == null || string.IsNullOrWhiteSpace(_currentCountryId))
                    return;

                _syncEngine = new BackgroundSyncEngine(() => _offlineFirstService);
                _syncEngine.OnPresenceUpdated += SyncEngine_OnPresenceUpdated;
                _syncEngine.OnRemoteChangesDetected += SyncEngine_OnRemoteChangesDetected;
                _syncEngine.Start(_currentCountryId);

                System.Diagnostics.Debug.WriteLine($"[Presence] Engine started for {_currentCountryId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Presence] Failed to start engine: {ex.Message}");
            }
        }

        private void StopPresenceEngine()
        {
            try
            {
                if (_syncEngine != null)
                {
                    _syncEngine.OnPresenceUpdated -= SyncEngine_OnPresenceUpdated;
                    _syncEngine.OnRemoteChangesDetected -= SyncEngine_OnRemoteChangesDetected;
                    _syncEngine.Dispose();
                    _syncEngine = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Called from background thread every ~2s with fresh presence data.
        /// Dispatches to UI thread to update row indicators and active users panel.
        /// </summary>
        private void SyncEngine_OnPresenceUpdated(PresenceFile.PresenceData data)
        {
            if (data == null) return;
            _lastPresenceData = data;

            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        UpdatePresenceUI(data);
                    }
                    catch { }
                }));
            }
            catch { }
        }

        /// <summary>
        /// Called when the sync engine detects remote changes (another user pushed).
        /// Triggers a UI refresh.
        /// </summary>
        private void SyncEngine_OnRemoteChangesDetected(string countryId, int pulledCount)
        {
            if (pulledCount <= 0) return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
                {
                    try
                    {
                        await RefreshAsync();
                        ShowToast($"🔄 {pulledCount} row(s) updated by another user");
                    }
                    catch { }
                }));
            }
            catch { }
        }

        /// <summary>
        /// Updates presence indicators on grid rows and the active users panel.
        /// Only touches rows whose presence state actually changed.
        /// </summary>
        private void UpdatePresenceUI(PresenceFile.PresenceData data)
        {
            var currentUser = Environment.UserName;

            // Build lookup: rowId -> displayName (exclude self, resolve login → display name)
            // A single user can have multiple active rows (multi-select)
            var rowToUser = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in data.Users)
            {
                if (string.Equals(u.UserName, currentUser, StringComparison.OrdinalIgnoreCase))
                    continue;
                var displayName = ResolveDisplayName(u.UserName);
                // Set DisplayName on the UserPresence object so header badges show it
                u.DisplayName = displayName;
                if (u.ActiveRowIds != null)
                {
                    foreach (var rid in u.ActiveRowIds)
                    {
                        if (!string.IsNullOrWhiteSpace(rid))
                            rowToUser[rid] = displayName;
                    }
                }
            }

            // Update per-row presence (only changed rows to avoid unnecessary PropertyChanged)
            var viewData = _viewData;
            if (viewData != null)
            {
                foreach (var row in viewData)
                {
                    string newPresence = null;
                    if (!string.IsNullOrWhiteSpace(row.ID))
                        rowToUser.TryGetValue(row.ID, out newPresence);

                    if (!string.Equals(row.PresenceUserName, newPresence, StringComparison.OrdinalIgnoreCase))
                        row.PresenceUserName = newPresence;
                }
            }

            // Update active users panel (exclude self)
            var others = data.Users
                .Where(u => !string.Equals(u.UserName, currentUser, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Only update if changed
            bool changed = others.Count != _activeUsers.Count;
            if (!changed)
            {
                for (int i = 0; i < others.Count; i++)
                {
                    if (i >= _activeUsers.Count || !string.Equals(_activeUsers[i].UserName, others[i].UserName, StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                _activeUsers.Clear();
                foreach (var u in others)
                    _activeUsers.Add(u);
                OnPropertyChanged(nameof(ActiveUsers));
                OnPropertyChanged(nameof(HasActiveUsers));
            }
        }

        public bool HasActiveUsers => _activeUsers.Count > 0;

        /// <summary>
        /// Notifies the presence engine of the currently selected row IDs (multi-select).
        /// Called from SelectionChanged event handler.
        /// </summary>
        private void UpdatePresenceActiveRow()
        {
            try
            {
                var items = ResultsDataGrid?.SelectedItems;
                if (items == null || items.Count == 0)
                {
                    _syncEngine?.SetActiveRowIds(null);
                    return;
                }
                var ids = new List<string>();
                foreach (var item in items)
                {
                    if (item is ReconciliationViewData r && !string.IsNullOrWhiteSpace(r.ID))
                        ids.Add(r.ID);
                }
                _syncEngine?.SetActiveRowIds(ids.Count > 0 ? ids : null);
            }
            catch { }
        }
    }
}
