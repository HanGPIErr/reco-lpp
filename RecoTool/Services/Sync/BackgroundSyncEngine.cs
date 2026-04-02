using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Services.Sync
{
    /// <summary>
    /// Dedicated background sync engine that runs on its own thread.
    /// Separates fast-path presence/change detection (every 2s, file I/O only)
    /// from slow-path push/pull (OleDb, only when changes detected).
    /// 
    /// NEVER blocks the UI thread. All callbacks are fire-and-forget.
    /// </summary>
    public sealed class BackgroundSyncEngine : IDisposable
    {
        private readonly Func<OfflineFirstService> _serviceProvider;
        private CancellationTokenSource _cts;
        private Thread _fastThread;  // Fast loop: presence + change detection (2s)
        private Thread _slowThread;  // Slow loop: push/pull OleDb (when triggered)
        private readonly ManualResetEventSlim _pushSignal = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _pullSignal = new ManualResetEventSlim(false);

        private uint _lastKnownSyncVersion;
        private string _presenceFilePath;
        private string _currentUser;
        private string _currentCountryId;
        private volatile List<string> _activeRowIds;
        private volatile bool _disposed;

        // Callbacks (invoked on background thread — caller must dispatch to UI)
        public event Action<string, int> OnRemoteChangesDetected;    // (countryId, pulledCount)
        public event Action<PresenceFile.PresenceData> OnPresenceUpdated; // presence data
        public event Action<string> OnSyncStateChanged;              // "syncing" | "synced" | "offline" | "error"

        public BackgroundSyncEngine(Func<OfflineFirstService> serviceProvider)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _currentUser = Environment.UserName;
        }

        public void Start(string countryId)
        {
            if (_disposed) return;
            Stop();

            _currentCountryId = countryId;
            _cts = new CancellationTokenSource();

            // Resolve presence file path
            try
            {
                var svc = _serviceProvider();
                var dir = svc?.GetParameter("CountryDatabaseDirectory");
                var pfx = svc?.GetParameter("CountryDatabasePrefix") ?? "DB_";
                _presenceFilePath = PresenceFile.GetPresenceFilePath(dir, pfx, countryId);
            }
            catch { _presenceFilePath = null; }

            // Start fast loop (presence + change detection)
            _fastThread = new Thread(FastLoop) { IsBackground = true, Name = "SyncFast", Priority = ThreadPriority.BelowNormal };
            _fastThread.Start();

            // Start slow loop (push/pull)
            _slowThread = new Thread(SlowLoop) { IsBackground = true, Name = "SyncSlow", Priority = ThreadPriority.BelowNormal };
            _slowThread.Start();
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }

            // Remove user from presence file on stop
            if (!string.IsNullOrWhiteSpace(_presenceFilePath))
            {
                try { PresenceFile.RemoveUser(_presenceFilePath, _currentUser); } catch { }
            }

            _cts = null;
        }

        /// <summary>
        /// Signal that a push is needed (call after local edit).
        /// </summary>
        public void SignalPush()
        {
            _pushSignal.Set();
        }

        /// <summary>
        /// Update the currently selected row IDs for presence display (multi-select).
        /// </summary>
        public void SetActiveRowIds(List<string> rowIds)
        {
            _activeRowIds = rowIds;
        }

        /// <summary>
        /// Fast loop: runs every 2s on a dedicated thread.
        /// Reads the presence file (~500 bytes) and checks SyncVersion.
        /// No OleDb. No network DB access. Pure file I/O.
        /// </summary>
        private void FastLoop()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            try
            {
                // Initial read to prime _lastKnownSyncVersion
                try
                {
                    var initial = PresenceFile.Read(_presenceFilePath);
                    if (initial != null) _lastKnownSyncVersion = initial.SyncVersion;
                }
                catch { }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 1) Write heartbeat (updates our presence + active row)
                        if (!string.IsNullOrWhiteSpace(_presenceFilePath))
                        {
                            PresenceFile.WriteHeartbeat(_presenceFilePath, _currentUser, _activeRowIds);
                        }

                        // 2) Read presence data (includes SyncVersion + all users)
                        var data = PresenceFile.Read(_presenceFilePath);
                        if (data != null)
                        {
                            // Notify UI of presence changes (other users online, active rows)
                            try { OnPresenceUpdated?.Invoke(data); } catch { }

                            // Check if SyncVersion changed (another user pushed)
                            if (data.SyncVersion != _lastKnownSyncVersion && _lastKnownSyncVersion > 0)
                            {
                                _lastKnownSyncVersion = data.SyncVersion;
                                _pullSignal.Set(); // Trigger slow-path pull
                            }
                            else
                            {
                                _lastKnownSyncVersion = data.SyncVersion;
                            }
                        }
                    }
                    catch { /* never crash the fast loop */ }

                    // Sleep 2s (interruptible)
                    try { token.WaitHandle.WaitOne(2000); } catch { break; }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        /// <summary>
        /// Slow loop: waits for push/pull signals, then does OleDb operations.
        /// Never runs on a timer — only when explicitly triggered.
        /// </summary>
        private void SlowLoop()
        {
            var token = _cts?.Token ?? CancellationToken.None;
            var handles = new[] { _pushSignal.WaitHandle, _pullSignal.WaitHandle, token.WaitHandle };

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Wait for any signal (push, pull, or cancellation)
                    // Also wake up every 15s as a safety net for periodic push
                    int idx = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(15));

                    if (token.IsCancellationRequested) break;

                    var svc = _serviceProvider?.Invoke();
                    if (svc == null || string.IsNullOrWhiteSpace(_currentCountryId)) continue;

                    bool didPush = false;
                    bool didPull = false;

                    // Handle push
                    if (_pushSignal.IsSet || idx == 2) // idx==2 is timeout (periodic)
                    {
                        _pushSignal.Reset();
                        try
                        {
                            if (svc.AllowBackgroundPushes && !svc.IsAmbreImportInProgress())
                            {
                                OnSyncStateChanged?.Invoke("syncing");
                                // SECURE: Add timeout to prevent indefinite blocking
                                var pushTask = Task.Run(() => svc.PushReconciliationIfPendingAsync(_currentCountryId));
                                var completed = Task.WhenAny(pushTask, Task.Delay(TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();
                                if (completed != pushTask)
                                {
                                    LogManager.Warning("[BackgroundSync] Push timed out after 30s");
                                    continue;
                                }
                                var pushed = pushTask.GetAwaiter().GetResult();
                                didPush = pushed;

                                // After successful push, increment SyncVersion so others detect it
                                if (didPush && !string.IsNullOrWhiteSpace(_presenceFilePath))
                                {
                                    PresenceFile.IncrementSyncVersion(_presenceFilePath);
                                }
                            }
                        }
                        catch { }
                    }

                    // Handle pull
                    if (_pullSignal.IsSet)
                    {
                        _pullSignal.Reset();
                        try
                        {
                            if (!svc.IsAmbreImportInProgress())
                            {
                                OnSyncStateChanged?.Invoke("syncing");
                                // SECURE: Add timeout to prevent indefinite blocking
                                var pullTask = Task.Run(() => svc.PullReconciliationFromNetworkAsync(_currentCountryId));
                                var completed = Task.WhenAny(pullTask, Task.Delay(TimeSpan.FromSeconds(60))).GetAwaiter().GetResult();
                                if (completed != pullTask)
                                {
                                    LogManager.Warning("[BackgroundSync] Pull timed out after 60s");
                                    continue;
                                }
                                var pulled = pullTask.GetAwaiter().GetResult();
                                if (pulled > 0)
                                {
                                    didPull = true;
                                    try { ReconciliationService.InvalidateReconciliationViewCache(_currentCountryId); } catch { }
                                    try { OnRemoteChangesDetected?.Invoke(_currentCountryId, pulled); } catch { }
                                }
                            }
                        }
                        catch { }
                    }

                    if (didPush || didPull)
                        OnSyncStateChanged?.Invoke("synced");
                    else if (!svc.IsNetworkSyncAvailable)
                        OnSyncStateChanged?.Invoke("offline");
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _pushSignal.Dispose();
            _pullSignal.Dispose();
        }
    }
}
