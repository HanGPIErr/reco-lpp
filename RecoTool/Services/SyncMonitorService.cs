using System;
using System.Threading.Tasks;
using System.Timers;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using System.Linq;
using OfflineFirstAccess.Helpers;

namespace RecoTool.Services
{
    /// <summary>
    /// Central service that polls global lock and network availability once and publishes events.
    /// Pages subscribe to receive notifications and trigger their own sync when appropriate.
    /// </summary>
    public sealed class SyncMonitorService : IDisposable
    {
        private static readonly Lazy<SyncMonitorService> _instance = new Lazy<SyncMonitorService>(() => new SyncMonitorService());
        public static SyncMonitorService Instance => _instance.Value;

        private readonly object _gate = new object();
        private System.Timers.Timer _timer;
        private ElapsedEventHandler _timerHandler;
        private Func<OfflineFirstService> _serviceProvider;
        private bool _lastLockActive;
        private bool _lastNetworkAvailable;
        private bool _initialized;
        private bool _disposed;
        private int _isTickRunning;
        private DateTime _lastSuggestUtc = DateTime.MinValue;
        private DateTime _lastForwardUtc = DateTime.MinValue;
        private DateTime _lastPeriodicPushUtc = DateTime.MinValue;
        private DateTime _lastRemoteReconCheckUtc = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtcDate)> _remoteReconFingerprint = new ConcurrentDictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _bulkPushQueue = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
        public TimeSpan SuggestCooldown { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan ForwardCooldown { get; set; } = TimeSpan.FromMilliseconds(300);
        public TimeSpan PeriodicPushInterval { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan RemoteReconCheckInterval { get; set; } = TimeSpan.FromSeconds(3);

        // Cached sync marker timestamps: countryId -> last known marker content
        private readonly ConcurrentDictionary<string, string> _syncMarkerCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Skip expensive lock check on every tick: only check every N ticks
        private int _tickCounter;
        private const int LockCheckEveryNTicks = 5; // check lock every 5 ticks (15s)

        // Events
        public event Action<bool> LockStateChanged;              // arg: isActive
        public event Action LockReleased;                        // fired when wasActive -> false
        public event Action NetworkBecameAvailable;              // fired when false -> true
        public event Action<string> SyncSuggested;               // reason: "LockReleased" | "NetworkBecameAvailable"
        public event Action<bool> NetworkAvailabilityChanged;    // arg: isAvailable (true when online)
        public event Action<OfflineFirstService.SyncStateChangedEventArgs> SyncStateChanged; // forwarded from OfflineFirstService

        /// <summary>
        /// Fired after a pull cycle applies changes from the network to the local DB.
        /// Args: (countryId, pulledRowCount). UI should refresh and highlight changed rows.
        /// </summary>
        public event Action<string, int> SyncPulledChanges;

        private SyncMonitorService() { }

        public void Initialize(Func<OfflineFirstService> serviceProvider)
        {
            if (serviceProvider == null) throw new ArgumentNullException(nameof(serviceProvider));
            lock (_gate)
            {
                _serviceProvider = serviceProvider;
                _initialized = true;
                // Prime last known states
                try
                {
                    var svc = _serviceProvider();
                    _lastNetworkAvailable = svc?.IsNetworkSyncAvailable == true;
                    // For lock, default false until first poll
                    _lastLockActive = false;

                    // Subscribe to sync state changes and forward them
                    if (svc != null)
                    {
                        try
                        {
                            svc.SyncStateChanged += (s, e) => ForwardSyncState(e);
                        }
                        catch { }
                    }
                }
                catch
                {
                    _lastNetworkAvailable = false;
                    _lastLockActive = false;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SyncMonitorService));
                if (!_initialized) throw new InvalidOperationException("SyncMonitorService not initialized");
                if (_timer != null) return;

                _timer = new System.Timers.Timer(PollInterval.TotalMilliseconds);
                _timer.AutoReset = true;
                _timerHandler = async (_, e) => await OnTimerElapsed(e);
                _timer.Elapsed += _timerHandler;
                _timer.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_timer != null)
                {
                    if (_timerHandler != null)
                    {
                        _timer.Elapsed -= _timerHandler;
                        _timerHandler = null;
                    }
                    _timer.Stop();
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        public void QueueBulkPush(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return;
            _bulkPushQueue[countryId] = true;
        }

        private async Task OnTimerElapsed(ElapsedEventArgs e)
        {
            if (Interlocked.CompareExchange(ref _isTickRunning, 1, 0) == 1) return;
            try
            {
                var svc = _serviceProvider?.Invoke();
                if (svc == null || string.IsNullOrEmpty(svc.CurrentCountryId)) return;
                var cid = svc.CurrentCountryId;
                _tickCounter++;

                // ──────────────────────────────────────────────────────────────
                // FAST PATH: check sync marker file first (~0.1ms on LAN)
                // This runs every tick (3s) and is extremely cheap
                // ──────────────────────────────────────────────────────────────
                bool remoteChanged = false;
                bool importInProgress = false;
                try { importInProgress = svc.IsAmbreImportInProgress(); } catch { }

                if (!importInProgress)
                {
                    try
                    {
                        remoteChanged = await CheckSyncMarkerChangedAsync(svc, cid).ConfigureAwait(false);
                    }
                    catch { }
                }

                // ──────────────────────────────────────────────────────────────
                // NETWORK CHECK: lightweight, runs every tick
                // ──────────────────────────────────────────────────────────────
                bool networkAvailable = false;
                try { networkAvailable = svc.IsNetworkSyncAvailable; } catch { networkAvailable = false; }

                if (networkAvailable != _lastNetworkAvailable)
                {
                    _lastNetworkAvailable = networkAvailable;
                    SafeInvoke(() => NetworkAvailabilityChanged?.Invoke(networkAvailable));
                    if (networkAvailable && !_lastLockActive)
                    {
                        SafeInvoke(() => NetworkBecameAvailable?.Invoke());
                        SuggestIfCooldownAllows("NetworkBecameAvailable");
                    }
                }

                // ──────────────────────────────────────────────────────────────
                // LOCK CHECK: expensive (network DB), only every 10 ticks (~30s)
                // Wrapped in Task.Run + timeout to avoid stalling the timer thread
                // ──────────────────────────────────────────────────────────────
                if (_tickCounter % 10 == 0)
                {
                    bool lockActive = false;
                    try
                    {
                        var lockTask = Task.Run(() => svc.IsGlobalLockActiveAsync());
                        if (await Task.WhenAny(lockTask, Task.Delay(3000)) == lockTask)
                            lockActive = await lockTask;
                    }
                    catch { lockActive = false; }
                    if (lockActive != _lastLockActive)
                    {
                        _lastLockActive = lockActive;
                        SafeInvoke(() => LockStateChanged?.Invoke(lockActive));
                        if (!lockActive) { SafeInvoke(() => LockReleased?.Invoke()); SuggestIfCooldownAllows("LockReleased"); }
                    }
                }

                if (!networkAvailable || _lastLockActive || importInProgress) return;

                // ──────────────────────────────────────────────────────────────
                // PUSH: fire-and-forget (never blocks the tick)
                // ──────────────────────────────────────────────────────────────
                if (svc.AllowBackgroundPushes)
                {
                    bool shouldPush = false;
                    foreach (var queuedCid in _bulkPushQueue.Keys.ToArray())
                    {
                        _bulkPushQueue.TryRemove(queuedCid, out _);
                        shouldPush = true;
                    }
                    var nowUtc = DateTime.UtcNow;
                    if (nowUtc - _lastPeriodicPushUtc >= PeriodicPushInterval)
                    {
                        _lastPeriodicPushUtc = nowUtc;
                        shouldPush = true;
                    }
                    if (shouldPush)
                    {
                        // Fire-and-forget: push runs on thread pool, never blocks the tick
                        _ = Task.Run(async () =>
                        {
                            try { await svc.PushReconciliationIfPendingAsync(cid); } catch { }
                        });
                    }
                }

                // ──────────────────────────────────────────────────────────────
                // PULL: fire-and-forget when sync marker changed
                // ──────────────────────────────────────────────────────────────
                if (remoteChanged)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var pulled = await svc.PullReconciliationFromNetworkAsync(cid);
                            if (pulled > 0)
                            {
                                try { ReconciliationService.InvalidateReconciliationViewCache(cid); } catch { }
                                SafeInvoke(() => SyncPulledChanges?.Invoke(cid, pulled));
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                LogManager.Error("[SYNC-MONITOR] Timer tick failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _isTickRunning, 0);
            }
        }

        /// <summary>
        /// Fast-path change detection using a lightweight .sync marker file.
        /// After each push, the pusher writes a timestamp to {CountryDir}/{prefix}{countryId}.sync.
        /// Reading this tiny file (~30 bytes) takes <1ms on LAN vs 100ms-3s for FileInfo on .accdb.
        /// Returns true if the marker changed since last check (= another user pushed changes).
        /// </summary>
        private async Task<bool> CheckSyncMarkerChangedAsync(OfflineFirstService svc, string countryId)
        {
            try
            {
                string remoteDir = svc.GetParameter("CountryDatabaseDirectory");
                if (string.IsNullOrWhiteSpace(remoteDir)) return false;
                string prefix = svc.GetParameter("CountryDatabasePrefix") ?? "DB_";
                var markerPath = Path.Combine(remoteDir, $"{prefix}{countryId}.sync");

                // Read marker content with timeout (should be <1ms on LAN, 2s max on slow VPN)
                string content = null;
                try
                {
                    var readTask = Task.Run(() =>
                    {
                        if (!File.Exists(markerPath)) return null;
                        return File.ReadAllText(markerPath).Trim();
                    });
                    if (await Task.WhenAny(readTask, Task.Delay(2000)) == readTask)
                        content = await readTask;
                }
                catch { return false; }

                if (string.IsNullOrWhiteSpace(content)) return false;

                // Ignore markers written by ourselves (format: timestamp|MachineName|UserName)
                try
                {
                    var parts = content.Split('|');
                    if (parts.Length >= 3)
                    {
                        var markerMachine = parts[1];
                        var markerUser = parts[2];
                        if (string.Equals(markerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(markerUser, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                        {
                            // This marker was written by us — update cache but don't trigger pull
                            _syncMarkerCache[countryId] = content;
                            return false;
                        }
                    }
                }
                catch { }

                var previous = _syncMarkerCache.GetOrAdd(countryId, string.Empty);
                if (string.Equals(content, previous, StringComparison.Ordinal))
                    return false; // No change

                _syncMarkerCache[countryId] = content;
                // First read (previous was empty) = initial sync, don't trigger pull
                if (string.IsNullOrEmpty(previous)) return false;

                return true; // Marker changed = another user pushed
            }
            catch { return false; }
        }

        /// <summary>
        /// Writes the sync marker file after a successful push.
        /// Called by OfflineFirstService after PushPendingChangesToNetworkAsync completes.
        /// </summary>
        public static void WriteSyncMarker(string countryDatabaseDirectory, string countryDatabasePrefix, string countryId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(countryDatabaseDirectory) || string.IsNullOrWhiteSpace(countryId)) return;
                var prefix = string.IsNullOrWhiteSpace(countryDatabasePrefix) ? "DB_" : countryDatabasePrefix;
                var markerPath = Path.Combine(countryDatabaseDirectory, $"{prefix}{countryId}.sync");
                // Write timestamp + machine name for debugging
                File.WriteAllText(markerPath, $"{DateTime.UtcNow:O}|{Environment.MachineName}|{Environment.UserName}");
            }
            catch { /* best-effort, don't fail the push */ }
        }

        private void SuggestIfCooldownAllows(string reason)
        {
            var now = DateTime.UtcNow;
            if (now - _lastSuggestUtc < SuggestCooldown) return;
            _lastSuggestUtc = now;
            SafeInvoke(() => SyncSuggested?.Invoke(reason));
        }

        private void ForwardSyncState(OfflineFirstService.SyncStateChangedEventArgs e)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastForwardUtc < ForwardCooldown) return;
                _lastForwardUtc = now;
                SafeInvoke(() => SyncStateChanged?.Invoke(e));
            }
            catch { }
        }

        /// <summary>
        /// Raises the SyncPulledChanges event from external callers (e.g., OfflineFirstService after push+pull).
        /// </summary>
        public void RaiseSyncPulledChanges(string countryId, int pulledCount)
        {
            if (pulledCount > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC-MONITOR] RaiseSyncPulledChanges: country={countryId}, pulled={pulledCount}, subscribers={SyncPulledChanges?.GetInvocationList()?.Length ?? 0}");
                SafeInvoke(() => SyncPulledChanges?.Invoke(countryId, pulledCount));
            }
        }

        private static void SafeInvoke(Action action)
        {
            try { action?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
        }
    }
}
