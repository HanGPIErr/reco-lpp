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
    /// No OleDb connections. File is ~500 bytes max, read/written in <1ms.
    /// 
    /// File format:
    ///   [4 bytes] SyncVersion (uint32) - incremented after each push
    ///   [4 bytes] UserCount (int32)
    ///   Per user (fixed 64 bytes each):
    ///     [20 bytes] Username (UTF8, null-padded)
    ///     [8 bytes]  HeartbeatTicks (int64)
    ///     [36 bytes] ActiveRowId (UTF8, null-padded) - currently selected row ID
    /// </summary>
    public static class PresenceFile
    {
        private const int UserNameLen = 20;
        private const int RowIdLen = 36;
        private const int UserBlockLen = UserNameLen + 8 + RowIdLen; // 64 bytes
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
            public string ActiveRowId { get; set; }
            public bool IsStale => (DateTime.UtcNow - LastHeartbeat) > StaleTimeout;
            public string Initials
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(UserName)) return "?";
                    var parts = UserName.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
                    return UserName.Length >= 2 ? UserName.Substring(0, 2).ToUpper() : UserName.ToUpper();
                }
            }
        }

        /// <summary>
        /// Reads the presence file. Returns null if file doesn't exist or read fails.
        /// Designed to be called every 1-2s from a background thread. Takes <1ms on LAN.
        /// </summary>
        public static PresenceData Read(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < HeaderLen) return null;

                var data = new PresenceData();
                data.SyncVersion = BitConverter.ToUInt32(bytes, 0);
                int userCount = BitConverter.ToInt32(bytes, 4);
                if (userCount < 0 || userCount > MaxUsers) userCount = 0;

                for (int i = 0; i < userCount && HeaderLen + (i + 1) * UserBlockLen <= bytes.Length; i++)
                {
                    int offset = HeaderLen + i * UserBlockLen;
                    var user = new UserPresence
                    {
                        UserName = ReadString(bytes, offset, UserNameLen),
                        LastHeartbeat = new DateTime(BitConverter.ToInt64(bytes, offset + UserNameLen), DateTimeKind.Utc),
                        ActiveRowId = ReadString(bytes, offset + UserNameLen + 8, RowIdLen)
                    };
                    data.Users.Add(user);
                }

                // Filter out stale users
                data.Users.RemoveAll(u => u.IsStale);
                return data;
            }
            catch { return null; }
        }

        /// <summary>
        /// Updates presence for the current user (heartbeat + active row).
        /// Reads the file, merges current user, writes back. Thread-safe via retry.
        /// </summary>
        public static void WriteHeartbeat(string filePath, string userName, string activeRowId = null)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var data = Read(filePath) ?? new PresenceData();

                    // Remove stale users + current user (will re-add below)
                    data.Users.RemoveAll(u => u.IsStale || string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));

                    // Add current user
                    data.Users.Add(new UserPresence
                    {
                        UserName = userName,
                        LastHeartbeat = DateTime.UtcNow,
                        ActiveRowId = activeRowId
                    });

                    // Cap to max users
                    if (data.Users.Count > MaxUsers)
                        data.Users = data.Users.OrderByDescending(u => u.LastHeartbeat).Take(MaxUsers).ToList();

                    Write(filePath, data);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50); // Brief retry on file lock contention
                }
                catch { return; }
            }
        }

        /// <summary>
        /// Increments the SyncVersion counter. Called after each successful push.
        /// </summary>
        public static void IncrementSyncVersion(string filePath)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var data = Read(filePath) ?? new PresenceData();
                    data.SyncVersion++;
                    Write(filePath, data);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
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
                WriteString(buffer, offset + UserNameLen + 8, RowIdLen, data.Users[i].ActiveRowId);
            }

            File.WriteAllBytes(filePath, buffer);
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
