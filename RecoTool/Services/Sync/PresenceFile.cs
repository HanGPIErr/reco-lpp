using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RecoTool.Services.Sync
{
    /// <summary>
    /// Ultra-lightweight presence system using a single binary file per country on the network share.
    /// No OleDb connections. File is ~4KB max, read/written in <1ms.
    /// 
    /// File format (v2 — multi-row):
    ///   [4 bytes] SyncVersion (uint32) - incremented after each push
    ///   [4 bytes] UserCount (int32)
    ///   Per user (fixed 396 bytes each):
    ///     [20 bytes] Username (UTF8, null-padded)
    ///     [8 bytes]  HeartbeatTicks (int64)
    ///     [4 bytes]  ActiveRowCount (int32, 0..MaxActiveRows)
    ///     [MaxActiveRows * 36 bytes] ActiveRowIds (UTF8, null-padded each)
    /// </summary>
    public static class PresenceFile
    {
        private const int UserNameLen = 20;
        private const int RowIdLen = 36;
        private const int MaxActiveRows = 10;
        private const int UserBlockLen = UserNameLen + 8 + 4 + MaxActiveRows * RowIdLen; // 20+8+4+360 = 392 bytes
        private const int HeaderLen = 8; // SyncVersion(4) + UserCount(4)
        private const int MaxUsers = 10;
        private static readonly TimeSpan StaleTimeout = TimeSpan.FromSeconds(30);

        public class PresenceData
        {
            public uint SyncVersion { get; set; }
            public List<UserPresence> Users { get; set; } = new List<UserPresence>();
        }

        public class UserPresence
        {
            public string UserName { get; set; }
            public DateTime LastHeartbeat { get; set; }
            public List<string> ActiveRowIds { get; set; } = new List<string>();
            public bool IsStale => (DateTime.UtcNow - LastHeartbeat) > StaleTimeout;

            // Display name resolved on the UI side (not stored in file)
            public string DisplayName { get; set; }

            public string Initials
            {
                get
                {
                    var src = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : UserName;
                    if (string.IsNullOrWhiteSpace(src)) return "?";
                    var parts = src.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
                    return src.Length >= 2 ? src.Substring(0, 2).ToUpper() : src.ToUpper();
                }
            }
        }

        /// <summary>
        /// Reads the presence file. Returns null if file doesn't exist or read fails.
        /// Uses FileShare.ReadWrite to avoid IOException when another process is writing.
        /// </summary>
        public static PresenceData Read(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                byte[] bytes;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
                {
                    bytes = new byte[fs.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = fs.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                if (bytes.Length < HeaderLen) return null;

                var data = new PresenceData();
                data.SyncVersion = BitConverter.ToUInt32(bytes, 0);
                int userCount = BitConverter.ToInt32(bytes, 4);
                if (userCount < 0 || userCount > MaxUsers) userCount = 0;

                for (int i = 0; i < userCount && HeaderLen + (i + 1) * UserBlockLen <= bytes.Length; i++)
                {
                    int offset = HeaderLen + i * UserBlockLen;
                    int rowCountOffset = offset + UserNameLen + 8;
                    int rowCount = 0;
                    if (rowCountOffset + 4 <= bytes.Length)
                        rowCount = Math.Min(BitConverter.ToInt32(bytes, rowCountOffset), MaxActiveRows);
                    if (rowCount < 0) rowCount = 0;

                    var user = new UserPresence
                    {
                        UserName = ReadString(bytes, offset, UserNameLen),
                        LastHeartbeat = new DateTime(BitConverter.ToInt64(bytes, offset + UserNameLen), DateTimeKind.Utc),
                    };
                    int rowsBase = rowCountOffset + 4;
                    for (int r = 0; r < rowCount; r++)
                    {
                        int rOff = rowsBase + r * RowIdLen;
                        if (rOff + RowIdLen > bytes.Length) break;
                        var rid = ReadString(bytes, rOff, RowIdLen);
                        if (!string.IsNullOrWhiteSpace(rid)) user.ActiveRowIds.Add(rid);
                    }
                    data.Users.Add(user);
                }

                // Filter out stale users
                data.Users.RemoveAll(u => u.IsStale);
                return data;
            }
            catch { return null; }
        }

        /// <summary>
        /// Updates presence for the current user (heartbeat + active rows).
        /// Reads the file, merges current user, writes back. Thread-safe via retry.
        /// </summary>
        public static void WriteHeartbeat(string filePath, string userName, IList<string> activeRowIds = null)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var data = Read(filePath) ?? new PresenceData();

                    // Remove stale users + current user (will re-add below)
                    data.Users.RemoveAll(u => u.IsStale || string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));

                    // Add current user
                    var entry = new UserPresence
                    {
                        UserName = userName,
                        LastHeartbeat = DateTime.UtcNow,
                    };
                    if (activeRowIds != null)
                    {
                        for (int i = 0; i < Math.Min(activeRowIds.Count, MaxActiveRows); i++)
                        {
                            if (!string.IsNullOrWhiteSpace(activeRowIds[i]))
                                entry.ActiveRowIds.Add(activeRowIds[i]);
                        }
                    }
                    data.Users.Add(entry);

                    // Cap to max users
                    if (data.Users.Count > MaxUsers)
                        data.Users = data.Users.OrderByDescending(u => u.LastHeartbeat).Take(MaxUsers).ToList();

                    Write(filePath, data);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 + attempt * 50); // Increasing retry delay
                }
                catch { return; }
            }
        }

        /// <summary>
        /// Increments the SyncVersion counter. Called after each successful push.
        /// </summary>
        public static void IncrementSyncVersion(string filePath)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var data = Read(filePath) ?? new PresenceData();
                    data.SyncVersion++;
                    Write(filePath, data);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 + attempt * 50);
                }
                catch { return; }
            }
        }

        /// <summary>
        /// Removes the current user from the presence file (on app close / country switch).
        /// </summary>
        public static void RemoveUser(string filePath, string userName)
        {
            try
            {
                var data = Read(filePath);
                if (data == null) return;
                data.Users.RemoveAll(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase) || u.IsStale);
                Write(filePath, data);
            }
            catch { }
        }

        public static string GetPresenceFilePath(string countryDatabaseDirectory, string countryDatabasePrefix, string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryDatabaseDirectory) || string.IsNullOrWhiteSpace(countryId)) return null;
            var prefix = string.IsNullOrWhiteSpace(countryDatabasePrefix) ? "DB_" : countryDatabasePrefix;
            return Path.Combine(countryDatabaseDirectory, $"{prefix}{countryId}.presence");
        }

        private static void Write(string filePath, PresenceData data)
        {
            var buffer = new byte[HeaderLen + data.Users.Count * UserBlockLen];
            BitConverter.GetBytes(data.SyncVersion).CopyTo(buffer, 0);
            BitConverter.GetBytes(data.Users.Count).CopyTo(buffer, 4);

            for (int i = 0; i < data.Users.Count; i++)
            {
                int offset = HeaderLen + i * UserBlockLen;
                WriteString(buffer, offset, UserNameLen, data.Users[i].UserName);
                BitConverter.GetBytes(data.Users[i].LastHeartbeat.Ticks).CopyTo(buffer, offset + UserNameLen);
                int rowCount = Math.Min(data.Users[i].ActiveRowIds?.Count ?? 0, MaxActiveRows);
                BitConverter.GetBytes(rowCount).CopyTo(buffer, offset + UserNameLen + 8);
                int rowsBase = offset + UserNameLen + 8 + 4;
                for (int r = 0; r < rowCount; r++)
                    WriteString(buffer, rowsBase + r * RowIdLen, RowIdLen, data.Users[i].ActiveRowIds[r]);
            }

            // Use FileStream + FileShare.ReadWrite to avoid IOException when another process reads simultaneously
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096))
            {
                fs.Write(buffer, 0, buffer.Length);
                fs.Flush();
            }
        }

        private static string ReadString(byte[] buffer, int offset, int maxLen)
        {
            if (offset + maxLen > buffer.Length) return null;
            int end = offset;
            while (end < offset + maxLen && buffer[end] != 0) end++;
            if (end == offset) return null;
            return Encoding.UTF8.GetString(buffer, offset, end - offset);
        }

        private static void WriteString(byte[] buffer, int offset, int maxLen, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var bytes = Encoding.UTF8.GetBytes(value);
            int len = Math.Min(bytes.Length, maxLen - 1); // Leave room for null terminator
            Array.Copy(bytes, 0, buffer, offset, len);
        }
    }
}
