using System;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace RecoTool.Services
{
    // Partial: replication of country databases between local and network.
    // Covers AMBRE / RECON / DW round-trips (including ZIP-based snapshots), the
    // atomic publish pattern (tmp_ → swap → bak cleanup), pre-flight checks
    // (IsDatabaseLockedAsync, IsLocalReconciliationEmptyAsync), and the
    // _SyncConfig anchor write (SetLastSyncAnchorAsync). Pure path/cs helpers
    // live in OfflineFirstService.Paths.cs and OfflineFirstService.ConnectionStrings.cs.
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Extracts the .accdb from a local Ambre ZIP cache and atomically replaces the local AMBRE DB.
        /// </summary>
        private async Task ExtractAmbreZipToLocalAsync(string countryId, string localZipPath, string localDbPath)
        {
            string dataDirectory = Path.GetDirectoryName(localDbPath) ?? GetParameter("DataDirectory");
            if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory);

            using (var archive = ZipFile.OpenRead(localZipPath))
            {
                var accdbEntry = archive.Entries
                    .OrderByDescending(e => e.Length)
                    .FirstOrDefault(e => e.FullName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase));

                if (accdbEntry == null)
                {
                    System.Diagnostics.Debug.WriteLine($"AMBRE: Aucun .accdb dans l'archive {localZipPath}");
                    throw new FileNotFoundException("Aucun fichier .accdb dans l'archive AMBRE", localZipPath);
                }

                string baseNameLocal = Path.GetFileNameWithoutExtension(localDbPath);
                string tempLocalFromZip = Path.Combine(dataDirectory, $"{baseNameLocal}.accdb.tmp_{Guid.NewGuid():N}");
                string backupLocalZip = Path.Combine(dataDirectory, $"{baseNameLocal}.accdb.bak");

                accdbEntry.ExtractToFile(tempLocalFromZip, true);
                if (File.Exists(localDbPath))
                    await FileReplaceWithRetriesAsync(tempLocalFromZip, localDbPath, backupLocalZip, maxAttempts: 5, initialDelayMs: 300);
                else
                    File.Move(tempLocalFromZip, localDbPath);
            }
        }

        /// <summary>
        /// Copie un ZIP depuis le réseau vers un cache local si différent (taille/contenu) de manière atomique. Renvoie true si une copie a été effectuée.
        /// </summary>
        private async Task<bool> CopyZipIfDifferentAsync(string networkZipPath, string localZipPath)
        {
            var netFi = new FileInfo(networkZipPath);
            var locFi = new FileInfo(localZipPath);
            bool needZipCopy = !locFi.Exists || !FilesAreEqual(locFi, netFi);
            if (!needZipCopy) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(localZipPath) ?? string.Empty);
            string tmp = localZipPath + ".tmp_copy";
            await CopyFileAsync(networkZipPath, tmp, overwrite: true).ConfigureAwait(false);
            try { await FileReplaceWithRetriesAsync(tmp, localZipPath, localZipPath + ".bak", maxAttempts: 5, initialDelayMs: 200); }
            catch
            {
                try { if (File.Exists(localZipPath)) File.Delete(localZipPath); } catch { }
                File.Move(tmp, localZipPath);
            }
            try { var bak = localZipPath + ".bak"; if (File.Exists(bak)) File.Delete(bak); } catch { }
            // Normalize destination timestamp to source (UTC) to avoid false mismatches across clients
            try { File.SetLastWriteTimeUtc(localZipPath, netFi.LastWriteTimeUtc); } catch { }
            return true;
        }

        private static bool FilesAreEqual(FileInfo first, FileInfo second)
        {
            try
            {
                if (!first.Exists || !second.Exists) return false;
                if (first.Length != second.Length) return false;
                // Compare UTC timestamps with tolerance to absorb FS/ZIP/SMB rounding and DST issues
                var dt1 = first.LastWriteTimeUtc;
                var dt2 = second.LastWriteTimeUtc;
                var diff = dt1 > dt2 ? (dt1 - dt2) : (dt2 - dt1);
                return diff <= TimeSpan.FromSeconds(5);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Extrait le .accdb depuis un ZIP DW local et remplace atomiquement la base DW locale.
        /// </summary>
        private async Task ExtractDwZipToLocalAsync(string countryId, string localZipPath, string localDbPath)
        {
            string dataDirectory = Path.GetDirectoryName(localDbPath) ?? GetParameter("DataDirectory");
            if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory);
            using (var archive = ZipFile.OpenRead(localZipPath))
            {
                // Prefer explicit DW_Data.accdb if present (new unified DW format)
                var accdbEntry = archive.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), "DW_Data.accdb", StringComparison.OrdinalIgnoreCase));
                if (accdbEntry == null)
                {
                    // Fallback: pick the largest .accdb (legacy zips with multiple databases)
                    accdbEntry = archive.Entries
                        .Where(e => e.FullName.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(e => e.Length)
                        .FirstOrDefault();
                }
                if (accdbEntry == null)
                {
                    System.Diagnostics.Debug.WriteLine($"DW: Aucun .accdb dans l'archive {localZipPath}");
                    throw new FileNotFoundException("Aucun fichier .accdb dans l'archive DW", localZipPath);
                }
                string baseNameLocal = Path.GetFileNameWithoutExtension(localDbPath);
                string tempLocalFromZip = Path.Combine(dataDirectory, $"{baseNameLocal}.accdb.tmp_{Guid.NewGuid():N}");
                string backupLocalZip = Path.Combine(dataDirectory, $"{baseNameLocal}.accdb.bak");
                accdbEntry.ExtractToFile(tempLocalFromZip, true);
                if (File.Exists(localDbPath))
                    await FileReplaceWithRetriesAsync(tempLocalFromZip, localDbPath, backupLocalZip, maxAttempts: 5, initialDelayMs: 300);
                else
                    File.Move(tempLocalFromZip, localDbPath);
            }
        }

        public async Task SetLastSyncAnchorAsync(string countryId, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));
            string iso = utcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            // 1) Tentative sur la base de contrôle (centralisée) — best effort, ne bloque pas la suite
            try
            {
                var controlConnStr = GetControlConnectionString(countryId);
                using (var connection = new OleDbConnection(controlConnStr))
                {
                    await connection.OpenAsync();

                    // Assurer le schéma de contrôle si nécessaire
                    try { await EnsureControlSchemaAsync(); } catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SYNC][WARN] EnsureControlSchemaAsync a échoué: {ex.Message}");
                    }

                    // S'assurer que la table _SyncConfig existe
                    var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" });
                    bool tableExists = schema != null && schema.Rows.OfType<System.Data.DataRow>()
                        .Any(r => string.Equals(r["TABLE_NAME"].ToString(), "_SyncConfig", StringComparison.OrdinalIgnoreCase));
                    if (!tableExists)
                    {
                        using (var create = new OleDbCommand("CREATE TABLE _SyncConfig (ConfigKey TEXT(255) PRIMARY KEY, ConfigValue MEMO)", connection))
                        {
                            await create.ExecuteNonQueryAsync();
                        }
                    }

                    using (var update = new OleDbCommand("UPDATE _SyncConfig SET ConfigValue = @val WHERE ConfigKey = 'LastSyncTimestamp'", connection))
                    {
                        update.Parameters.Add(new OleDbParameter("@val", OleDbType.LongVarWChar) { Value = (object)iso ?? DBNull.Value });
                        int rows = await update.ExecuteNonQueryAsync();
                        if (rows == 0)
                        {
                            using (var insert = new OleDbCommand("INSERT INTO _SyncConfig (ConfigKey, ConfigValue) VALUES ('LastSyncTimestamp', @val)", connection))
                            {
                                insert.Parameters.Add(new OleDbParameter("@val", OleDbType.LongVarWChar) { Value = (object)iso ?? DBNull.Value });
                                await insert.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC][WARN] Echec mise à jour LastSyncTimestamp sur la base de contrôle ({countryId}): {ex.Message}. Tentative sur la base locale.");
            }

            // 2) Toujours écrire l'ancre dans la base LOCALE du pays (source pour le SyncOrchestrator)
            try
            {
                var localConnStr = GetCountryConnectionString(countryId);
                using (var connection = new OleDbConnection(localConnStr))
                {
                    await connection.OpenAsync();

                    // S'assurer que la table _SyncConfig existe côté local
                    var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" });
                    bool tableExists = schema != null && schema.Rows.OfType<System.Data.DataRow>()
                        .Any(r => string.Equals(r["TABLE_NAME"].ToString(), "_SyncConfig", StringComparison.OrdinalIgnoreCase));
                    if (!tableExists)
                    {
                        using (var create = new OleDbCommand("CREATE TABLE _SyncConfig (ConfigKey TEXT(255) PRIMARY KEY, ConfigValue MEMO)", connection))
                        {
                            await create.ExecuteNonQueryAsync();
                        }
                    }

                    using (var update = new OleDbCommand("UPDATE _SyncConfig SET ConfigValue = @val WHERE ConfigKey = 'LastSyncTimestamp'", connection))
                    {
                        update.Parameters.Add(new OleDbParameter("@val", OleDbType.LongVarWChar) { Value = (object)iso ?? DBNull.Value });
                        int rows = await update.ExecuteNonQueryAsync();
                        if (rows == 0)
                        {
                            using (var insert = new OleDbCommand("INSERT INTO _SyncConfig (ConfigKey, ConfigValue) VALUES ('LastSyncTimestamp', @val)", connection))
                            {
                                insert.Parameters.Add(new OleDbParameter("@val", OleDbType.LongVarWChar) { Value = (object)iso ?? DBNull.Value });
                                await insert.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[SYNC][INFO] LastSyncTimestamp stocké côté LOCAL pour {countryId} (fallback).");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC][ERROR] Impossible de stocker LastSyncTimestamp (control et local ont échoué) pour {countryId}: {ex.Message}");
                throw; // remonter l'erreur pour que l'appelant puisse informer l'utilisateur
            }
        }

        /// <summary>
        /// Vérifie si une base de données est verrouillée (import en cours)
        /// </summary>
        /// <param name="databasePath">Chemin de la base à vérifier</param>
        /// <returns>True si la base est verrouillée</returns>
        private async Task<bool> IsDatabaseLockedAsync(string databasePath)
        {
            try
            {
                using (var connection = new OleDbConnection(AceConn(databasePath)))
                {
                    await connection.OpenAsync();
                    return false; // ouverture exclusive OK => non verrouillée
                }
            }
            catch
            {
                // Toute exception lors de l'ouverture exclusive => considérer comme verrouillée
                return true;
            }
        }

        /// <summary>
        /// Indique si la table locale T_Reconciliation est vide pour le pays donné.
        /// Utilisé pour traiter un cas "cold-local" même si le fichier existe (DB fraîche).
        /// </summary>
        private async Task<bool> IsLocalReconciliationEmptyAsync(string countryId)
        {
            try
            {
                var connStr = GetCountryConnectionString(countryId);
                using (var connection = new OleDbConnection(connStr))
                {
                    await connection.OpenAsync();
                    // Vérifier l'existence de la table
                    var schema = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, null, "TABLE" });
                    bool hasTable = schema != null && schema.Rows.OfType<System.Data.DataRow>()
                        .Any(r => string.Equals(Convert.ToString(r["TABLE_NAME"]), "T_Reconciliation", StringComparison.OrdinalIgnoreCase));
                    if (!hasTable) return true; // considérer vide si la table n'existe pas

                    using (var cmd = new OleDbCommand("SELECT COUNT(*) FROM [T_Reconciliation]", connection))
                    {
                        var obj = await cmd.ExecuteScalarAsync();
                        int count = 0;
                        if (obj != null && obj != DBNull.Value)
                            count = Convert.ToInt32(obj, System.Globalization.CultureInfo.InvariantCulture);
                        return count == 0;
                    }
                }
            }
            catch
            {
                // En cas d'erreur, rester conservateur et retourner false pour ne pas masquer d'autres problèmes
                return false;
            }
        }
        /// <summary>
        /// Copie la base locale du pays vers l'emplacement réseau de manière atomique.
        /// Suppose que le verrou global a été acquis en amont pour éviter les accès concurrents.
        /// </summary>
        /// <param name="countryId">ID du pays</param>
        public async Task CopyLocalToNetworkAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId est requis", nameof(countryId));

            // Récupérer chemins
            string dataDirectory = GetParameter("DataDirectory");
            string remoteDir = GetParameter("CountryDatabaseDirectory");
            string countryPrefix = GetParameter("CountryDatabasePrefix") ?? "DB_";

            if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(remoteDir))
                throw new InvalidOperationException("Paramètres DataDirectory ou CountryDatabaseDirectory manquants (T_Param)");

            string localDbPath = Path.Combine(dataDirectory, $"{countryPrefix}{countryId}.accdb");
            string networkDbPath = Path.Combine(remoteDir, $"{countryPrefix}{countryId}.accdb");

            if (!File.Exists(localDbPath))
                throw new FileNotFoundException($"Base locale introuvable pour {countryId}", localDbPath);

            // S'assurer que le répertoire réseau existe
            if (!Directory.Exists(remoteDir))
            {
                Directory.CreateDirectory(remoteDir);
            }

            // Vérifier si la base réseau est verrouillée (meilleure robustesse)
            // Normalement inutile si le verrou global applicatif est respecté
            try
            {
                if (File.Exists(networkDbPath))
                {
                    bool locked = await IsDatabaseLockedAsync(networkDbPath);
                    if (locked)
                    {
                        throw new IOException($"La base réseau est verrouillée: {networkDbPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Avertissement: impossible de vérifier le verrou de la base réseau ({ex.Message}). Poursuite de la copie.");
            }

            // Chemins temporaires et sauvegarde
            string tempPath = Path.Combine(remoteDir, $"{countryPrefix}{countryId}.accdb.tmp_{Guid.NewGuid():N}");

            // Compact & Repair local DB to a temporary file before publishing (best-effort)
            string sourceForPublish = localDbPath;
            string compactTempLocal = null;
            try
            {
                compactTempLocal = await TryCompactAccessDatabaseAsync(localDbPath);
                if (!string.IsNullOrEmpty(compactTempLocal) && File.Exists(compactTempLocal))
                {
                    sourceForPublish = compactTempLocal;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Compact/Repair échec ({ex.Message}). Publication avec la base locale telle quelle.");
            }

            // Copier vers un fichier temporaire sur le même volume réseau
            File.Copy(sourceForPublish, tempPath, true);

            // Remplacer le fichier réseau sans créer de backup réseau
            if (File.Exists(networkDbPath))
            {
                try { File.Delete(networkDbPath); } catch { }
            }
            File.Move(tempPath, networkDbPath);

            System.Diagnostics.Debug.WriteLine($"Base locale publiée vers le réseau pour {countryId} -> {networkDbPath}");

            // Nettoyer le fichier compact temporaire s'il existe
            try { if (!string.IsNullOrEmpty(compactTempLocal) && File.Exists(compactTempLocal)) File.Delete(compactTempLocal); } catch { }

            // Purge orphan *.tmp_<guid> left over by previous crashed publishes (this user
            // or another user). 30-min age threshold protects an in-flight publish from a
            // concurrent client. Best-effort: any failure is logged in PurgeOrphanedTempFilesAsync.
            try { await PurgeOrphanedTempFilesAsync(remoteDir, TimeSpan.FromMinutes(30), _clock).ConfigureAwait(false); } catch { }
        }

        /// <summary>
        /// Crée une sauvegarde locale de la base RECON avant un import (fichier copié dans un dossier SavedLocal/ avec horodatage).
        /// Best-effort: nève pas d'exception si la sauvegarde échoue.
        /// </summary>
        public async Task CreateLocalReconciliationBackupAsync(string countryId, string label = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(countryId)) return;
                string localDbPath = GetLocalReconciliationDbPath(countryId);
                if (!File.Exists(localDbPath)) return;

                string dir = Path.GetDirectoryName(localDbPath);
                if (string.IsNullOrWhiteSpace(dir)) return;
                string savedDir = Path.Combine(dir, "SavedLocal");
                if (!Directory.Exists(savedDir)) Directory.CreateDirectory(savedDir);

                string baseName = Path.GetFileNameWithoutExtension(localDbPath);
                string timeStamp = _clock.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string suffix = string.IsNullOrWhiteSpace(label) ? "PreImport" : label.Trim();
                string backupPath = Path.Combine(savedDir, $"{baseName}_{suffix}_{timeStamp}.accdb");

                // Copier de manière asynchrone
                await CopyFileAsync(localDbPath, backupPath, overwrite: true).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"RECON: sauvegarde locale créée: {backupPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RECON: échec sauvegarde locale (non bloquant): {ex.Message}");
            }
        }

        /// <summary>
        /// Marque toutes les entrées non synchronisées du ChangeLog comme synchronisées.
        /// À utiliser immédiatement après publication locale->réseau (la base réseau reflète déjà les changements).
        /// </summary>
        /// <param name="countryId">ID du pays</param>
        public async Task MarkAllLocalChangesAsSyncedAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId est requis", nameof(countryId));

            // Le ChangeLog est stocké dans la base de lock côté réseau (voir DatabaseTemplateBuilder commentaire)
            var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(countryId));
            var unsynced = await tracker.GetUnsyncedChangesAsync();
            if (unsynced == null) return;
            var ids = unsynced.Select(c => c.Id).ToList();
            if (ids.Count == 0) return;
            await tracker.MarkChangesAsSyncedAsync(ids);
        }

        /// <summary>
        /// Copie la base réseau du pays vers la base locale de manière atomique (sur le volume local).
        /// Crée un fichier temporaire dans le répertoire local puis remplace atomiquement la base locale.
        /// </summary>
        /// <param name="countryId">ID du pays</param>
        public async Task CopyNetworkToLocalAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId est requis", nameof(countryId));

            string dataDirectory = GetParameter("DataDirectory");
            string remoteDir = GetParameter("CountryDatabaseDirectory");
            string countryPrefix = GetParameter("CountryDatabasePrefix") ?? "DB_";

            if (string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(remoteDir))
                throw new InvalidOperationException("Paramètres DataDirectory ou CountryDatabaseDirectory manquants (T_Param)");

            string localDbPath = Path.Combine(dataDirectory, $"{countryPrefix}{countryId}.accdb");
            string networkDbPath = Path.Combine(remoteDir, $"{countryPrefix}{countryId}.accdb");

            if (!File.Exists(networkDbPath))
                throw new FileNotFoundException($"Base réseau introuvable pour {countryId}", networkDbPath);

            // S'assurer que le répertoire local existe
            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
            }

            // Copier vers un fichier temporaire local, puis remplacer atomiquement la base locale
            string tempLocal = Path.Combine(dataDirectory, $"{countryPrefix}{countryId}.accdb.tmp_{Guid.NewGuid():N}");
            string backupLocal = Path.Combine(dataDirectory, $"{countryPrefix}{countryId}.accdb.bak");

            await CopyFileAsync(networkDbPath, tempLocal, overwrite: true).ConfigureAwait(false);

            if (File.Exists(localDbPath))
            {
                await FileReplaceWithRetriesAsync(tempLocal, localDbPath, backupLocal, maxAttempts: 5, initialDelayMs: 400);
            }
            else
            {
                File.Move(tempLocal, localDbPath);
            }

            System.Diagnostics.Debug.WriteLine($"Base réseau copiée vers le local pour {countryId} -> {localDbPath}");
            // Après un rafraîchissement complet local <- réseau, initialiser/mettre à jour l'ancre de synchronisation
            try { await SetLastSyncAnchorAsync(countryId, _clock.UtcNow); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC][WARN] Echec SetLastSyncAnchorAsync après copie réseau->local ({countryId}): {ex.Message}");
            }
        }

        /// <summary>
        /// Copie la base AMBRE réseau vers la base AMBRE locale (atomique côté local).
        /// Utilise les paramètres Ambre* si présents, sinon retombe sur Country*.
        /// </summary>
        public async Task CopyNetworkToLocalAmbreAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            // AMBRE est un instantané lecture seule côté client. Même s'il y a des changements locaux en attente
            // (liés à la table de réconciliation), on doit toujours rafraîchir AMBRE depuis le réseau.
            // On retire donc le blocage ici.

            string localDbPath = GetLocalAmbreDbPath(countryId);
            string dataDirectory = Path.GetDirectoryName(localDbPath);
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new InvalidOperationException("DataDirectory invalide");

            // 0) Si un ZIP AMBRE est présent côté réseau pour ce pays, on le préfère et on ne copie que s'il est différent
            try
            {
                string networkZipPath = GetNetworkAmbreZipPath(countryId);
                if (!string.IsNullOrWhiteSpace(networkZipPath) && File.Exists(networkZipPath))
                {
                    if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory);
                    string localZipPath = GetLocalAmbreZipCachePath(countryId);

                    var netZipFi = new FileInfo(networkZipPath);
                    var locZipFi = new FileInfo(localZipPath);
                    bool needZipCopy = !locZipFi.Exists || !FilesAreEqual(locZipFi, netZipFi);

                    if (needZipCopy)
                    {
                        string tmpZip = localZipPath + ".tmp_copy";
                        await CopyFileAsync(networkZipPath, tmpZip, overwrite: true).ConfigureAwait(false);
                        try { await FileReplaceWithRetriesAsync(tmpZip, localZipPath, localZipPath + ".bak", maxAttempts: 5, initialDelayMs: 250); }
                        catch
                        {
                            try { if (File.Exists(localZipPath)) File.Delete(localZipPath); } catch { }
                            await Task.Run(() => File.Move(tmpZip, localZipPath));
                        }
                        try { var bak = localZipPath + ".bak"; if (File.Exists(bak)) File.Delete(bak); } catch { }
                        // Normalize destination timestamp to source (UTC) to prevent false mismatch
                        try { File.SetLastWriteTimeUtc(localZipPath, netZipFi.LastWriteTimeUtc); } catch { }

                        await ExtractAmbreZipToLocalAsync(countryId, localZipPath, localDbPath);
                    }
                    else if (!File.Exists(localDbPath))
                    {
                        await ExtractAmbreZipToLocalAsync(countryId, localZipPath, localDbPath);
                    }

                    System.Diagnostics.Debug.WriteLine($"AMBRE: ZIP réseau synchronisé vers local pour {countryId} -> {localDbPath}");
                    try { await SetLastSyncAnchorAsync(countryId, _clock.UtcNow); } catch { }
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AMBRE: échec gestion ZIP ({ex.Message}). Bascule sur copie réseau.");
            }

            // 1) Fallback: copie brute .accdb réseau -> local
            string networkDbPath = GetNetworkAmbreDbPath(countryId);
            if (!File.Exists(networkDbPath))
                throw new FileNotFoundException($"Base AMBRE réseau introuvable pour {countryId}", networkDbPath);

            if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory);

            // Copier uniquement si le fichier réseau diffère du local
            var netFi = new FileInfo(networkDbPath);
            var locFi = new FileInfo(localDbPath);
            bool needCopy = !locFi.Exists || !FilesAreEqual(locFi, netFi);
            if (!needCopy)
            {
                System.Diagnostics.Debug.WriteLine($"AMBRE: local à jour pour {countryId} (aucune copie nécessaire)");
                return;
            }

            string baseName = Path.GetFileNameWithoutExtension(networkDbPath);
            string tempLocal = Path.Combine(dataDirectory, $"{baseName}.accdb.tmp_{Guid.NewGuid():N}");
            string backupLocal = Path.Combine(dataDirectory, $"{baseName}.accdb.bak");

            await CopyFileAsync(networkDbPath, tempLocal, overwrite: true).ConfigureAwait(false);
            if (File.Exists(localDbPath))
                await FileReplaceWithRetriesAsync(tempLocal, localDbPath, backupLocal, maxAttempts: 5, initialDelayMs: 300);
            else
                await Task.Run(() => File.Move(tempLocal, localDbPath));

            System.Diagnostics.Debug.WriteLine($"AMBRE: base réseau copiée vers local pour {countryId} -> {localDbPath}");
            try { await SetLastSyncAnchorAsync(countryId, _clock.UtcNow); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC][WARN] Echec SetLastSyncAnchorAsync après copie réseau->local AMBRE ({countryId}): {ex.Message}");
            }
        }

        /// <summary>
        /// Copie la base RECONCILIATION réseau vers la base locale (atomique côté local).
        /// Utilise les paramètres Reconciliation* si présents, sinon retombe sur Country*.
        /// </summary>
        public async Task CopyNetworkToLocalReconciliationAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            // Guard: prevent overwriting local RECON DB if unsynced local changes exist
            // Previous behavior threw an exception here; we now log and return silently to avoid noisy errors
            // during country initialization. The full sync path (push+pull) already aligns reconciliation data.
            if (await HasUnsyncedLocalChangesAsync(countryId))
            {
                try { System.Diagnostics.Debug.WriteLine($"RECON: refresh skipped due to pending local changes for {countryId}"); } catch { }
                return;
            }

            string localDbPath = GetLocalReconciliationDbPath(countryId);
            string networkDbPath = GetNetworkReconciliationDbPath(countryId);
            string dataDirectory = Path.GetDirectoryName(localDbPath);
            if (string.IsNullOrWhiteSpace(dataDirectory)) throw new InvalidOperationException("DataDirectory invalide");

            if (!File.Exists(networkDbPath))
                throw new FileNotFoundException($"Base RECON réseau introuvable pour {countryId}", networkDbPath);

            if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory);

            string baseName = Path.GetFileNameWithoutExtension(networkDbPath);
            string tempLocal = Path.Combine(dataDirectory, $"{baseName}.accdb.tmp_{Guid.NewGuid():N}");
            string backupLocal = Path.Combine(dataDirectory, $"{baseName}.accdb.bak");

            await CopyFileAsync(networkDbPath, tempLocal, overwrite: true).ConfigureAwait(false);
            if (File.Exists(localDbPath))
                await FileReplaceWithRetriesAsync(tempLocal, localDbPath, backupLocal, maxAttempts: 5, initialDelayMs: 300);
            else
                await Task.Run(() => File.Move(tempLocal, localDbPath));

            System.Diagnostics.Debug.WriteLine($"RECON: base réseau copiée vers local pour {countryId} -> {localDbPath}");
            // Après un rafraîchissement complet local <- réseau, initialiser/mettre à jour l'ancre de synchronisation
            try { await SetLastSyncAnchorAsync(countryId, _clock.UtcNow); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SYNC][WARN] Echec SetLastSyncAnchorAsync après copie réseau->local RECON ({countryId}): {ex.Message}");
            }
        }

        ///summary>
        /// Copie la base DWINGS réseau vers la base locale (atomique côté local).
        /// Retourne true si la copie s’est correctement effectuée, false sinon.
        /// </summary>
        public async Task<bool> CopyNetworkToLocalDwAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId))
                throw new ArgumentException("countryId est requis", nameof(countryId));

            try
            {
                // -----------------------------------------------------------------
                // Chemins locaux / réseaux
                // -----------------------------------------------------------------
                string localDbPath = GetLocalDwDbPath(countryId);
                string networkDbPath = GetNetworkDwDbPath(countryId);
                string dataDirectory = Path.GetDirectoryName(localDbPath) ?? throw new InvalidOperationException("DataDirectory invalide");

                // -----------------------------------------------------------------
                // 0) Gestion du ZIP DWINGS – priorité si disponible
                // -----------------------------------------------------------------
                try
                {
                    string netDwZip = GetNetworkDwZipPath(countryId);
                    if (!string.IsNullOrWhiteSpace(netDwZip) && File.Exists(netDwZip))
                    {
                        string locZip = GetLocalDwZipCachePath(countryId);
                        await CopyZipIfDifferentAsync(netDwZip, locZip);
                        await ExtractDwZipToLocalAsync(countryId, locZip, localDbPath);
                        Debug.WriteLine($"DW: ZIP extrait pour {countryId} -> {localDbPath}");
                        return true;        // extraction réussie, on sort
                    }
                }
                catch (Exception zipEx)
                {
                    Debug.WriteLine($"DW: échec extraction ZIP ({zipEx.Message}). Bascule sur copie fichier réseau.");
                }

                // -----------------------------------------------------------------
                // 1) Vérifications pré‑copie du fichier .accdb
                // -----------------------------------------------------------------
                if (!File.Exists(networkDbPath))
                {
                    Debug.WriteLine($"DW: Base réseau introuvable : {networkDbPath}");
                    return false;
                }

                if (!Directory.Exists(dataDirectory))
                    Directory.CreateDirectory(dataDirectory);

                // -----------------------------------------------------------------
                // 2) Copie atomique (temp → final) avec sauvegarde éventuelle
                // -----------------------------------------------------------------
                string baseName = Path.GetFileNameWithoutExtension(networkDbPath);
                string tempLocal = Path.Combine(dataDirectory, $"{baseName}.accdb.tmp_{Guid.NewGuid():N}");
                string backupLocal = Path.Combine(dataDirectory, $"{baseName}.accdb.bak");

                // Copie du réseau vers un fichier temporaire
                await CopyFileAsync(networkDbPath, tempLocal, overwrite: true).ConfigureAwait(false);

                // Si le fichier local existe déjà, on le remplace de façon résiliente
                if (File.Exists(localDbPath))
                {
                    await FileReplaceWithRetriesAsync(tempLocal,
                                                      localDbPath,
                                                      backupLocal,
                                                      maxAttempts: 5,
                                                      initialDelayMs: 300);
                }
                else
                {
                    // Aucun fichier local → on le déplace simplement
                    await Task.Run(() => File.Move(tempLocal, localDbPath));
                }

                Debug.WriteLine($"DW: Base réseau copiée vers local pour {countryId} -> {localDbPath}");
                return true;
            }
            catch (Exception ex)
            {
                // Tout ce qui n’a pas été capturé précédemment arrive ici.
                Debug.WriteLine($"DW: Exception pendant la copie pour {countryId} – {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Publie la base AMBRE locale vers le réseau.
        /// </summary>
        public async Task CopyLocalToNetworkAmbreAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            string localDbPath = GetLocalAmbreDbPath(countryId);
            if (!File.Exists(localDbPath)) throw new FileNotFoundException($"Base AMBRE locale introuvable pour {countryId}", localDbPath);

            string networkZipPath = GetNetworkAmbreZipPath(countryId);
            string remoteDir = Path.GetDirectoryName(networkZipPath);
            if (string.IsNullOrWhiteSpace(remoteDir)) throw new InvalidOperationException("Répertoire réseau AMBRE invalide");
            if (!Directory.Exists(remoteDir)) Directory.CreateDirectory(remoteDir);

            // Pas de sauvegarde réseau

            // Compact local puis créer un ZIP temporaire local
            string sourceForZip = localDbPath;
            string compactTempLocal = null;
            try
            {
                compactTempLocal = await TryCompactAccessDatabaseAsync(localDbPath);
                if (!string.IsNullOrEmpty(compactTempLocal) && File.Exists(compactTempLocal)) sourceForZip = compactTempLocal;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AMBRE: Compact/Repair échec ({ex.Message}). Compression du fichier courant.");
            }

            string localTempZip = Path.Combine(Path.GetDirectoryName(localDbPath) ?? Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(localDbPath)}_AMBRE.zip.tmp_{Guid.NewGuid():N}");
            try
            {
                using (var archive = ZipFile.Open(localTempZip, ZipArchiveMode.Create))
                {
                    string entryName = Path.GetFileName(localDbPath);
                    archive.CreateEntryFromFile(sourceForZip, entryName, CompressionLevel.Optimal);
                }
            }
            catch
            {
                try { if (File.Exists(localTempZip)) File.Delete(localTempZip); } catch { }
                throw;
            }

            // Copier le ZIP temporaire vers le réseau de façon atomique
            string tempRemote = Path.Combine(remoteDir, $"{Path.GetFileNameWithoutExtension(networkZipPath)}.tmp_{Guid.NewGuid():N}.zip");
            File.Copy(localTempZip, tempRemote, true);
            if (File.Exists(networkZipPath)) { try { File.Delete(networkZipPath); } catch { } }
            File.Move(tempRemote, networkZipPath);
            System.Diagnostics.Debug.WriteLine($"AMBRE: archive ZIP publiée vers réseau pour {countryId} -> {networkZipPath}");

            // Nettoyage local temporaire
            try { if (File.Exists(localTempZip)) File.Delete(localTempZip); } catch { }
            try { if (!string.IsNullOrEmpty(compactTempLocal) && File.Exists(compactTempLocal)) File.Delete(compactTempLocal); } catch { }
        }

        /// <summary>
        /// Publie la base RECONCILIATION locale vers le réseau.
        /// </summary>
        public async Task CopyLocalToNetworkReconciliationAsync(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId est requis", nameof(countryId));

            string localDbPath = GetLocalReconciliationDbPath(countryId);
            string networkDbPath = GetNetworkReconciliationDbPath(countryId);
            if (!File.Exists(localDbPath)) throw new FileNotFoundException($"Base RECON locale introuvable pour {countryId}", localDbPath);

            string remoteDir = Path.GetDirectoryName(networkDbPath);
            if (string.IsNullOrWhiteSpace(remoteDir)) throw new InvalidOperationException("Répertoire réseau RECON invalide");
            if (!Directory.Exists(remoteDir)) Directory.CreateDirectory(remoteDir);

            try
            {
                if (File.Exists(networkDbPath))
                {
                    bool locked = await IsDatabaseLockedAsync(networkDbPath);
                    if (locked) throw new IOException($"La base RECON réseau est verrouillée: {networkDbPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RECON: avertissement vérification verrou échouée ({ex.Message}). Poursuite.");
            }

            // Pas de sauvegarde réseau

            // Compact & Replace atomique
            string baseNameForTemp = Path.GetFileNameWithoutExtension(networkDbPath);
            string tempPath = Path.Combine(remoteDir, $"{baseNameForTemp}.accdb.tmp_{Guid.NewGuid():N}");

            string sourceForPublish = localDbPath;
            string compactTempLocal = null;
            try
            {
                compactTempLocal = await TryCompactAccessDatabaseAsync(localDbPath);
                if (!string.IsNullOrEmpty(compactTempLocal) && File.Exists(compactTempLocal)) sourceForPublish = compactTempLocal;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RECON: Compact/Repair échec ({ex.Message}). Publication avec la base locale.");
            }

            File.Copy(sourceForPublish, tempPath, true);
            if (File.Exists(networkDbPath)) { try { File.Delete(networkDbPath); } catch { } }
            File.Move(tempPath, networkDbPath);
            System.Diagnostics.Debug.WriteLine($"RECON: base locale publiée vers réseau pour {countryId} -> {networkDbPath}");
            try { if (!string.IsNullOrEmpty(compactTempLocal) && File.Exists(compactTempLocal)) File.Delete(compactTempLocal); } catch { }
        }
    }
}
