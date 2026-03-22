using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;
using RecoTool.Configuration;

namespace RecoTool.Services
{
    /// <summary>
    /// Lightweight tracker for active user sessions on TodoList items.
    /// Registers/unregisters sessions and provides simple heartbeat to keep them alive.
    /// No cache, no persistent connection — every read hits the DB for accuracy.
    /// </summary>
    public class TodoListSessionTracker : IDisposable
    {
        private readonly string _lockDbConnectionString;
        private readonly string _currentUserId;
        private readonly OfflineFirstService _offlineFirstService;
        private readonly Timer _heartbeatTimer;
        private readonly HashSet<int> _trackedTodoIds = new HashSet<int>();
        private readonly object _lock = new object();
        private bool _disposed;
        private int _heartbeatRunning; // Re-entrancy guard: 0=idle, 1=executing

        private const int HEARTBEAT_INTERVAL_MS = 60_000; // Write heartbeat every 60 seconds
        private const int SESSION_TIMEOUT_SECONDS = 180;   // Session dead after 3 min without heartbeat

        public TodoListSessionTracker(string lockDbConnectionString, string currentUserId, OfflineFirstService offlineFirstService = null)
        {
            if (string.IsNullOrWhiteSpace(lockDbConnectionString))
                throw new ArgumentException("Connection string is required", nameof(lockDbConnectionString));
            if (string.IsNullOrWhiteSpace(currentUserId))
                throw new ArgumentException("User ID is required", nameof(currentUserId));

            _lockDbConnectionString = lockDbConnectionString;
            _currentUserId = currentUserId;
            _offlineFirstService = offlineFirstService;

            if (FeatureFlags.ENABLE_MULTI_USER)
            {
                _heartbeatTimer = new Timer(HeartbeatCallback, null, HEARTBEAT_INTERVAL_MS, HEARTBEAT_INTERVAL_MS);
            }
        }

        public async Task EnsureTableAsync()
        {
            const string table = "T_TodoList_Sessions";
            using (var conn = new OleDbConnection(_lockDbConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                try
                {
                    var schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, table, "TABLE" });
                    if (schema == null || schema.Rows.Count == 0)
                    {
                        var createSql = $@"
                            CREATE TABLE {table} (
                                [SessionId] AUTOINCREMENT PRIMARY KEY,
                                [TodoId] LONG NOT NULL,
                                [UserId] TEXT(255) NOT NULL,
                                [UserName] TEXT(255),
                                [SessionStart] DATETIME NOT NULL,
                                [LastHeartbeat] DATETIME NOT NULL,
                                [IsEditing] BIT NOT NULL DEFAULT 0
                            )";
                        using (var cmd = new OleDbCommand(createSql, conn))
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                        try
                        {
                            using (var cmd = new OleDbCommand($"CREATE INDEX IX_TodoSessions_Todo ON {table}([TodoId])", conn))
                                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                catch { /* Table might already exist */ }
            }
        }

        /// <summary>
        /// Registers that the current user is viewing a TodoList item.
        /// </summary>
        public async Task<bool> RegisterViewingAsync(int todoId, string userName = null, bool isEditing = false)
        {
            if (!FeatureFlags.ENABLE_MULTI_USER) return true;
            try
            {
                lock (_lock) { _trackedTodoIds.Add(todoId); }

                using (var conn = new OleDbConnection(_lockDbConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);

                    // Upsert: check then insert/update
                    var checkSql = "SELECT SessionId FROM T_TodoList_Sessions WHERE TodoId = ? AND UserId = ?";
                    using (var checkCmd = new OleDbCommand(checkSql, conn))
                    {
                        checkCmd.Parameters.AddWithValue("@TodoId", todoId);
                        checkCmd.Parameters.AddWithValue("@UserId", _currentUserId);
                        var existing = await checkCmd.ExecuteScalarAsync().ConfigureAwait(false);

                        if (existing != null)
                        {
                            var updateSql = "UPDATE T_TodoList_Sessions SET LastHeartbeat = ? WHERE SessionId = ?";
                            using (var cmd = new OleDbCommand(updateSql, conn))
                            {
                                cmd.Parameters.Add("@LH", OleDbType.Date).Value = DateTime.Now;
                                cmd.Parameters.AddWithValue("@SID", existing);
                                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            var insertSql = @"INSERT INTO T_TodoList_Sessions 
                                (TodoId, UserId, UserName, SessionStart, LastHeartbeat, IsEditing) 
                                VALUES (?, ?, ?, ?, ?, ?)";
                            using (var cmd = new OleDbCommand(insertSql, conn))
                            {
                                cmd.Parameters.AddWithValue("@TodoId", todoId);
                                cmd.Parameters.AddWithValue("@UserId", _currentUserId);
                                cmd.Parameters.AddWithValue("@UserName", userName ?? _currentUserId);
                                cmd.Parameters.Add("@SS", OleDbType.Date).Value = DateTime.Now;
                                cmd.Parameters.Add("@LH", OleDbType.Date).Value = DateTime.Now;
                                cmd.Parameters.AddWithValue("@IE", false);
                                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[SessionTracker] Registered TodoId={todoId} User={_currentUserId}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionTracker.Register] ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unregisters the current user from a TodoList item.
        /// </summary>
        public async Task UnregisterViewingAsync(int todoId)
        {
            if (!FeatureFlags.ENABLE_MULTI_USER) return;
            try
            {
                lock (_lock) { _trackedTodoIds.Remove(todoId); }
                using (var conn = new OleDbConnection(_lockDbConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);
                    using (var cmd = new OleDbCommand("DELETE FROM T_TodoList_Sessions WHERE TodoId = ? AND UserId = ?", conn))
                    {
                        cmd.Parameters.AddWithValue("@TodoId", todoId);
                        cmd.Parameters.AddWithValue("@UserId", _currentUserId);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Gets other users currently on a TodoList item.
        /// Fresh DB query every time — no cache.
        /// </summary>
        public async Task<List<TodoSessionInfo>> GetActiveSessionsAsync(int todoId)
        {
            if (!FeatureFlags.ENABLE_MULTI_USER)
                return new List<TodoSessionInfo>();

            var sessions = new List<TodoSessionInfo>();
            try
            {
                using (var conn = new OleDbConnection(_lockDbConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);

                    // Clean stale sessions first
                    try
                    {
                        var cutoff = DateTime.Now.AddSeconds(-SESSION_TIMEOUT_SECONDS);
                        using (var delCmd = new OleDbCommand("DELETE FROM T_TodoList_Sessions WHERE LastHeartbeat < ?", conn))
                        {
                            delCmd.Parameters.Add("@C", OleDbType.Date).Value = cutoff;
                            await delCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    catch { }

                    // Get other users on this TodoList
                    var sql = @"SELECT UserId, UserName, SessionStart, LastHeartbeat 
                               FROM T_TodoList_Sessions 
                               WHERE TodoId = ? AND UserId <> ?
                               ORDER BY SessionStart";
                    using (var cmd = new OleDbCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@TodoId", todoId);
                        cmd.Parameters.AddWithValue("@UserId", _currentUserId);
                        using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                sessions.Add(new TodoSessionInfo
                                {
                                    UserId = reader["UserId"]?.ToString(),
                                    UserName = reader["UserName"]?.ToString(),
                                    SessionStart = reader["SessionStart"] as DateTime? ?? DateTime.MinValue,
                                    LastHeartbeat = reader["LastHeartbeat"] as DateTime? ?? DateTime.MinValue,
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionTracker.GetActiveSessions] ERROR: {ex.Message}");
            }
            return sessions;
        }

        /// <summary>
        /// Simple heartbeat: update LastHeartbeat for all tracked sessions every 60s.
        /// </summary>
        private void HeartbeatCallback(object state)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _heartbeatRunning, 1) == 1) return;

            try
            {
                if (_offlineFirstService != null && _offlineFirstService.IsAmbreImportInProgress())
                    return; // skip during imports

                int[] todoIds;
                lock (_lock) { todoIds = _trackedTodoIds.ToArray(); }
                if (todoIds.Length == 0) return;

                using (var conn = new OleDbConnection(_lockDbConnectionString))
                {
                    conn.Open();
                    var now = DateTime.Now;
                    foreach (var todoId in todoIds)
                    {
                        try
                        {
                            using (var cmd = new OleDbCommand("UPDATE T_TodoList_Sessions SET LastHeartbeat = ? WHERE TodoId = ? AND UserId = ?", conn))
                            {
                                cmd.Parameters.Add("@LH", OleDbType.Date).Value = now;
                                cmd.Parameters.AddWithValue("@TodoId", todoId);
                                cmd.Parameters.AddWithValue("@UserId", _currentUserId);
                                cmd.ExecuteNonQuery();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _heartbeatRunning, 0);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _heartbeatTimer?.Dispose();

            try
            {
                using (var conn = new OleDbConnection(_lockDbConnectionString))
                {
                    conn.Open();
                    using (var cmd = new OleDbCommand("DELETE FROM T_TodoList_Sessions WHERE UserId = ?", conn))
                    {
                        cmd.Parameters.AddWithValue("@UserId", _currentUserId);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Information about an active TodoList viewing session
    /// </summary>
    public class TodoSessionInfo
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public DateTime SessionStart { get; set; }
        public DateTime LastHeartbeat { get; set; }

        public TimeSpan Duration => DateTime.Now - SessionStart;
        public bool IsActive => (DateTime.Now - LastHeartbeat).TotalSeconds < 120;
    }
}
