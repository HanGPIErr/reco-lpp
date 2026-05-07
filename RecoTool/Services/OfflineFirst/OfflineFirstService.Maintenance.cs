using System;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RecoTool.Services
{
    // Partial: maintenance utilities (compact/repair, cleanup)
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Tente un Compact & Repair de la base Access en utilisant Access.Application (COM) en late-binding.
        /// Retourne le chemin du fichier compacté si succès, sinon null. Best-effort: ne jette pas en cas d'absence d'Access.
        /// </summary>
        /// <param name="sourcePath">Chemin de la base source (.accdb)</param>
        /// <returns>Chemin du fichier compacté temporaire, ou null en cas d'échec</returns>
        private async Task<string> TryCompactAccessDatabaseAsync(string sourcePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
                string dir = Path.GetDirectoryName(sourcePath);
                string nameNoExt = Path.GetFileNameWithoutExtension(sourcePath);
                string tempCompact = Path.Combine(dir ?? "", $"{nameNoExt}.compact_{Guid.NewGuid():N}.accdb");

                return await Task.Run(() =>
                {
                    try
                    {
                        // Late-bind Access.Application to avoid adding COM references
                        var accType = Type.GetTypeFromProgID("Access.Application");
                        if (accType == null) return null;
                        dynamic app = Activator.CreateInstance(accType);
                        try
                        {
                            // Some versions return bool, some void; rely on file existence afterwards
                            try { var _ = app.CompactRepair(sourcePath, tempCompact, true); }
                            catch { app.CompactRepair(sourcePath, tempCompact, true); }

                            return File.Exists(tempCompact) ? tempCompact : null;
                        }
                        finally
                        {
                            try { app.Quit(); } catch { }
                        }
                    }
                    catch
                    {
                        return null;
                    }
                });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes orphaned temporary files left behind by atomic publish/copy patterns
        /// (e.g. <c>DB_DATA_DK.accdb.tmp_&lt;guid&gt;</c>, <c>*.compact_&lt;guid&gt;.accdb</c>,
        /// <c>*.tmp_copy</c>) when the process crashed or lost the network share between
        /// the temp-copy and the final move/replace. Files younger than <paramref name="minAge"/>
        /// are kept to avoid stealing temp files that another user is currently writing.
        /// All operations are best-effort: missing directories or locked files are ignored.
        /// </summary>
        /// <param name="directory">Directory to scan (network share or local data directory).</param>
        /// <param name="minAge">Minimum age before a temp file is considered orphaned.</param>
        /// <returns>Number of files actually deleted.</returns>
        public static async Task<int> PurgeOrphanedTempFilesAsync(string directory, TimeSpan minAge)
        {
            if (string.IsNullOrWhiteSpace(directory)) return 0;
            if (!Directory.Exists(directory)) return 0;

            // Patterns produced by the atomic publish helpers across the codebase.
            // Kept conservative: only files with a guid-like suffix that no production
            // code path uses for the final artifact name.
            var patterns = new[]
            {
                "*.tmp_*",          // *.accdb.tmp_<guid>, *.zip.tmp_<guid>, *.tmp_copy, *.tmp_copy.bak
                "*.compact_*.accdb" // Compact & Repair scratch files
            };

            var cutoffUtc = DateTime.UtcNow - minAge;
            int deleted = 0;

            await Task.Run(() =>
            {
                foreach (var pattern in patterns)
                {
                    string[] matches;
                    try { matches = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly); }
                    catch { continue; }

                    foreach (var path in matches)
                    {
                        try
                        {
                            // De-dup safety: GetFiles("*.tmp_*") on Windows can also match
                            // files containing ".tmp_" anywhere — re-check with InvariantCulture.
                            var name = Path.GetFileName(path);
                            if (name.IndexOf(".tmp_", StringComparison.OrdinalIgnoreCase) < 0
                                && name.IndexOf(".compact_", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            var info = new FileInfo(path);
                            if (!info.Exists) continue;
                            if (info.LastWriteTimeUtc > cutoffUtc) continue; // too recent → likely in-flight

                            info.Delete();
                            deleted++;
                            System.Diagnostics.Debug.WriteLine($"[PurgeOrphanedTempFiles] deleted {path}");
                        }
                        catch (Exception ex)
                        {
                            // File locked, perms, race condition — try again next time.
                            System.Diagnostics.Debug.WriteLine($"[PurgeOrphanedTempFiles] skipped {path}: {ex.Message}");
                        }
                    }
                }
            }).ConfigureAwait(false);

            return deleted;
        }

        /// <summary>
        /// Deletes all synchronized ChangeLog entries from the control/lock database and then attempts a Compact & Repair.
        /// Safe to call multiple times. Should be called while holding the global lock to avoid external access.
        /// IMPORTANT: May fail if other connections (e.g., TodoListSessionTracker heartbeat) are holding the DB open.
        /// </summary>
        public async Task CleanupChangeLogAndCompactAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            // 1) Delete synchronized rows from ChangeLog (best-effort if table exists)
            try
            {
                var connStr = GetRemoteLockConnectionString(countryId);
                using (var connection = new OleDbConnection(connStr))
                {
                    await connection.OpenAsync();

                    // Verify table exists before deleting
                    var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" });
                    bool hasChangeLog = schema != null && schema.Rows.OfType<System.Data.DataRow>()
                        .Any(r => string.Equals(Convert.ToString(r["TABLE_NAME"]), "ChangeLog", StringComparison.OrdinalIgnoreCase));
                    if (hasChangeLog)
                    {
                        using (var cmd = new OleDbCommand("DELETE FROM ChangeLog WHERE Synchronized = TRUE", connection))
                        {
                            try { await cmd.ExecuteNonQueryAsync(); } catch { /* ignore delete errors */ }
                        }
                    }
                }
            }
            catch { /* best-effort cleanup */ }

            // Wait briefly for any in-flight DB operations to complete
            await Task.Delay(500).ConfigureAwait(false);

            // 3) Compact & Repair the lock/control database to reclaim space
            try
            {
                var dbPath = GetRemoteLockDbPath(countryId);
                System.Diagnostics.Debug.WriteLine($"[CleanupChangeLogAndCompactAsync] Attempting to compact control DB: {dbPath}");
                var compacted = await TryCompactAccessDatabaseAsync(dbPath);
                
                if (string.IsNullOrWhiteSpace(compacted) || !File.Exists(compacted))
                {
                    System.Diagnostics.Debug.WriteLine("[CleanupChangeLogAndCompactAsync] Compaction failed or was skipped (likely DB is locked by heartbeat or other process)");
                    return; // Exit early if compaction failed
                }
                
                if (!string.IsNullOrWhiteSpace(compacted) && File.Exists(compacted))
                {
                    try
                    {
                        await FileReplaceWithRetriesAsync(compacted, dbPath, dbPath + ".bak", maxAttempts: 6, initialDelayMs: 300);
                    }
                    catch
                    {
                        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
                        File.Move(compacted, dbPath);
                    }
                    // Cleanup backup if present
                    try { var bak = dbPath + ".bak"; if (File.Exists(bak)) File.Delete(bak); } catch { }
                }
            }
            catch { /* ignore compaction errors */ }
        }
    }
}
