using OfflineFirstAccess.Helpers;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Helpers;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Services.Helpers;
using System.Collections.Concurrent;

namespace RecoTool.Services
{
    // Partial: push/pull synchronization operations
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Pousse les changements locaux de réconciliation s'il y en a, si le réseau est disponible et aucun lock global.
        /// </summary>
        public async Task<bool> PushReconciliationIfPendingAsync(string countryId)
        {
            try
            {
                // Global kill-switch: do not push if background pushes are disabled
                if (!AllowBackgroundPushes)
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(countryId)) return false;
                // Basculer si besoin
                if (!string.Equals(countryId, _currentCountryId, StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await SetCurrentCountryAsync(countryId, suppressPush: true);
                    if (!ok) return false;
                }

                var cid = _currentCountryId;
                var diag = IsDiagSyncEnabled();

                // Debounce: skip if a push just happened very recently
                var last = _lastPushTimesUtc.TryGetValue(cid, out var t) ? t : DateTime.MinValue;
                if (DateTime.UtcNow - last < _pushCooldown)
                {
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PUSH][{cid}] Skipped due to cooldown. SinceLast={(DateTime.UtcNow - last).TotalSeconds:F1}s, Cooldown={_pushCooldown.TotalSeconds}s");
                        try { LogManager.Info($"[PUSH][{cid}] Skipped due to cooldown"); } catch { }
                    }
                    return false;
                }

                if (diag)
                {
                    var lastDbg = _lastPushTimesUtc.TryGetValue(cid, out var tdbg) ? tdbg : DateTime.MinValue;
                    System.Diagnostics.Debug.WriteLine($"[PUSH][{cid}] Enter PushReconciliationIfPendingAsync. NowUtc={DateTime.UtcNow:o}, LastPushUtc={lastDbg:o}");
                    try { LogManager.Info($"[PUSH][{cid}] Enter PushReconciliationIfPendingAsync"); } catch { }
                }

                // FAST PATH: Check for pending changes FIRST (local DB only, ~5ms)
                // This avoids the expensive network lock check when there's nothing to push
                List<OfflineFirstAccess.Models.ChangeLogEntry> recoUnsynced = null;
                try
                {
                    var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(cid));
                    var tmp = await tracker.GetUnsyncedChangesAsync();
                    recoUnsynced = tmp?.Where(e => string.Equals(e?.TableName, "T_Reconciliation", StringComparison.OrdinalIgnoreCase)).ToList()
                                   ?? new List<OfflineFirstAccess.Models.ChangeLogEntry>();
                }
                catch { recoUnsynced = new List<OfflineFirstAccess.Models.ChangeLogEntry>(); }

                if (!recoUnsynced.Any())
                {
                    _lastPushTimesUtc[cid] = DateTime.UtcNow;
                    return true; // Nothing to push — skip all network I/O
                }

                // Only check network/lock when we actually have changes to push
                if (!IsNetworkSyncAvailable)
                {
                    try { await RaiseSyncStateAsync(cid, SyncStateKind.OfflinePending); } catch { }
                    return false;
                }
                bool lockActiveByOthers = false;
                try { lockActiveByOthers = await IsGlobalLockActiveByOthersAsync(); } catch { }
                if (lockActiveByOthers)
                {
                    try { await RaiseSyncStateAsync(cid, SyncStateKind.OfflinePending); } catch { }
                    return false;
                }

                try { await RaiseSyncStateAsync(cid, SyncStateKind.SyncInProgress, pendingOverride: recoUnsynced.Count); } catch { }

                // Push granulaire (T_Reconciliation uniquement) SANS verrou global.
                // Access/OleDb gère les écritures concurrentes via son propre .ldb file locking.
                // Le GlobalLock ajoutait 2 allers-retours réseau inutiles (INSERT+DELETE SyncLocks) sur réseau lent.
                // On garde seulement le check passif IsGlobalLockActiveByOthersAsync (ci-dessus) pour éviter de pusher pendant un import.
                if (diag)
                {
                    System.Diagnostics.Debug.WriteLine($"[PUSH][{cid}] Invoking PushPendingChangesToNetworkAsync (Lite/T_Reconciliation, no global lock)...");
                    try { LogManager.Info($"[PUSH][{cid}] Invoking PushPendingChangesToNetworkAsync (Lite/T_Reconciliation, no global lock)"); } catch { }
                }
                await PushPendingChangesToNetworkAsync(cid, assumeLockHeld: true, source: nameof(PushReconciliationIfPendingAsync) + "/Lite", preloadedUnsynced: recoUnsynced);
                // Two-way: pull network changes back to local (LastModified first, then Version)
                try
                {
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PULL][{cid}] Starting PullReconciliationFromNetworkAsync after push...");
                        try { LogManager.Info($"[PULL][{cid}] Starting PullReconciliationFromNetworkAsync after push"); } catch { }
                    }
                    var pulled = await PullReconciliationFromNetworkAsync(cid);
                    if (pulled > 0)
                    {
                        try { ReconciliationService.InvalidateReconciliationViewCache(cid); } catch { }
                        // Do NOT fire SyncPulledChanges here — this pull runs right after OUR OWN push,
                        // so pulled rows are likely our own changes echoed back from the network.
                        // The SyncMonitorService marker-based detection handles true remote changes.
                    }
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PULL][{cid}] Completed pull. Applied {pulled} row(s) from network to local.");
                        try { LogManager.Info($"[PULL][{cid}] Completed pull. Applied {pulled} row(s)"); } catch { }
                    }
                }
                catch (Exception exPull)
                {
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PULL][{cid}] PullReconciliationFromNetworkAsync failed: {exPull.Message}");
                        try { LogManager.Error($"[PULL][{cid}] PullReconciliationFromNetworkAsync failed: {exPull.Message}", exPull); } catch { }
                    }
                    // best-effort: do not fail the overall operation
                }
                _lastPushTimesUtc[cid] = DateTime.UtcNow;
                try { await RaiseSyncStateAsync(cid, SyncStateKind.UpToDate, pendingOverride: 0); } catch { }

                // Write sync marker so other users detect the change within 3s
                try
                {
                    var dir = GetParameter("CountryDatabaseDirectory");
                    var pfx = GetParameter("CountryDatabasePrefix") ?? "DB_";
                    SyncMonitorService.WriteSyncMarker(dir, pfx, cid);
                }
                catch { }
                if (diag)
                {
                    System.Diagnostics.Debug.WriteLine($"[PUSH][{cid}] Completed PushReconciliationIfPendingAsync successfully.");
                    try { LogManager.Info($"[PUSH][{cid}] Completed PushReconciliationIfPendingAsync"); } catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                try { await RaiseSyncStateAsync(countryId, SyncStateKind.Error, error: ex); } catch { }
                try { LogManager.Error($"[PUSH] PushReconciliationIfPendingAsync failed for {countryId}: {ex}", ex); } catch { }
                return false;
            }
        }

        // ── Cached pull metadata (discovered once per country, reused across pulls) ──
        private static readonly ConcurrentDictionary<string, PullMetadata> _pullMetadataCache
            = new ConcurrentDictionary<string, PullMetadata>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> _pullWatermarks
            = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private class PullMetadata
        {
            public string PkCol;
            public List<string> SelectCols;
            public string ColList;
            public bool HasLM, HasVer;
            public Dictionary<string, OleDbType> LocalTypes;
        }

        /// <summary>
        /// Pull network changes for T_Reconciliation back into the local database.
        /// OPTIMIZED: caches column metadata, uses persistent LastModified watermark,
        /// skips full local map scan by using server-side WHERE filter.
        /// Typical incremental pull (0 changes): ~500ms. With changes: ~1s per 100 rows.
        /// </summary>
        public async Task<int> PullReconciliationFromNetworkAsync(string countryId)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            int applied = 0;

            // Use persistent network connection to avoid repeated Open/Close (.ldb churn) on slow networks
            var netConn = await GetOrOpenNetworkConnectionAsync();
            if (netConn == null || netConn.State != ConnectionState.Open)
            {
                // Fallback: ephemeral connection if persistent fails
                netConn = new OleDbConnection(GetNetworkCountryConnectionString(countryId));
                await netConn.OpenAsync();
            }
            // netConn is NOT disposed here — it's either persistent (managed by CloseNetworkConnection) or fallback
            bool isEphemeralNet = !ReferenceEquals(netConn, _persistentNetworkConn);

            try
            {
            using (var localConn = new OleDbConnection(GetCountryConnectionString(countryId)))
            {
                await localConn.OpenAsync();

                // ── Discover metadata ONCE per country (cached for session) ──
                var meta = _pullMetadataCache.GetOrAdd(countryId, _ =>
                {
                    var m = new PullMetadata();
                    m.PkCol = GetPrimaryKeyColumnAsync(localConn, "T_Reconciliation").Result;
                    var localCols = GetTableColumnsAsync(localConn, "T_Reconciliation").Result;
                    var netCols = GetTableColumnsAsync(netConn, "T_Reconciliation").Result;
                    var common = new HashSet<string>(localCols.Intersect(netCols, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                    common.Add(m.PkCol);
                    m.HasVer = common.Contains("Version");
                    m.HasLM = common.Contains("LastModified");
                    m.SelectCols = common.OrderBy(c => string.Equals(c, m.PkCol, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                        .ThenBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                    m.ColList = string.Join(", ", m.SelectCols.Select(c => $"[{c}]"));
                    m.LocalTypes = OleDbSchemaHelper.GetColumnTypesAsync(localConn, "T_Reconciliation").Result;
                    return m;
                });

                if (string.IsNullOrWhiteSpace(meta.PkCol)) return 0;

                // ── Use persistent watermark for incremental pull ──
                // This avoids scanning the full local table for MAX(LastModified) every pull
                DateTime? watermark = null;
                if (meta.HasLM)
                {
                    if (_pullWatermarks.TryGetValue(countryId, out var cached))
                    {
                        watermark = cached;
                    }
                    else
                    {
                        // First pull: compute from local DB (one-time cost)
                        try { watermark = await OleDbUtils.GetMaxLastModifiedAsync(localConn, "T_Reconciliation"); } catch { }
                        if (!watermark.HasValue) _lastSyncTimes.TryGetValue(countryId, out var ts);
                    }
                }

                // ── Build network query with server-side filter ──
                var ncmd = new OleDbCommand { Connection = netConn };
                if (meta.HasLM && watermark.HasValue)
                {
                    ncmd.CommandText = $"SELECT {meta.ColList} FROM [T_Reconciliation] WHERE [LastModified] > ?";
                    ncmd.Parameters.Add(new OleDbParameter("@pLM", OleDbType.Date) { Value = OleDbSchemaHelper.CoerceValueForOleDb(watermark.Value, OleDbType.Date) });
                }
                else
                {
                    ncmd.CommandText = $"SELECT {meta.ColList} FROM [T_Reconciliation]";
                }

                // ── Build local ID set for INSERT vs UPDATE detection (lightweight: PK only) ──
                var localIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var cmd = new OleDbCommand($"SELECT [{meta.PkCol}] FROM [T_Reconciliation]", localConn))
                    using (var rd = await cmd.ExecuteReaderAsync())
                    {
                        while (await rd.ReadAsync())
                        {
                            var pk = Convert.ToString(rd[0]);
                            if (!string.IsNullOrWhiteSpace(pk)) localIds.Add(pk);
                        }
                    }
                }
                catch { }

                // ── Apply changes from network ──
                DateTime? maxAppliedLm = watermark;
                using (var nrd = await ncmd.ExecuteReaderAsync())
                {
                    while (await nrd.ReadAsync())
                    {
                        var pkVal = nrd[meta.PkCol];
                        if (pkVal == null || pkVal == DBNull.Value) continue;
                        var pkStr = Convert.ToString(pkVal);

                        // Track watermark for next pull
                        if (meta.HasLM)
                        {
                            try
                            {
                                var o = nrd["LastModified"];
                                if (o != null && o != DBNull.Value)
                                {
                                    var lm = Convert.ToDateTime(o);
                                    if (!maxAppliedLm.HasValue || lm > maxAppliedLm.Value)
                                        maxAppliedLm = lm;
                                }
                            }
                            catch { }
                        }

                        // Build values
                        var rowVals = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (var c in meta.SelectCols) rowVals[c] = nrd[c];

                        bool existsLocal = localIds.Contains(pkStr);

                        if (existsLocal)
                        {
                            var setCols = meta.SelectCols.Where(c => !string.Equals(c, meta.PkCol, StringComparison.OrdinalIgnoreCase)).ToList();
                            var setParts = setCols.Select((c, i) => $"[{c}] = @p{i}").ToList();
                            using (var up = new OleDbCommand($"UPDATE [T_Reconciliation] SET {string.Join(", ", setParts)} WHERE [{meta.PkCol}] = @key", localConn))
                            {
                                for (int i = 0; i < setCols.Count; i++)
                                {
                                    var c = setCols[i];
                                    meta.LocalTypes.TryGetValue(c, out var t);
                                    up.Parameters.Add(new OleDbParameter($"@p{i}", t == 0 ? OleDbSchemaHelper.InferOleDbTypeFromValue(rowVals[c]) : t)
                                    { Value = OleDbSchemaHelper.CoerceValueForOleDb(rowVals[c], t == 0 ? OleDbSchemaHelper.InferOleDbTypeFromValue(rowVals[c]) : t) });
                                }
                                meta.LocalTypes.TryGetValue(meta.PkCol, out var tkey);
                                up.Parameters.Add(new OleDbParameter("@key", tkey == 0 ? OleDbSchemaHelper.InferOleDbTypeFromValue(pkVal) : tkey)
                                { Value = OleDbSchemaHelper.CoerceValueForOleDb(pkVal, tkey == 0 ? OleDbSchemaHelper.InferOleDbTypeFromValue(pkVal) : tkey) });
                                await up.ExecuteNonQueryAsync();
                            }
                        }
                        else
                        {
                            var ph = string.Join(", ", meta.SelectCols.Select((c, i) => $"@p{i}"));
                            var colListIns = string.Join(", ", meta.SelectCols.Select(c => $"[{c}]"));
                            using (var ins = new OleDbCommand($"INSERT INTO [T_Reconciliation] ({colListIns}) VALUES ({ph})", localConn))
                            {
                                for (int i = 0; i < meta.SelectCols.Count; i++)
                                {
                                    var c = meta.SelectCols[i];
                                    meta.LocalTypes.TryGetValue(c, out var t);
                                    var v = rowVals[c];
                                    ins.Parameters.Add(new OleDbParameter($"@p{i}", t == 0 ? OleDbSchemaHelper.InferOleDbTypeFromValue(v) : t)
                                    { Value = OleDbSchemaHelper.CoerceValueForOleDb(v, t == 0 ? OleDbSchemaHelper.InferOleDbTypeFromValue(v) : t) });
                                }
                                await ins.ExecuteNonQueryAsync();
                            }
                            localIds.Add(pkStr); // track for subsequent rows in same pull
                        }
                        applied++;
                    }
                }

                // ── Update watermark for next pull ──
                if (maxAppliedLm.HasValue)
                    _pullWatermarks[countryId] = maxAppliedLm.Value;
            }

            return applied;
            }
            finally
            {
                // Dispose ephemeral fallback connection only (persistent is managed by CloseNetworkConnection)
                if (isEphemeralNet)
                {
                    try { netConn?.Close(); } catch { }
                    try { netConn?.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Pousse de manière robuste les changements locaux en attente vers la base réseau sous verrou global.
        /// Applique INSERT/UPDATE/DELETE sur la base réseau à partir de l'état local pour chaque ChangeLog non synchronisé trouvé,
        /// puis marque uniquement ces entrées comme synchronisées. Ignore les entrées qui ne correspondent pas à une ligne locale.
        /// </summary>
        public async Task<int> PushPendingChangesToNetworkAsync(string countryId, bool assumeLockHeld = false, CancellationToken token = default, string source = null, IEnumerable<OfflineFirstAccess.Models.ChangeLogEntry> preloadedUnsynced = null)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            var diag = IsDiagSyncEnabled();
            if (diag)
            {
                var src = string.IsNullOrWhiteSpace(source) ? "-" : source;
                System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Enter PushPendingChangesToNetworkAsync (assumeLockHeld={assumeLockHeld}, src={src})");
                try { LogManager.Info($"[PUSH][{countryId}] Enter PushPendingChangesToNetworkAsync (assumeLockHeld={assumeLockHeld}, src={src})"); } catch { }
            }

            // Coalesce concurrent calls per country in a race-free way: only one task is created.
            async Task<int> RunAsync()
            {
                var sem = _pushSemaphores.GetOrAdd(countryId, _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(token);
                try { return await PushPendingChangesToNetworkCoreAsync(countryId, assumeLockHeld, token, preloadedUnsynced); }
                finally { try { sem.Release(); } catch { } }
            }

            bool created = false;
            var task = _activePushes.GetOrAdd(countryId, _ => { created = true; return RunAsync(); });
            if (!created)
            {
                // A push is already in progress for this country: wait for it to finish and return its result
                // Do not start a new push and avoid extra logs/noise.
                try { return await task; }
                finally { /* no-op: the remover will run when original creator finishes */ }
            }
            try { return await task; }
            finally { _activePushes.TryRemove(countryId, out _); }
        }

        // Core implementation separated so we can gate/coalesce the public entry
        private async Task<int> PushPendingChangesToNetworkCoreAsync(string countryId, bool assumeLockHeld, CancellationToken token, IEnumerable<OfflineFirstAccess.Models.ChangeLogEntry> preloadedUnsynced)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            // S'assurer que le service est positionné sur le bon pays (AcquireGlobalLockAsync utilise _currentCountryId)
            if (!string.Equals(countryId, _currentCountryId, StringComparison.OrdinalIgnoreCase))
            {
                var ok = await SetCurrentCountryAsync(countryId, suppressPush: true);
                if (!ok) throw new InvalidOperationException($"Impossible d'initialiser la base locale pour {countryId}");
            }

            var diag = IsDiagSyncEnabled();
            var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(countryId));

            // Start a per-run watchdog early to capture stalls in FetchUnsynced as well
            var runId = Guid.NewGuid();
            _pushRunIds[countryId] = runId;
            var watchdogCts = new CancellationTokenSource();
            if (diag)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), watchdogCts.Token);
                        if (watchdogCts.Token.IsCancellationRequested) return;
                        if (_pushRunIds.TryGetValue(countryId, out var currentId) && currentId == runId)
                        {
                            _pushStages.TryGetValue(countryId, out var stageMsg);
                            System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Still running after 30s... stage={stageMsg}");
                            try { LogManager.Info($"[PUSH][{countryId}] Still running after 30s... stage={stageMsg}"); } catch { }
                        }
                    }
                    catch { }
                });
            }

            // Récupérer les entrées non synchronisées (utiliser la liste préchargée si fournie)
            List<OfflineFirstAccess.Models.ChangeLogEntry> unsynced;
            if (preloadedUnsynced != null)
            {
                _pushStages[countryId] = "UsePreloadedUnsynced";
                unsynced = preloadedUnsynced.ToList();
                if (diag) { System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Using preloaded unsynced: {unsynced.Count}"); try { LogManager.Info($"[PUSH][{countryId}] Using preloaded unsynced: {unsynced.Count}"); } catch { } }
            }
            else
            {
                _pushStages[countryId] = "FetchUnsynced";
                if (diag) { System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Fetching unsynced changes from ChangeLog..."); try { LogManager.Info($"[PUSH][{countryId}] Fetching unsynced changes from ChangeLog..."); } catch { } }
                // Service-level hard cap slightly above inner 15s reader timeout
                var fetchTask = tracker.GetUnsyncedChangesAsync();
                var fetchCompleted = await Task.WhenAny(fetchTask, Task.Delay(TimeSpan.FromSeconds(20), token)) == fetchTask;
                if (!fetchCompleted)
                {
                    if (diag) { System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Timeout fetching unsynced changes after 20s"); try { LogManager.Info($"[PUSH][{countryId}] Timeout fetching unsynced changes after 20s"); } catch { } }
                    try { watchdogCts.Cancel(); } catch { }
                    try { watchdogCts.Dispose(); } catch { }
                    try { if (_pushRunIds.TryGetValue(countryId, out var id) && id == runId) _pushRunIds.TryRemove(countryId, out _); } catch { }
                    throw new TimeoutException("Timeout fetching unsynced changes from ChangeLog after 20s");
                }
                unsynced = (await fetchTask)?.ToList() ?? new List<OfflineFirstAccess.Models.ChangeLogEntry>();
            }
            if (diag) { System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Unsynced fetched: {unsynced.Count}"); try { LogManager.Info($"[PUSH][{countryId}] Unsynced fetched: {unsynced.Count}"); } catch { } }
            if (unsynced.Count == 0)
            {
                try { await RaiseSyncStateAsync(countryId, SyncStateKind.UpToDate, pendingOverride: 0); } catch { }
                try { watchdogCts.Cancel(); } catch { }
                try { watchdogCts.Dispose(); } catch { }
                try { if (_pushRunIds.TryGetValue(countryId, out var id) && id == runId) _pushRunIds.TryRemove(countryId, out _); } catch { }
                return 0;
            }

            // Notify start
            try { await RaiseSyncStateAsync(countryId, SyncStateKind.SyncInProgress, pendingOverride: unsynced.Count); } catch { }
            if (diag)
            {
                System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Push core start. Unsynced={unsynced.Count}");
                try { LogManager.Info($"[PUSH][{countryId}] Push core start. Unsynced={unsynced.Count}"); } catch { }
            }

            // Acquérir le verrou global si non détenu par l'appelant
            IDisposable globalLock = null;
            if (!assumeLockHeld)
            {
                _pushStages[countryId] = "AcquireLock";
                if (diag)
                {
                    System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Acquiring global lock...");
                    try { LogManager.Info($"[PUSH][{countryId}] Acquiring global lock..."); } catch { }
                }
                int lockSecs = 20; // configurable acquire timeout
                try { lockSecs = Math.Max(5, Math.Min(120, int.Parse(GetParameter("GlobalLockAcquireTimeoutSeconds") ?? "20"))); } catch { }
                var acquireTask = AcquireGlobalLockAsync(countryId, "PushPendingChanges", TimeSpan.FromMinutes(5), token);
                var completed = await Task.WhenAny(acquireTask, Task.Delay(TimeSpan.FromSeconds(lockSecs), token)) == acquireTask;
                if (!completed)
                {
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Timeout acquiring global lock after {lockSecs}s");
                        try { LogManager.Info($"[PUSH][{countryId}] Timeout acquiring global lock after {lockSecs}s"); } catch { }
                    }
                    try { watchdogCts.Cancel(); } catch { }
                    try { watchdogCts.Dispose(); } catch { }
                    try { if (_pushRunIds.TryGetValue(countryId, out var id) && id == runId) _pushRunIds.TryRemove(countryId, out _); } catch { }
                    throw new TimeoutException($"Timeout acquiring global lock after {lockSecs}s");
                }
                globalLock = await acquireTask;
                if (globalLock == null)
                {
                    try { watchdogCts.Cancel(); } catch { }
                    try { watchdogCts.Dispose(); } catch { }
                    try { if (_pushRunIds.TryGetValue(countryId, out var id) && id == runId) _pushRunIds.TryRemove(countryId, out _); } catch { }
                    throw new InvalidOperationException($"Impossible d'acquérir le verrou global pour {countryId} (PushPendingChanges)");
                }
                if (diag)
                {
                    System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Global lock acquired.");
                    try { LogManager.Info($"[PUSH][{countryId}] Global lock acquired"); } catch { }
                }
            }
            try
            {
                var appliedIds = new List<long>();

                // Préparer connexions
                // Preflight: ensure network reconciliation DB exists; if missing, create and publish it.
                _pushStages[countryId] = "EnsureNetworkDb";
                try
                {
                    var networkDbPathPre = GetNetworkReconciliationDbPath(countryId);
                    if (string.IsNullOrWhiteSpace(networkDbPathPre)) throw new InvalidOperationException("Network DB path is empty");
                    if (!File.Exists(networkDbPathPre))
                    {
                        if (diag)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Network DB missing. Recreating...");
                            try { LogManager.Info($"[PUSH][{countryId}] Network DB missing. Recreating..."); } catch { }
                        }
                        var recreator = new DatabaseRecreationService();
                        var rep = await recreator.RecreateReconciliationAsync(this, countryId);
                        if (!rep.Success)
                        {
                            throw new InvalidOperationException($"Failed to (re)create network reconciliation DB: {string.Join(" | ", rep.Errors ?? new System.Collections.Generic.List<string>())}");
                        }
                        if (diag)
                        {
                            System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Network DB recreated successfully.");
                            try { LogManager.Info($"[PUSH][{countryId}] Network DB recreated successfully."); } catch { }
                        }
                    }
                }
                catch (Exception exEnsure)
                {
                    // Fail fast: cannot proceed without a network DB
                    try { watchdogCts.Cancel(); } catch { }
                    try { watchdogCts.Dispose(); } catch { }
                    try { if (_pushRunIds.TryGetValue(countryId, out var id) && id == runId) _pushRunIds.TryRemove(countryId, out _); } catch { }
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] EnsureNetworkDb failed: {exEnsure.Message}");
                        try { LogManager.Error($"[PUSH][{countryId}] EnsureNetworkDb failed: {exEnsure.Message}", exEnsure); } catch { }
                    }
                    throw;
                }

                using (var localConn = new OleDbConnection(GetCountryConnectionString(countryId)))
                using (var netConn = new OleDbConnection(GetNetworkCountryConnectionString(countryId)))
                {
                    // Configurable open timeout
                    int openSecs = 20;
                    try { openSecs = Math.Max(5, Math.Min(120, int.Parse(GetParameter("NetworkOpenTimeoutSeconds") ?? "20"))); } catch { }
                    var openTimeout = TimeSpan.FromSeconds(openSecs);

                    _pushStages[countryId] = "OpenLocal";
                    await OleDbUtils.OpenWithTimeoutAsync(localConn, openTimeout, token, IsDiagSyncEnabled() ? countryId : null, isNetwork:false);
                    if (diag) { System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Local DB opened."); try { LogManager.Info($"[PUSH][{countryId}] Local DB opened"); } catch { } }
                    _pushStages[countryId] = "OpenNetwork";
                    await OleDbUtils.OpenWithTimeoutAsync(netConn, openTimeout, token, IsDiagSyncEnabled() ? countryId : null, isNetwork:true);
                    if (diag) { System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Network DB opened."); try { LogManager.Info($"[PUSH][{countryId}] Network DB opened"); } catch { } }

                    _pushStages[countryId] = "BeginTx";
                    using (var tx = netConn.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
                    {
                        try
                        {
                            // Caches de schéma
                            var tableColsCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                            var pkColCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            var colTypeCache = new Dictionary<string, Dictionary<string, OleDbType>>(StringComparer.OrdinalIgnoreCase);

                            Func<string, Task<HashSet<string>>> getColsAsync = async (table) =>
                            {
                                if (tableColsCache.TryGetValue(table, out var set)) return set;
                                set = await GetTableColumnsAsync(netConn, table);
                                tableColsCache[table] = set;
                                return set;
                            };

                            Func<string, Task<string>> getPkAsync = async (table) =>
                            {
                                if (pkColCache.TryGetValue(table, out var pkc)) return pkc;
                                pkc = await GetPrimaryKeyColumnAsync(netConn, table) ?? "ID";
                                pkColCache[table] = pkc;
                                return pkc;
                            };

                            // Network schema column types for robust parameter binding
                            Func<string, Task<Dictionary<string, OleDbType>>> getColTypesAsync = async (table) =>
                            {
                                if (colTypeCache.TryGetValue(table, out var map)) return map;
                                var dt = netConn.GetSchema("Columns", new string[] { null, null, table, null });
                                map = new Dictionary<string, OleDbType>(StringComparer.OrdinalIgnoreCase);
                                foreach (System.Data.DataRow row in dt.Rows)
                                {
                                    var colName = Convert.ToString(row["COLUMN_NAME"]);
                                    if (string.IsNullOrEmpty(colName)) continue;
                                    // DATA_TYPE is an Int16 OLE DB type enum; cast to OleDbType
                                    var typeCode = Convert.ToInt32(row["DATA_TYPE"]);
                                    map[colName] = (OleDbType)typeCode;
                                }
                                colTypeCache[table] = map;
                                return map;
                            };

                            // Helper for robust parameter creation with type coercion
                            Action<OleDbCommand, string, object, string, Dictionary<string, OleDbType>> addParam = (cmd, name, value, col, typeMap) =>
                            {
                                object v = value;
                                OleDbType? t = null;
                                if (typeMap != null && col != null && typeMap.TryGetValue(col, out var mapped)) t = mapped;

                                if (v == null)
                                {
                                    var p = cmd.Parameters.Add(name, t ?? OleDbType.Variant);
                                    p.Value = DBNull.Value;
                                    return;
                                }

                                // Normalize Date/Time values (avoid OADate; use DateTime/DateTimeOffset)
                                if (t.HasValue && (t.Value == OleDbType.DBDate || t.Value == OleDbType.DBTime || t.Value == OleDbType.DBTimeStamp || t.Value == OleDbType.Date))
                                {
                                    if (v is DateTimeOffset dto) v = dto.UtcDateTime;
                                    else if (v is string s)
                                    {
                                        DateTime parsed;
                                        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out parsed)) v = parsed;
                                        else if (DateTime.TryParse(s, System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), System.Globalization.DateTimeStyles.AssumeLocal, out parsed)) v = parsed;
                                        else if (DateTime.TryParse(s, out parsed)) v = parsed;
                                    }
                                }

                                if (t.HasValue)
                                {
                                    var p = cmd.Parameters.Add(name, t.Value);
                                    p.Value = v;
                                }
                                else
                                {
                                    // Fallback: infer from value and coerce
                                    var it = OleDbSchemaHelper.InferOleDbTypeFromValue(v);
                                    var p = new OleDbParameter(name, it) { Value = OleDbSchemaHelper.CoerceValueForOleDb(v, it) };
                                    cmd.Parameters.Add(p);
                                }
                            };

                            // Helper to execute non-query with retry on common Access lock violations
                            Func<OleDbCommand, Task> execWithRetryAsync = async (cmd) =>
                            {
                                _pushStages[countryId] = "ProcessChanges";
                                const int maxRetries = 5;
                                int attempt = 0;
                                while (true)
                                {
                                    try
                                    {
                                        await cmd.ExecuteNonQueryAsync();
                                        return;
                                    }
                                    catch (OleDbException ex)
                                    {
                                        // Access/Jet lock violations often surface with these native error codes
                                        bool isLock = false;
                                        foreach (OleDbError err in ex.Errors)
                                        {
                                            if (err.NativeError == 3218 || err.NativeError == 3260 || err.NativeError == 3188)
                                            {
                                                isLock = true; break;
                                            }
                                        }
                                        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
                                        if (!isLock)
                                        {
                                            if (msg.Contains("locked") || msg.Contains("verrou")) isLock = true;
                                        }

                                        if (isLock && attempt < maxRetries)
                                        {
                                            attempt++;
                                            await Task.Delay(200 * attempt, token);
                                            continue;
                                        }
                                        throw;
                                    }
                                }
                            };

                            foreach (var entry in unsynced)
                            {
                                if (token.IsCancellationRequested) break;
                                if (string.IsNullOrWhiteSpace(entry?.TableName) || string.IsNullOrWhiteSpace(entry?.RecordId)) continue;

                                var table = entry.TableName;
                                var op = (entry.OperationType ?? string.Empty).Trim().ToUpperInvariant();

                                var cols = await getColsAsync(table);
                                if (cols == null || cols.Count == 0) continue;
                                var pkCol = await getPkAsync(table);
                                var typeMap = await getColTypesAsync(table);

                                // 1) Lire la ligne locale (si elle existe)
                                object localPkVal = entry.RecordId;
                                object Prepare(object v) => v ?? DBNull.Value;

                                Dictionary<string, object> localValues = null;
                                using (var lcCmd = new OleDbCommand($"SELECT * FROM [{table}] WHERE [{pkCol}] = @k", localConn))
                                {
                                    var localTypeMap = await OleDbSchemaHelper.GetColumnTypesAsync(localConn, table);
                                    var kType = localTypeMap.TryGetValue(pkCol, out var ktt) ? ktt : OleDbSchemaHelper.InferOleDbTypeFromValue(localPkVal);
                                    var pK = new OleDbParameter("@k", kType) { Value = OleDbSchemaHelper.CoerceValueForOleDb(localPkVal, kType) };
                                    lcCmd.Parameters.Add(pK);
                                    using (var r = await lcCmd.ExecuteReaderAsync())
                                    {
                                        if (await r.ReadAsync())
                                        {
                                            localValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                            for (int i = 0; i < r.FieldCount; i++)
                                            {
                                                var c = r.GetName(i);
                                                if (!cols.Contains(c)) continue; // garder uniquement les colonnes connues côté réseau
                                                localValues[c] = r.IsDBNull(i) ? null : r.GetValue(i);
                                            }
                                        }
                                    }
                                }

                                // 2) Appliquer sur réseau
                                if (op == "DELETE")
                                {
                                    // Soft-delete si possible
                                    bool hasIsDeleted = cols.Contains(_syncConfig.IsDeletedColumn);
                                    bool hasDeleteDate = cols.Contains("DeleteDate");
                                    bool hasLastMod = cols.Contains(_syncConfig.LastModifiedColumn) || cols.Contains("LastModified");

                                    if (hasIsDeleted || hasDeleteDate)
                                    {
                                        var setParts = new List<string>();
                                        var paramCols = new List<string>();
                                        var parameters = new List<object>();
                                        if (hasIsDeleted) setParts.Add($"[{_syncConfig.IsDeletedColumn}] = true");
                                        if (hasDeleteDate) { setParts.Add("[DeleteDate] = @p0"); parameters.Add(DateTime.UtcNow); paramCols.Add("DeleteDate"); }
                                        if (hasLastMod)
                                        {
                                            var col = cols.Contains(_syncConfig.LastModifiedColumn) ? _syncConfig.LastModifiedColumn : "LastModified";
                                            setParts.Add($"[{col}] = @p1"); parameters.Add(DateTime.UtcNow); paramCols.Add(col);
                                        }
                                        using (var cmd = new OleDbCommand($"UPDATE [{table}] SET {string.Join(", ", setParts)} WHERE [{pkCol}] = @key", netConn, tx))
                                        {
                                            for (int i = 0; i < parameters.Count; i++) addParam(cmd, $"@p{i}", parameters[i], i < paramCols.Count ? paramCols[i] : null, typeMap);
                                            addParam(cmd, "@key", localPkVal, pkCol, typeMap);
                                            await execWithRetryAsync(cmd);
                                        }
                                    }
                                    else
                                    {
                                        using (var cmd = new OleDbCommand($"DELETE FROM [{table}] WHERE [{pkCol}] = @key", netConn, tx))
                                        {
                                            addParam(cmd, "@key", localPkVal, pkCol, typeMap);
                                            await execWithRetryAsync(cmd);
                                        }
                                    }
                                    appliedIds.Add(entry.Id);
                                }
                                else // INSERT / UPDATE
                                {
                                    if (localValues == null)
                                    {
                                        // Rien à appliquer pour cette entrée (probablement créée ailleurs) -> ignorer
                                        continue;
                                    }

                                    // Existence sur réseau
                                    int exists;
                                    using (var exCmd = new OleDbCommand($"SELECT COUNT(*) FROM [{table}] WHERE [{pkCol}] = @key", netConn, tx))
                                    {
                                        addParam(exCmd, "@key", localPkVal, pkCol, typeMap);
                                        exists = Convert.ToInt32(await exCmd.ExecuteScalarAsync());
                                    }

                                    // Préparer listes colonnes/valeurs (exclure PK en update)
                                    var allCols = localValues.Keys.Where(c => !string.Equals(c, pkCol, StringComparison.OrdinalIgnoreCase)).ToList();

                                    if (exists > 0)
                                    {
                                        // UPDATE
                                        var setParts = new List<string>();
                                        // If the table has a Version column, handle it via increment expression only (case-insensitive)
                                        bool hasVersionCol = cols != null && cols.Any(n => string.Equals(n, "Version", StringComparison.OrdinalIgnoreCase));
                                        var effectiveCols = hasVersionCol
                                            ? allCols.Where(c => !string.Equals(c, "Version", StringComparison.OrdinalIgnoreCase)).ToList()
                                            : allCols;
                                        for (int i = 0; i < effectiveCols.Count; i++) setParts.Add($"[{effectiveCols[i]}] = @p{i}");
                                        if (hasVersionCol)
                                        {
                                            setParts.Add("[Version] = [Version] + 1");
                                        }

                                        // Optimistic concurrency: if Version column exists, add WHERE Version = @localVer
                                        // to detect if another user modified the row since our last pull.
                                        string whereClause = $"[{pkCol}] = @key";
                                        object localVersionVal = null;
                                        if (hasVersionCol && localValues.ContainsKey("Version") && localValues["Version"] != null && localValues["Version"] != DBNull.Value)
                                        {
                                            whereClause += " AND [Version] = @localVer";
                                            localVersionVal = localValues["Version"];
                                        }

                                        using (var up = new OleDbCommand($"UPDATE [{table}] SET {string.Join(", ", setParts)} WHERE {whereClause}", netConn, tx))
                                        {
                                            for (int i = 0; i < effectiveCols.Count; i++) addParam(up, $"@p{i}", localValues[effectiveCols[i]], effectiveCols[i], typeMap);
                                            addParam(up, "@key", localPkVal, pkCol, typeMap);
                                            if (localVersionVal != null)
                                                addParam(up, "@localVer", localVersionVal, "Version", typeMap);

                                            // Execute with retry, but check rows affected for conflict detection
                                            int rowsAffected = 0;
                                            const int maxRetries = 5;
                                            int retryAttempt = 0;
                                            while (true)
                                            {
                                                try
                                                {
                                                    rowsAffected = await up.ExecuteNonQueryAsync();
                                                    break;
                                                }
                                                catch (OleDbException ex)
                                                {
                                                    bool isLock = false;
                                                    foreach (OleDbError err in ex.Errors)
                                                    {
                                                        if (err.NativeError == 3218 || err.NativeError == 3260 || err.NativeError == 3188) { isLock = true; break; }
                                                    }
                                                    if (!isLock) { var m = ex.Message?.ToLowerInvariant() ?? ""; if (m.Contains("locked") || m.Contains("verrou")) isLock = true; }
                                                    if (isLock && retryAttempt < maxRetries) { retryAttempt++; await Task.Delay(200 * retryAttempt, token); continue; }
                                                    throw;
                                                }
                                            }

                                            if (rowsAffected == 0 && localVersionVal != null)
                                            {
                                                // Conflict: another user modified this row since our last pull.
                                                // Skip — the next pull will bring the latest state from network.
                                                System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] CONFLICT on {table} PK={localPkVal} (localVer={localVersionVal}). Skipping — will resolve on next pull.");
                                                try { LogManager.Warning($"[PUSH][{countryId}] Conflict detected on {table} PK={localPkVal}. Row skipped."); } catch { }
                                                continue; // skip appliedIds.Add — ChangeLog entry stays unsynced for retry after pull
                                            }
                                        }

                                        // Mirror Version increment locally (best-effort) so local stays aligned without a pull
                                        if (hasVersionCol)
                                        {
                                            try
                                            {
                                                using (var lup = new OleDbCommand($"UPDATE [{table}] SET [Version] = [Version] + 1 WHERE [{pkCol}] = @key", localConn))
                                                {
                                                    var localTypes = await OleDbSchemaHelper.GetColumnTypesAsync(localConn, table);
                                                    addParam(lup, "@key", localPkVal, pkCol, localTypes);
                                                    await lup.ExecuteNonQueryAsync();
                                                }
                                            }
                                            catch { /* keep push resilient */ }
                                        }
                                    }
                                    else
                                    {
                                        // INSERT
                                        var insertCols = localValues.Keys.ToList();
                                        // If table has Version column and it's missing/null in localValues, set to 1 on insert
                                        bool hasVersionCol = cols.Contains("Version");
                                        if (hasVersionCol)
                                        {
                                            var lvHasVersion = localValues.ContainsKey("Version") && localValues["Version"] != null && localValues["Version"] != DBNull.Value;
                                            if (!lvHasVersion && !insertCols.Contains("Version", StringComparer.OrdinalIgnoreCase))
                                            {
                                                insertCols.Add("Version");
                                            }
                                        }
                                        var ph = string.Join(", ", insertCols.Select((c, i) => $"@p{i}"));
                                        var colList = string.Join(", ", insertCols.Select(c => $"[{c}]"));
                                        using (var ins = new OleDbCommand($"INSERT INTO [{table}] ({colList}) VALUES ({ph})", netConn, tx))
                                        {
                                            for (int i = 0; i < insertCols.Count; i++)
                                            {
                                                var colName = insertCols[i];
                                                object val;
                                                if (string.Equals(colName, "Version", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    var lvHasVersion = localValues.ContainsKey("Version") && localValues["Version"] != null && localValues["Version"] != DBNull.Value;
                                                    val = lvHasVersion ? localValues["Version"] : (object)1;
                                                }
                                                else
                                                {
                                                    val = localValues.ContainsKey(colName) ? localValues[colName] : DBNull.Value;
                                                }
                                                addParam(ins, $"@p{i}", val, colName, typeMap);
                                            }
                                            await execWithRetryAsync(ins);
                                        }

                                        // If Version was added/set to 1 on network insert, mirror locally when missing
                                        if (hasVersionCol)
                                        {
                                            try
                                            {
                                                using (var lins = new OleDbCommand($"UPDATE [{table}] SET [Version] = [Version] + IIF([Version] <= 0, 1, 0) WHERE [{pkCol}] = @key", localConn))
                                                {
                                                    var localTypes = await OleDbSchemaHelper.GetColumnTypesAsync(localConn, table);
                                                    addParam(lins, "@key", localPkVal, pkCol, localTypes);
                                                    await lins.ExecuteNonQueryAsync();
                                                }
                                            }
                                            catch { /* best-effort */ }
                                        }
                                    }

                                    appliedIds.Add(entry.Id);
                                }
                            }

                            tx.Commit();
                        }
                        catch
                        {
                            try { tx.Rollback(); } catch { }
                            if (diag)
                            {
                                System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Transaction failed. Rolling back.");
                                try { LogManager.Info($"[PUSH][{countryId}] Transaction failed. Rolling back"); } catch { }
                            }
                            throw;
                        }
                    }
                }

                // Marquer uniquement les id appliqués
                if (appliedIds.Count > 0)
                {
                    await tracker.MarkChangesAsSyncedAsync(appliedIds);
                    if (diag)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Marked {appliedIds.Count} ChangeLog entries as synced.");
                        try { LogManager.Info($"[PUSH][{countryId}] Marked {appliedIds.Count} entries as synced"); } catch { }
                    }
                }

                // Notify completion as UpToDate
                try { await RaiseSyncStateAsync(countryId, SyncStateKind.UpToDate, pendingOverride: 0); } catch { }

                if (diag)
                {
                    System.Diagnostics.Debug.WriteLine($"[PUSH][{countryId}] Push core completed. Applied={appliedIds.Count}");
                    try { LogManager.Info($"[PUSH][{countryId}] Push core completed. Applied={appliedIds.Count}"); } catch { }
                }
                return appliedIds.Count;
            }
            finally
            {
                try { globalLock?.Dispose(); } catch { }
                try { watchdogCts.Cancel(); } catch { }
                try { watchdogCts.Dispose(); } catch { }
                try { if (_pushRunIds.TryGetValue(countryId, out var id) && id == runId) _pushRunIds.TryRemove(countryId, out _); } catch { }
            }
        }
    }
}
