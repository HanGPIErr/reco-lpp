using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;

namespace RecoTool.Services
{
    // Partial: global cross-process lock surface backed by the SyncLocks table in the remote
    // control/lock database. Provides acquire/release semantics with heartbeat-based expiry,
    // re-entrancy detection, force-break, and status reporting (SyncStatus column).
    public partial class OfflineFirstService
    {
        // In-process gate: ensure only one AcquireGlobalLockAsync is executing per process at any time.
        private static readonly SemaphoreSlim _acquireGlobalProcessGate = new SemaphoreSlim(1, 1);
        // Re-entrancy flag: 1 = a lock is currently held in-process, 0 = free.
        // Checked BEFORE the gate to allow nested calls to succeed immediately.
        private static int _processLockHeld;

        /// <summary>
        /// Returns true if a global lock is currently active AND held by another process (not this MachineName+ProcessId).
        /// Ignores expired rows and performs a best-effort cleanup of expired entries.
        /// </summary>
        public async Task<bool> IsGlobalLockActiveByOthersAsync(CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(_currentCountryId))
                return false;

            try
            {
                string connStr = GetRemoteLockConnectionString(_currentCountryId);
                var now = _clock.UtcNow;
                using (var connection = new OleDbConnection(connStr))
                {
                    await connection.OpenAsync(token);
                    await EnsureSyncLocksTableExistsAsync(connection);

                    // Cleanup expired locks
                    using (var cleanup = new OleDbCommand("DELETE FROM SyncLocks WHERE ExpiresAt IS NOT NULL AND ExpiresAt < ?", connection))
                    {
                        cleanup.Parameters.Add(new OleDbParameter("@ExpiresAt", OleDbType.Date) { Value = now });
                        await cleanup.ExecuteNonQueryAsync();
                    }

                    // Check if an active lock exists held by OTHER processes
                    using (var check = new OleDbCommand("SELECT COUNT(*) FROM SyncLocks WHERE (ExpiresAt IS NULL OR ExpiresAt > ?) AND NOT (MachineName = ? AND ProcessId = ?)", connection))
                    {
                        check.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                        check.Parameters.Add(new OleDbParameter("@MachineName", OleDbType.VarWChar, 255) { Value = (object)Environment.MachineName ?? DBNull.Value });
                        check.Parameters.Add(new OleDbParameter("@ProcessId", OleDbType.Integer) { Value = System.Diagnostics.Process.GetCurrentProcess().Id });
                        var countObj = await check.ExecuteScalarAsync();
                        int active = 0;
                        if (countObj != null && countObj != DBNull.Value)
                            active = Convert.ToInt32(countObj);
                        return active > 0;
                    }
                }
            }
            catch
            {
                // On error, do not block caller: assume no foreign lock
                return false;
            }
        }

        // No-op handle used when a re-entrant acquisition detects an active lock already held by this process.
        private sealed class NoopLockHandle : IDisposable
        {
            public static readonly NoopLockHandle Instance = new NoopLockHandle();
            private NoopLockHandle() { }
            public void Dispose() { /* nothing */ }
        }

        private sealed class GlobalLockHandle : IDisposable
        {
            private readonly string _connStr;
            private readonly string _lockId;
            private readonly int _expirySeconds;
            private readonly RecoTool.Infrastructure.Time.IClock _clock;
            private bool _released;
            private System.Timers.Timer _heartbeat;
            private int _hbRunning; // 0 = idle, 1 = executing

            public GlobalLockHandle(string connStr, string lockId, int expirySeconds, RecoTool.Infrastructure.Time.IClock clock)
            {
                _connStr = connStr;
                _lockId = lockId;
                _expirySeconds = Math.Max(30, expirySeconds);
                _clock = clock ?? RecoTool.Infrastructure.Time.SystemClock.Instance;
                StartHeartbeat();
            }

            private void StartHeartbeat()
            {
                try
                {
                    // Renew at half the expiry, bounded between 15s and 120s
                    int periodSec = Math.Max(15, Math.Min(120, _expirySeconds / 2));
                    _heartbeat = new System.Timers.Timer(periodSec * 1000);
                    _heartbeat.AutoReset = true;
                    _heartbeat.Elapsed += (s, e) =>
                    {
                        // Prevent overlapping ticks or post-release renewals
                        if (Volatile.Read(ref _released)) return;
                        if (Interlocked.Exchange(ref _hbRunning, 1) == 1) return;
                        try
                        {
                            var newExpiry = _clock.UtcNow.AddSeconds(_expirySeconds);
                            using (var conn = new OleDbConnection(_connStr))
                            {
                                conn.Open();
                                using (var cmd = new OleDbCommand("UPDATE SyncLocks SET ExpiresAt = ? WHERE LockID = ?", conn))
                                {
                                    var pExpires = new OleDbParameter("@ExpiresAt", OleDbType.Date) { Value = newExpiry };
                                    var pLock = new OleDbParameter("@LockID", OleDbType.VarWChar, 36) { Value = (object)_lockId ?? DBNull.Value };
                                    cmd.Parameters.Add(pExpires);
                                    cmd.Parameters.Add(pLock);
                                    var corr = Guid.NewGuid();
                                    LogOleDbCommand("GlobalLockHandle.Heartbeat", cmd, corr);
                                    cmd.ExecuteNonQuery();
                                }
                            }
                        }
                        catch { /* best-effort */ }
                        finally { Interlocked.Exchange(ref _hbRunning, 0); }
                    };
                    _heartbeat.Start();
                }
                catch { /* best-effort */ }
            }

            public void Dispose()
            {
                if (Volatile.Read(ref _released)) return;
                try
                {
                    try { _heartbeat?.Stop(); _heartbeat?.Dispose(); } catch { }
                    using (var conn = new OleDbConnection(_connStr))
                    {
                        conn.Open();
                        using (var cmd = new OleDbCommand("DELETE FROM SyncLocks WHERE LockID = ?", conn))
                        {
                            var pLock = new OleDbParameter("@LockID", OleDbType.VarWChar, 36) { Value = (object)_lockId ?? DBNull.Value };
                            cmd.Parameters.Add(pLock);
                            var corr = Guid.NewGuid();
                            LogOleDbCommand("GlobalLockHandle.Dispose", cmd, corr);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
                catch { /* best-effort */ }
                finally { Volatile.Write(ref _released, true); }
            }
        }

        // Wrapper that ensures our in-process gate is released when the underlying DB lock is disposed
        private sealed class ProcessGateLockHandle : IDisposable
        {
            private readonly IDisposable _inner;
            private readonly SemaphoreSlim _gate;
            private readonly Action _onRelease;
            private int _released;

            public ProcessGateLockHandle(IDisposable inner, SemaphoreSlim gate, Action onRelease = null)
            {
                _inner = inner;
                _gate = gate;
                _onRelease = onRelease;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 1) return;
                try { _onRelease?.Invoke(); } catch { }
                try { _inner?.Dispose(); } catch { }
                try { _gate?.Release(); } catch { }
            }
        }

        // Lightweight diagnostic helper for OleDb commands
        private static void LogOleDbCommand(string where, OleDbCommand cmd, Guid correlationId)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[{correlationId}] {where} SQL: {cmd?.CommandText}");
                if (cmd != null)
                {
                    foreach (OleDbParameter p in cmd.Parameters)
                    {
                        var v = p?.Value;
                        var vs = v == null ? "<null>" : (v == DBNull.Value ? "<DBNULL>" : v.ToString());
                        sb.AppendLine($"[{correlationId}]   {p?.ParameterName} ({p?.OleDbType}): {vs}");
                    }
                }
                System.Diagnostics.Debug.WriteLine(sb.ToString());
            }
            catch { /* logging must never throw */ }
        }

        private async Task<IDisposable> AcquireGlobalLockInternalAsync(string identifier, string reason, int timeoutSeconds, CancellationToken token)
        {
            if (string.IsNullOrEmpty(_currentCountryId))
                throw new InvalidOperationException("Aucun pays courant n'est initialisé");

            // Two semantics:
            // - Wait budget: we will wait up to timeoutSeconds to acquire the lock (0 => no wait, fail fast)
            // - Expiry: to avoid deadlocks, we always set an expiration; if timeoutSeconds==0, default to 3 minutes
            int waitBudgetSeconds = Math.Max(0, timeoutSeconds);
            int expirySeconds = timeoutSeconds > 0 ? timeoutSeconds : 180; // 3 min safety default

            string connStr = GetRemoteLockConnectionString(_currentCountryId);
            DateTime deadline = _clock.UtcNow.AddSeconds(waitBudgetSeconds);
            Exception lastError = null;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                string lockId = Guid.NewGuid().ToString();
                var now = _clock.UtcNow;
                var expiresAt = now.AddSeconds(expirySeconds);

                try
                {
                    await _lockDbGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                    using (var connection = new OleDbConnection(connStr))
                    {
                        await connection.OpenAsync(token);
                        await EnsureSyncLocksTableExistsAsync(connection);

                        // Cleanup expired locks
                        using (var cleanup = new OleDbCommand("DELETE FROM SyncLocks WHERE ExpiresAt IS NOT NULL AND ExpiresAt < ?", connection))
                        {
                            cleanup.Parameters.Add(new OleDbParameter("@ExpiresAt", OleDbType.Date) { Value = now });
                            await cleanup.ExecuteNonQueryAsync();
                        }

                        // Purge stale self-locks: same machine, process no longer alive
                        try
                        {
                            using (var selectSelf = new OleDbCommand("SELECT LockID, ProcessId FROM SyncLocks WHERE (ExpiresAt IS NULL OR ExpiresAt > ?) AND MachineName = ?", connection))
                            {
                                selectSelf.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                                selectSelf.Parameters.Add(new OleDbParameter("@MachineName", OleDbType.VarWChar, 255) { Value = (object)Environment.MachineName ?? DBNull.Value });
                                using (var reader = await selectSelf.ExecuteReaderAsync())
                                {
                                    var staleIds = new List<string>();
                                    while (await reader.ReadAsync())
                                    {
                                        string id = reader["LockID"]?.ToString();
                                        int pid = 0;
                                        try { pid = Convert.ToInt32(reader["ProcessId"]); } catch { pid = 0; }
                                        bool alive = false;
                                        if (pid > 0)
                                        {
                                            try { var p = System.Diagnostics.Process.GetProcessById(pid); alive = (p != null && !p.HasExited); } catch { alive = false; }
                                        }
                                        if (!alive && !string.IsNullOrEmpty(id)) staleIds.Add(id);
                                    }
                                    reader.Close();

                                    foreach (var id in staleIds)
                                    {
                                        using (var del = new OleDbCommand("DELETE FROM SyncLocks WHERE LockID = ?", connection))
                                        {
                                            del.Parameters.Add(new OleDbParameter("@LockID", OleDbType.VarWChar, 36) { Value = (object)id ?? DBNull.Value });
                                            await del.ExecuteNonQueryAsync();
                                        }
                                    }
                                }
                            }
                        }
                        catch { /* best-effort cleanup */ }

                        // Check if a global lock is already held
                        using (var check = new OleDbCommand("SELECT COUNT(*) FROM SyncLocks WHERE ExpiresAt IS NULL OR ExpiresAt > ?", connection))
                        {
                            check.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                            var countObj = await check.ExecuteScalarAsync();
                            int active = 0;
                            if (countObj != null && countObj != DBNull.Value)
                                active = Convert.ToInt32(countObj);

                            if (active == 0)
                            {
                                // Try to acquire the lock
                                using (var command = new OleDbCommand("INSERT INTO SyncLocks (LockID, Reason, CreatedAt, ExpiresAt, MachineName, ProcessId) VALUES (?, ?, ?, ?, ?, ?)", connection))
                                {
                                    command.Parameters.Add(new OleDbParameter("@LockID", OleDbType.VarWChar, 36) { Value = lockId });
                                    command.Parameters.Add(new OleDbParameter("@Reason", OleDbType.VarWChar, 255) { Value = (object)(reason ?? "Global") ?? DBNull.Value });
                                    command.Parameters.Add(new OleDbParameter("@CreatedAt", OleDbType.Date) { Value = now });
                                    command.Parameters.Add(new OleDbParameter("@ExpiresAt", OleDbType.Date) { Value = expiresAt });
                                    command.Parameters.Add(new OleDbParameter("@MachineName", OleDbType.VarWChar, 255) { Value = (object)Environment.MachineName ?? DBNull.Value });
                                    command.Parameters.Add(new OleDbParameter("@ProcessId", OleDbType.Integer) { Value = System.Diagnostics.Process.GetCurrentProcess().Id });

                                    await command.ExecuteNonQueryAsync();
                                }

                                // Best-effort initial status
                                try
                                {
                                    using (var set = new OleDbCommand("UPDATE SyncLocks SET SyncStatus = ? WHERE LockID = ?", connection))
                                    {
                                        set.Parameters.Add(new OleDbParameter("@SyncStatus", OleDbType.VarWChar, 50) { Value = "Acquired" });
                                        set.Parameters.Add(new OleDbParameter("@LockID", OleDbType.VarWChar, 36) { Value = lockId });
                                        await set.ExecuteNonQueryAsync();
                                    }
                                }
                                catch { /* column may not exist yet */ }

                                return new GlobalLockHandle(connStr, lockId, expirySeconds, _clock);
                            }
                            else
                            {
                                // Re-entrancy: if the active lock is ours (same process), allow nested acquisition without waiting
                                using (var self = new OleDbCommand("SELECT TOP 1 LockID FROM SyncLocks WHERE (ExpiresAt IS NULL OR ExpiresAt > ?) AND MachineName = ? AND ProcessId = ? ORDER BY CreatedAt DESC", connection))
                                {
                                    self.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                                    self.Parameters.Add(new OleDbParameter("@MachineName", OleDbType.VarWChar, 255) { Value = (object)Environment.MachineName ?? DBNull.Value });
                                    self.Parameters.Add(new OleDbParameter("@ProcessId", OleDbType.Integer) { Value = System.Diagnostics.Process.GetCurrentProcess().Id });
                                    var obj = await self.ExecuteScalarAsync();
                                    if (obj != null && obj != DBNull.Value)
                                    {
                                        // Return a no-op handle; original holder will release the DB row
                                        return NoopLockHandle.Instance;
                                    }
                                }
                            }
                        }
                    }
                    }
                    finally
                    {
                        _lockDbGate.Release();
                    }

                    // If we reach here, a lock is already held (retry delay OUTSIDE semaphore)
                    if (waitBudgetSeconds == 0 || _clock.UtcNow >= deadline)
                        throw new TimeoutException("Impossible d'acquérir le verrou global dans le délai imparti.");

                    await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Could be race condition on insert or transient DB issue; honor wait budget
                    lastError = ex;
                    if (waitBudgetSeconds == 0 || _clock.UtcNow >= deadline)
                        throw new InvalidOperationException("Echec de l'acquisition du verrou global.", lastError);

                    await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                    continue;
                }
            }
        }

        /// <summary>
        /// Force-breaks ALL global locks in the SyncLocks table for the current country.
        /// Also resets the in-process gate if it was stuck.
        /// Should only be called after user confirmation (e.g. import blocked > 30 min).
        /// </summary>
        public async Task ForceBreakGlobalLockAsync(CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(_currentCountryId))
                return;

            LogManager.Warning("[LOCK] ForceBreakGlobalLockAsync invoked by user.");

            // 1. Delete ALL rows in SyncLocks for this country (DB-level)
            try
            {
                string connStr = GetRemoteLockConnectionString(_currentCountryId);
                using (var conn = new OleDbConnection(connStr))
                {
                    await conn.OpenAsync(token);
                    using (var cmd = new OleDbCommand("DELETE FROM SyncLocks", conn))
                    {
                        int deleted = await cmd.ExecuteNonQueryAsync();
                        LogManager.Warning($"[LOCK] Force-deleted {deleted} SyncLocks row(s).");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"[LOCK] Error clearing SyncLocks table: {ex.Message}");
            }

            // 2. Reset the in-process gate if it's stuck (count == 0 means it's held)
            Volatile.Write(ref _processLockHeld, 0);
            try
            {
                // If the gate is currently at 0 (held), release it so new acquisitions can proceed.
                // If already at 1 (free), Release() would throw or go to 2 — catch silently.
                if (_acquireGlobalProcessGate.CurrentCount == 0)
                    _acquireGlobalProcessGate.Release();
            }
            catch { /* already free */ }

            LogManager.Warning("[LOCK] Force-break completed. Import can be retried.");
        }

        /// <summary>
        /// Vérifie si un verrou global est actuellement actif pour le pays courant.
        /// Utilisé par l'IHM pour éviter des opérations réseau concurrentes (ex: import) lors d'une synchronisation.
        /// </summary>
        public async Task<bool> IsGlobalLockActiveAsync(CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(_currentCountryId))
                return false;

            // Serialize access to the lock DB to prevent concurrent OleDb opens ("file in use")
            await _lockDbGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string connStr = GetRemoteLockConnectionString(_currentCountryId);
                var now = _clock.UtcNow;
                using (var connection = new OleDbConnection(connStr))
                {
                    await connection.OpenAsync(token);
                    await EnsureSyncLocksTableExistsAsync(connection);

                    // Nettoyer les verrous expirés puis vérifier l'existence d'un verrou actif
                    using (var cleanup = new OleDbCommand("DELETE FROM SyncLocks WHERE ExpiresAt IS NOT NULL AND ExpiresAt < ?", connection))
                    {
                        cleanup.Parameters.Add(new OleDbParameter("@ExpiresAt", OleDbType.Date) { Value = now });
                        await cleanup.ExecuteNonQueryAsync();
                    }

                    using (var check = new OleDbCommand("SELECT COUNT(*) FROM SyncLocks WHERE ExpiresAt IS NULL OR ExpiresAt > ?", connection))
                    {
                        check.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                        var countObj = await check.ExecuteScalarAsync();
                        int active = 0;
                        if (countObj != null && countObj != DBNull.Value)
                            active = Convert.ToInt32(countObj);
                        return active > 0;
                    }
                }
            }
            catch
            {
                // En cas d'erreur d'accès réseau, considérer qu'aucun verrou ne bloque l'IHM
                return false;
            }
            finally
            {
                _lockDbGate.Release();
            }
        }

        /// <summary>
        /// Attend (polling) jusqu'à la libération d'un verrou global ou expiration du délai.
        /// </summary>
        public async Task<bool> WaitForGlobalLockReleaseAsync(TimeSpan pollInterval, TimeSpan timeout, CancellationToken token = default)
        {
            var deadline = _clock.UtcNow.Add(timeout);
            while (_clock.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                var locked = await IsGlobalLockActiveAsync(token);
                if (!locked) return true;
                await Task.Delay(pollInterval, token);
            }
            return false;
        }

        /// <summary>
        /// Met à jour le champ SyncStatus pour le verrou global actif détenu par ce processus.
        /// </summary>
        public async Task SetSyncStatusAsync(string status, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(_currentCountryId)) return;
            await _lockDbGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string connStr = GetRemoteLockConnectionString(_currentCountryId);
                var now = _clock.UtcNow;
                using (var connection = new OleDbConnection(connStr))
                {
                    await connection.OpenAsync(token);
                    await EnsureSyncLocksTableExistsAsync(connection);
                    using (var cmd = new OleDbCommand("UPDATE SyncLocks SET SyncStatus = ? WHERE MachineName = ? AND ProcessId = ? AND (ExpiresAt IS NULL OR ExpiresAt > ?)", connection))
                    {
                        cmd.Parameters.Add(new OleDbParameter("@SyncStatus", OleDbType.VarWChar, 50) { Value = (object)(status ?? "Unknown") ?? DBNull.Value });
                        cmd.Parameters.Add(new OleDbParameter("@MachineName", OleDbType.VarWChar, 255) { Value = (object)Environment.MachineName ?? DBNull.Value });
                        cmd.Parameters.Add(new OleDbParameter("@ProcessId", OleDbType.Integer) { Value = System.Diagnostics.Process.GetCurrentProcess().Id });
                        cmd.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                        await cmd.ExecuteNonQueryAsync();
                    }
                }
            }
            catch { /* best-effort */ }
            finally
            {
                _lockDbGate.Release();
            }
        }

        /// <summary>
        /// Récupère le SyncStatus courant pour le verrou global actif de ce processus, s'il existe.
        /// </summary>
        public async Task<string> GetCurrentSyncStatusAsync(CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(_currentCountryId)) return null;
            await _lockDbGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                string connStr = GetRemoteLockConnectionString(_currentCountryId);
                var now = _clock.UtcNow;
                using (var connection = new OleDbConnection(connStr))
                {
                    await connection.OpenAsync(token);
                    await EnsureSyncLocksTableExistsAsync(connection);
                    using (var cmd = new OleDbCommand("SELECT TOP 1 SyncStatus FROM SyncLocks WHERE MachineName = ? AND ProcessId = ? AND (ExpiresAt IS NULL OR ExpiresAt > ?) ORDER BY CreatedAt DESC", connection))
                    {
                        cmd.Parameters.Add(new OleDbParameter("@MachineName", OleDbType.VarWChar, 255) { Value = (object)Environment.MachineName ?? DBNull.Value });
                        cmd.Parameters.Add(new OleDbParameter("@ProcessId", OleDbType.Integer) { Value = System.Diagnostics.Process.GetCurrentProcess().Id });
                        cmd.Parameters.Add(new OleDbParameter("@Now", OleDbType.Date) { Value = now });
                        var obj = await cmd.ExecuteScalarAsync();
                        return obj == null || obj == DBNull.Value ? null : obj.ToString();
                    }
                }
            }
            catch { return null; }
            finally
            {
                _lockDbGate.Release();
            }
        }

        /// <summary>
        /// Acquiert un verrou global pour empêcher la synchronisation pendant des opérations critiques (TimeSpan overload).
        /// </summary>
        public async Task<IDisposable> AcquireGlobalLockAsync(string identifier, string reason, TimeSpan timeout, CancellationToken token = default)
        {
            int timeoutSeconds = (int)Math.Max(0, timeout.TotalSeconds);

            // ── Re-entrancy: if this process already holds the lock, return a no-op ──
            if (Volatile.Read(ref _processLockHeld) == 1)
            {
                LogManager.Info($"[LOCK] Re-entrant acquisition detected for '{reason}' – returning NoopLockHandle.");
                return NoopLockHandle.Instance;
            }

            // ── Acquire the in-process gate WITH a timeout to avoid infinite blocking ──
            int gateTimeoutMs = Math.Max(5_000, timeoutSeconds * 1000);
            if (!await _acquireGlobalProcessGate.WaitAsync(gateTimeoutMs, token).ConfigureAwait(false))
            {
                throw new TimeoutException(
                    $"Impossible d'acquérir le verrou interne (gate) après {gateTimeoutMs / 1000}s. " +
                    "Un import ou une synchronisation est probablement toujours en cours.");
            }

            try
            {
                var inner = await AcquireGlobalLockInternalAsync(identifier, reason, timeoutSeconds, token);
                Volatile.Write(ref _processLockHeld, 1);
                return new ProcessGateLockHandle(inner, _acquireGlobalProcessGate,
                    onRelease: () => Volatile.Write(ref _processLockHeld, 0));
            }
            catch
            {
                try { _acquireGlobalProcessGate.Release(); } catch { }
                throw;
            }
        }
    }
}
