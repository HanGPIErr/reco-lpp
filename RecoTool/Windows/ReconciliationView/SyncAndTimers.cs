using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RecoTool.Services;
using RecoTool.Services.DTOs;

namespace RecoTool.Windows
{
    // Partial: Sync events, debounce timers, and VM change handling
    public partial class ReconciliationView
    {
        private void InitializeFilterDebounce()
        {
            _filterDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _filterDebounceTimer.Tick += FilterDebounceTimer_Tick;
        }

        // Ensure the push debounce timer exists
        private void EnsurePushDebounceTimer()
        {
            if (_pushDebounceTimer != null) return;
            _pushDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _pushDebounceTimer.Tick += PushDebounceTimer_Tick;
        }

        // Timer handlers (named so we can unsubscribe on Unloaded)
        private void FilterDebounceTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _filterDebounceTimer?.Stop();
                try { ApplyFilters(); } catch { }
            }
            catch { }
        }

        private void PushDebounceTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Disable automatic background push: do not enqueue any network sync here
                if (_pushDebounceTimer != null)
                {
                    _pushDebounceTimer.Stop();
                }
                return;
            }
            catch { }
        }

        // Public entry to schedule a debounced background push after local edits
        public void ScheduleBulkPushDebounced()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_currentCountryId)) return;
                SyncMonitorService.Instance?.QueueBulkPush(_currentCountryId);
            }
            catch { }
        }

        private void SubscribeToSyncEvents()
        {
            try
            {
                if (_syncEventsHooked) return;
                var svc = SyncMonitorService.Instance;
                if (svc != null)
                {
                    svc.SyncStateChanged += OnSyncStateChanged;
                    svc.SyncPulledChanges += OnSyncPulledChanges;
                    _syncEventsHooked = true;
                }
            }
            catch { }
        }

        private volatile bool _syncRefreshPending;

        private void OnSyncPulledChanges(string countryId, int pulledCount)
        {
            if (pulledCount <= 0) return;
            if (!string.Equals(countryId, _currentCountryId, StringComparison.OrdinalIgnoreCase)) return;
            if (_syncRefreshPending) return; // Coalesce: skip if a refresh is already queued

            _syncRefreshPending = true;

            // Single lightweight Dispatcher call — does cache clear + refresh, nothing else heavy
            Dispatcher?.InvokeAsync(async () =>
            {
                try
                {
                    _reconciliationService?.ClearViewCache();
                    await RefreshAsync();
                    ShowToast($"🔄 {pulledCount} row(s) synced from another user");
                }
                catch { }
                finally { _syncRefreshPending = false; }
            }, DispatcherPriority.Background);
        }

        private void ReconciliationView_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_syncEventsHooked)
                {
                    var svc = SyncMonitorService.Instance;
                    if (svc != null)
                    {
                        svc.SyncStateChanged -= OnSyncStateChanged;
                        svc.SyncPulledChanges -= OnSyncPulledChanges;
                    }
                    _syncEventsHooked = false;
                }
                // Stop presence engine
                try { StopPresenceEngine(); } catch { }
                // Unsubscribe from rule-applied event
                try { _reconciliationService.RuleApplied -= ReconciliationService_RuleApplied; } catch { }
                // Stop session warning banner refresh timer
                try { SessionWarningBanner?.Stop(); } catch { }
                if (_highlightClearTimer != null)
                {
                    _highlightClearTimer.Stop();
                    _highlightClearTimer.Tick -= HighlightClearTimer_Tick;
                    _highlightClearTimer = null;
                }
                // Stop and release debounce timers
                if (_filterDebounceTimer != null)
                {
                    _filterDebounceTimer.Stop();
                    _filterDebounceTimer.Tick -= FilterDebounceTimer_Tick;
                    _filterDebounceTimer = null;
                }
                if (_pushDebounceTimer != null)
                {
                    _pushDebounceTimer.Stop();
                    _pushDebounceTimer.Tick -= PushDebounceTimer_Tick;
                    _pushDebounceTimer = null;
                }
                // Unhook grid scroll events
                if (_resultsScrollViewer != null)
                {
                    try { _resultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged; } catch { }
                    _resultsScrollViewer = null;
                }
                _scrollHooked = false;
                // Detach VM change notifications
                try { VM.PropertyChanged -= VM_PropertyChanged; } catch { }
            }
            catch { }
        }

        private void OnSyncStateChanged(OfflineFirstService.SyncStateChangedEventArgs e)
        {
            // No-op: sync indicator removed from UI. Only keep the data refresh path.
            _ = HandleSyncStateChangedAsync(e);
        }

        // When any VM Filter* property changes, debounce ApplyFilters
        private void VM_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                if (e == null) { ScheduleApplyFiltersDebounced(); return; }
                if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName.StartsWith("Filter", StringComparison.Ordinal))
                {
                    ScheduleApplyFiltersDebounced();
                }
            }
            catch { }
        }

        private Task HandleSyncStateChangedAsync(OfflineFirstService.SyncStateChangedEventArgs e)
        {
            // NO-OP: Data refresh is now handled exclusively by OnSyncPulledChanges (fires only when actual changes are pulled).
            // Previously this method did a full RefreshAsync + snapshot diff on EVERY UpToDate event (every 10s push cycle),
            // causing massive UI lag even when nothing changed.
            return Task.CompletedTask;
        }

        private void StartHighlightClearTimer()
        {
            try
            {
                if (_highlightClearTimer == null)
                {
                    _highlightClearTimer = new DispatcherTimer();
                    _highlightClearTimer.Interval = TimeSpan.FromMilliseconds(HighlightDurationMs);
                    _highlightClearTimer.Tick += HighlightClearTimer_Tick;
                }
                else
                {
                    _highlightClearTimer.Stop();
                }
                _highlightClearTimer.Interval = TimeSpan.FromMilliseconds(HighlightDurationMs);
                _highlightClearTimer.Start();
            }
            catch { }
        }

        private void HighlightClearTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _highlightClearTimer?.Stop();
                // Clear flags on all rows
                foreach (var r in _allViewData ?? Enumerable.Empty<ReconciliationViewData>())
                {
                    try
                    {
                        if (r.IsNewlyAdded) r.IsNewlyAdded = false;
                        if (r.IsUpdated) r.IsUpdated = false;
                        if (r.IsHighlighted) r.IsHighlighted = false;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ScheduleApplyFiltersDebounced()
        {
            try
            {
                _filterDebounceTimer.Stop();
                _filterDebounceTimer.Start();
            }
            catch { }
        }

        public void QueueBulkPush()
        {
            try
            {
                SyncMonitorService.Instance?.QueueBulkPush(_currentCountryId);
            }
            catch { }
        }
    }
}
