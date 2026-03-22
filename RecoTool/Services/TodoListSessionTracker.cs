using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Configuration;

namespace RecoTool.Services
{
    /// <summary>
    /// Lightweight file-based tracker for active user sessions on TodoList items.
    /// Uses tiny text files on the shared network folder instead of OleDb — zero UI freeze.
    /// Session folder: _Sessions/ next to the lock DB.
    /// File format: {TodoId}_{UserId}.session containing "UserName|SessionStartTicks"
    /// Heartbeat = touch the file (update LastWriteTime).
    /// Active = file LastWriteTime within timeout window.
    /// </summary>
    public class TodoListSessionTracker : IDisposable
    {
        private readonly string _sessionFolder;
        private readonly string _currentUserId;
        private readonly string _currentUserName;
        private readonly OfflineFirstService _offlineFirstService;
        private readonly Timer _heartbeatTimer;
        private readonly HashSet<int> _trackedTodoIds = new HashSet<int>();
        private readonly object _lock = new object();
        private bool _disposed;
        private int _heartbeatRunning;
        private string _lastError;
        private DateTime _lastSuccessfulOp;

        private const int HEARTBEAT_INTERVAL_MS = 60_000;
        private const int SESSION_TIMEOUT_SECONDS = 180;

        /// <summary>
        /// Creates a file-based session tracker.
        /// </summary>
        /// <param name="sessionFolderPath">Absolute path to the shared _Sessions folder (e.g. \\server\share\_Sessions)</param>
        /// <param name="currentUserId">Windows username</param>
        /// <param name="offlineFirstService">Optional — used to skip heartbeat during imports</param>
        /// <param name="currentUserName">Display name (defaults to userId)</param>
        public TodoListSessionTracker(string sessionFolderPath, string currentUserId,
            OfflineFirstService offlineFirstService = null, string currentUserName = null)
        {
            if (string.IsNullOrWhiteSpace(sessionFolderPath))
                throw new ArgumentException("Session folder path is required", nameof(sessionFolderPath));
            if (string.IsNullOrWhiteSpace(currentUserId))
                throw new ArgumentException("User ID is required", nameof(currentUserId));

            _sessionFolder = sessionFolderPath;
            _currentUserId = currentUserId;
            _currentUserName = currentUserName ?? currentUserId;
            _offlineFirstService = offlineFirstService;

            if (FeatureFlags.ENABLE_MULTI_USER)
            {
                _heartbeatTimer = new Timer(HeartbeatCallback, null, HEARTBEAT_INTERVAL_MS, HEARTBEAT_INTERVAL_MS);
            }
            LogDiag($"Tracker created. Folder={_sessionFolder}, User={_currentUserId}");
        }

        /// <summary>
        /// Returns a diagnostic summary of the tracker state.
        /// </summary>
        public string GetDiagnostics()
        {
            try
            {
                var folderExists = Directory.Exists(_sessionFolder);
                int fileCount = 0;
                string[] files = null;
                if (folderExists)
                {
                    files = Directory.GetFiles(_sessionFolder, "*.session");
                    fileCount = files.Length;
                }
                int trackedCount;
                lock (_lock) { trackedCount = _trackedTodoIds.Count; }
                return $"Folder: {_sessionFolder}\n" +
                       $"Folder exists: {folderExists}\n" +
                       $"Session files: {fileCount}\n" +
                       $"Tracked TodoIds: {trackedCount}\n" +
                       $"User: {_currentUserId}\n" +
                       $"Last error: {_lastError ?? "(none)"}\n" +
                       $"Last OK: {_lastSuccessfulOp:HH:mm:ss}\n" +
                       (files != null && files.Length > 0 ? $"Files: {string.Join(", ", files.Select(Path.GetFileName))}" : "Files: (none)");
            }
            catch (Exception ex)
            {
                return $"Diagnostics error: {ex.Message}";
            }
        }

        private void LogDiag(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{_currentUserId}] {message}";
                System.Diagnostics.Debug.WriteLine($"[SessionTracker] {line}");
                // Also write to a log file in the session folder for cross-PC debugging
                if (!string.IsNullOrWhiteSpace(_sessionFolder))
                {
                    try
                    {
                        if (!Directory.Exists(_sessionFolder))
                            Directory.CreateDirectory(_sessionFolder);
                        File.AppendAllText(
                            Path.Combine(_sessionFolder, "_session_diag.log"),
                            line + Environment.NewLine);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Ensures the session folder exists. Cheap and safe to call multiple times.
        /// Replaces the old EnsureTableAsync — no OleDb involved.
        /// </summary>
        public Task EnsureTableAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_sessionFolder))
                        Directory.CreateDirectory(_sessionFolder);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionTracker] Cannot create session folder: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Registers that the current user is viewing a TodoList item.
        /// Writes a small .session file on the network share (background thread).
        /// </summary>
        public async Task<bool> RegisterViewingAsync(int todoId, string userName = null, bool isEditing = false)
        {
            if (!FeatureFlags.ENABLE_MULTI_USER) return true;
            try
            {
                lock (_lock) { _trackedTodoIds.Add(todoId); }

                var displayName = userName ?? _currentUserName;
                var filePath = GetSessionFilePath(todoId, _currentUserId);

                await Task.Run(() =>
                {
                    try
                    {
                        if (!Directory.Exists(_sessionFolder))
                            Directory.CreateDirectory(_sessionFolder);

                        // Content: "DisplayName|SessionStartTicks"
                        // SessionStart is only written on first creation; heartbeat just touches the file
                        if (!File.Exists(filePath))
                        {
                            File.WriteAllText(filePath, $"{displayName}|{DateTime.UtcNow.Ticks}");
                        }
                        else
                        {
                            // Already registered — just touch it
                            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow);
                        }
                        _lastSuccessfulOp = DateTime.Now;
                    }
                    catch (Exception ex)
                    {
                        _lastError = $"Register file write: {ex.Message}";
                        LogDiag($"Register FAILED: {ex.Message}");
                    }
                }).ConfigureAwait(false);

                LogDiag($"Registered TodoId={todoId} User={_currentUserId} File={filePath}");
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
        /// Deletes the .session file (background thread).
        /// </summary>
        public async Task UnregisterViewingAsync(int todoId)
        {
            if (!FeatureFlags.ENABLE_MULTI_USER) return;
            try
            {
                lock (_lock) { _trackedTodoIds.Remove(todoId); }

                var filePath = GetSessionFilePath(todoId, _currentUserId);
                await Task.Run(() =>
                {
                    try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                }).ConfigureAwait(false);
            }
            catch { }
        }

        /// <summary>
        /// Gets other users currently on a TodoList item.
        /// Scans session files by prefix — fast directory listing, no OleDb.
        /// </summary>
        public async Task<List<TodoSessionInfo>> GetActiveSessionsAsync(int todoId)
        {
            if (!FeatureFlags.ENABLE_MULTI_USER)
                return new List<TodoSessionInfo>();

            return await Task.Run(() =>
            {
                var sessions = new List<TodoSessionInfo>();
                try
                {
                    if (!Directory.Exists(_sessionFolder))
                        return sessions;

                    var prefix = $"{todoId}_";
                    var cutoff = DateTime.UtcNow.AddSeconds(-SESSION_TIMEOUT_SECONDS);

                    foreach (var filePath in Directory.GetFiles(_sessionFolder, $"{prefix}*.session"))
                    {
                        try
                        {
                            var fi = new FileInfo(filePath);

                            // Stale? Delete and skip
                            if (fi.LastWriteTimeUtc < cutoff)
                            {
                                try { fi.Delete(); } catch { }
                                continue;
                            }

                            // Extract userId from filename: {TodoId}_{UserId}.session
                            var fileName = Path.GetFileNameWithoutExtension(fi.Name);
                            var userId = fileName.Substring(prefix.Length);

                            // Skip self
                            if (string.Equals(userId, _currentUserId, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Parse content: "DisplayName|SessionStartTicks"
                            string displayName = userId;
                            DateTime sessionStart = fi.CreationTimeUtc;
                            try
                            {
                                var content = File.ReadAllText(filePath);
                                var parts = content.Split('|');
                                if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                                    displayName = parts[0];
                                if (parts.Length >= 2 && long.TryParse(parts[1], out var ticks))
                                    sessionStart = new DateTime(ticks, DateTimeKind.Utc);
                            }
                            catch { }

                            sessions.Add(new TodoSessionInfo
                            {
                                UserId = userId,
                                UserName = displayName,
                                SessionStart = sessionStart.ToLocalTime(),
                                LastHeartbeat = fi.LastWriteTimeUtc.ToLocalTime(),
                            });
                        }
                        catch { /* skip unreadable files */ }
                    }
                }
                catch (Exception ex)
                {
                    _lastError = $"GetActiveSessions: {ex.Message}";
                    LogDiag($"GetActiveSessions FAILED: {ex.Message}");
                }
                return sessions;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Heartbeat: touch all tracked session files (update LastWriteTime).
        /// Runs on ThreadPool — never blocks UI.
        /// </summary>
        private void HeartbeatCallback(object state)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _heartbeatRunning, 1) == 1) return;

            try
            {
                if (_offlineFirstService != null && _offlineFirstService.IsAmbreImportInProgress())
                    return;

                int[] todoIds;
                lock (_lock) { todoIds = _trackedTodoIds.ToArray(); }
                if (todoIds.Length == 0) return;

                var now = DateTime.UtcNow;
                foreach (var todoId in todoIds)
                {
                    try
                    {
                        var filePath = GetSessionFilePath(todoId, _currentUserId);
                        if (File.Exists(filePath))
                            File.SetLastWriteTimeUtc(filePath, now);
                    }
                    catch { }
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

            // Clean up all session files for this user — fire-and-forget on ThreadPool
            // NEVER do synchronous file I/O here: Dispose is often called from UI thread
            var folder = _sessionFolder;
            var userId = _currentUserId;
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        foreach (var file in Directory.GetFiles(folder, $"*_{userId}.session"))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
                catch { }
            });
        }

        private string GetSessionFilePath(int todoId, string userId)
        {
            return Path.Combine(_sessionFolder, $"{todoId}_{userId}.session");
        }

        /// <summary>
        /// Batch query: gets active sessions for multiple TodoIds in a SINGLE directory scan.
        /// Returns a dictionary keyed by TodoId. Much faster than calling GetActiveSessionsAsync N times.
        /// </summary>
        public async Task<Dictionary<int, List<TodoSessionInfo>>> GetAllActiveSessionsAsync(IEnumerable<int> todoIds)
        {
            var result = new Dictionary<int, List<TodoSessionInfo>>();
            if (!FeatureFlags.ENABLE_MULTI_USER || todoIds == null)
                return result;

            var todoIdSet = new HashSet<int>(todoIds);
            if (todoIdSet.Count == 0) return result;

            return await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(_sessionFolder))
                        return result;

                    var cutoff = DateTime.UtcNow.AddSeconds(-SESSION_TIMEOUT_SECONDS);

                    // Single directory scan — read ALL .session files once
                    foreach (var filePath in Directory.GetFiles(_sessionFolder, "*.session"))
                    {
                        try
                        {
                            var fileName = Path.GetFileNameWithoutExtension(filePath);
                            var underscoreIdx = fileName.IndexOf('_');
                            if (underscoreIdx <= 0) continue;

                            if (!int.TryParse(fileName.Substring(0, underscoreIdx), out var todoId))
                                continue;

                            // Only process requested TodoIds
                            if (!todoIdSet.Contains(todoId)) continue;

                            var userId = fileName.Substring(underscoreIdx + 1);

                            // Skip self
                            if (string.Equals(userId, _currentUserId, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var fi = new FileInfo(filePath);

                            // Stale? Delete and skip
                            if (fi.LastWriteTimeUtc < cutoff)
                            {
                                try { fi.Delete(); } catch { }
                                continue;
                            }

                            // Parse content
                            string displayName = userId;
                            DateTime sessionStart = fi.CreationTimeUtc;
                            try
                            {
                                var content = File.ReadAllText(filePath);
                                var parts = content.Split('|');
                                if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                                    displayName = parts[0];
                                if (parts.Length >= 2 && long.TryParse(parts[1], out var ticks))
                                    sessionStart = new DateTime(ticks, DateTimeKind.Utc);
                            }
                            catch { }

                            if (!result.TryGetValue(todoId, out var list))
                            {
                                list = new List<TodoSessionInfo>();
                                result[todoId] = list;
                            }

                            list.Add(new TodoSessionInfo
                            {
                                UserId = userId,
                                UserName = displayName,
                                SessionStart = sessionStart.ToLocalTime(),
                                LastHeartbeat = fi.LastWriteTimeUtc.ToLocalTime(),
                            });
                        }
                        catch { /* skip unreadable files */ }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionTracker.GetAllActiveSessions] ERROR: {ex.Message}");
                }
                return result;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Helper: derives the _Sessions folder path from a lock DB connection string.
        /// Use this when constructing the tracker from existing code that has the conn string.
        /// </summary>
        public static string DeriveSessionFolder(string lockDbConnectionString)
        {
            if (string.IsNullOrWhiteSpace(lockDbConnectionString))
                return null;

            // Extract file path from OleDb connection string like:
            // "Provider=Microsoft.ACE.OLEDB.12.0;Data Source=\\server\share\DB_FR_lock.accdb;..."
            try
            {
                var parts = lockDbConnectionString.Split(';');
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                    {
                        var dbPath = trimmed.Substring("Data Source=".Length).Trim();
                        var dir = Path.GetDirectoryName(dbPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            return Path.Combine(dir, "_Sessions");
                    }
                }
            }
            catch { }
            return null;
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
        public bool IsActive => (DateTime.Now - LastHeartbeat).TotalSeconds < SESSION_TIMEOUT_SECONDS;
        private const int SESSION_TIMEOUT_SECONDS = 180;
    }
}
