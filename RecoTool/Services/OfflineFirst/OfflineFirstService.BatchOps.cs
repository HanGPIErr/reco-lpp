using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using OfflineFirstAccess.Models;
using RecoTool.Infrastructure.DataAccess;

namespace RecoTool.Services
{
    // Partial: batch entity write operations (INSERT/UPDATE/DELETE/ARCHIVE) and
    // single-entity delete. Centralizes the Access/Jet lock detection helper used
    // by retry loops. CRC32 helpers live in OfflineFirstService.Crc.cs.
    public partial class OfflineFirstService
    {
        private static bool IsAccessLockException(OleDbException ex)
        {
            // Common Access/Jet/ACE locking and sharing violation error codes
            // 3218: Could not update; currently locked by another session/user
            // 3260: Couldn't lock table; already in use
            // 3050: Couldn't lock file
            // 3188/3197: Couldn't update; currently locked
            // 3704: Operation not allowed when the object is closed (may follow a lock)
            var codes = new HashSet<int> { 3218, 3260, 3050, 3188, 3197 };
            try
            {
                foreach (OleDbError err in ex.Errors)
                {
                    if (codes.Contains(err.NativeError)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Applique en lot des ajouts/mises à jour/archivages dans une seule connexion et transaction.
        /// Réduit drastiquement le coût des imports volumineux.
        /// </summary>
        /// <param name="identifier">Country identifier.</param>
        /// <param name="toAdd">Entities to insert.</param>
        /// <param name="toUpdate">Entities to update.</param>
        /// <param name="toArchive">Entities to logically delete/archive.</param>
        /// <param name="suppressChangeLog">Quand true, n'enregistre pas les changements dans la table ChangeLog (utile pour imports Ambre).</param>
        public async Task<bool> ApplyEntitiesBatchAsync(string identifier, List<Entity> toAdd, List<Entity> toUpdate, List<Entity> toArchive, bool suppressChangeLog = false)
        {
            EnsureInitialized();
            toAdd = toAdd ?? new List<Entity>();
            toUpdate = toUpdate ?? new List<Entity>();
            toArchive = toArchive ?? new List<Entity>();

            if (toAdd.Count == 0 && toUpdate.Count == 0 && toArchive.Count == 0)
                return true;

            // Choose target DB based on involved tables.
            // If the batch exclusively targets AMBRE table, use the AMBRE local DB.
            // Otherwise use the default local (reconciliation) DB.
            var allTables = toAdd.Select(e => e.TableName)
                                 .Concat(toUpdate.Select(e => e.TableName))
                                 .Concat(toArchive.Select(e => e.TableName))
                                 .Where(t => !string.IsNullOrWhiteSpace(t))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            string selectedConnStr;
            if (allTables.Count == 1 && string.Equals(allTables[0], "T_Data_Ambre", StringComparison.OrdinalIgnoreCase))
            {
                var ambrePath = GetLocalAmbreDbPath(identifier);
                selectedConnStr = AceConn(ambrePath);
            }
            else
            {
                selectedConnStr = GetLocalConnectionString();
            }

            using (var connection = new OleDbConnection(selectedConnStr))
            {
                await connection.OpenAsync();
                using (var tx = connection.BeginTransaction())
                {
                    var changeTuples = new List<(string TableName, string RecordId, string OperationType)>();
                    // Caches must be declared outside try to be visible in finally
                    var tableColsCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    var colTypeCache = new Dictionary<string, Dictionary<string, OleDbType>>(StringComparer.OrdinalIgnoreCase);
                    var archiveCmdCache = new Dictionary<string, OleDbCommand>(StringComparer.OrdinalIgnoreCase);
                    var insertCmdCache = new Dictionary<string, (OleDbCommand Cmd, List<string> Cols)>(StringComparer.OrdinalIgnoreCase);
                    var updateCmdCache = new Dictionary<string, (OleDbCommand Cmd, List<string> Cols, int KeyIndex)>(StringComparer.OrdinalIgnoreCase);
                    var pkColCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        // Use a single batch timestamp to reduce DateTime.UtcNow calls
                        var nowUtc = _clock.UtcNow;
                        // caches declared above

                        Func<string, Task<HashSet<string>>> getColsAsync = async (table) =>
                        {
                            if (!tableColsCache.TryGetValue(table, out var cols))
                            {
                                cols = await GetTableColumnsAsync(connection, table);
                                tableColsCache[table] = cols;
                            }
                            return cols;
                        };

                        Func<string, Task<string>> getPkColAsync = async (table) =>
                        {
                            if (pkColCache.TryGetValue(table, out var pk)) return pk;
                            pk = await GetPrimaryKeyColumnAsync(connection, table) ?? "ID";
                            pkColCache[table] = pk;
                            return pk;
                        };

                        // No RowGuid usage anymore; rely on primary key

                        // INSERTS
                        foreach (var entity in toAdd)
                        {
                            var cols = await getColsAsync(entity.TableName);
                            var lastModCol = cols.Contains(_syncConfig.LastModifiedColumn, StringComparer.OrdinalIgnoreCase) ? _syncConfig.LastModifiedColumn : (cols.Contains("LastModified", StringComparer.OrdinalIgnoreCase) ? "LastModified" : null);
                            var isDeletedCol = cols.Contains(_syncConfig.IsDeletedColumn, StringComparer.OrdinalIgnoreCase) ? _syncConfig.IsDeletedColumn : (cols.Contains("DeleteDate", StringComparer.OrdinalIgnoreCase) ? "DeleteDate" : null);

                            if (lastModCol != null)
                                entity.Properties[lastModCol] = nowUtc;
                            if (isDeletedCol != null)
                            {
                                if (isDeletedCol.Equals(_syncConfig.IsDeletedColumn, StringComparison.OrdinalIgnoreCase))
                                    entity.Properties[isDeletedCol] = false;
                                else if (isDeletedCol.Equals("DeleteDate", StringComparison.OrdinalIgnoreCase))
                                    entity.Properties[isDeletedCol] = DBNull.Value;
                            }

                            // CRC on INSERT for T_Data_Ambre when CRC column exists
                            bool isAmbreInsert = string.Equals(entity.TableName, "T_Data_Ambre", StringComparison.OrdinalIgnoreCase);
                            bool hasCrcInsert = isAmbreInsert && cols.Contains("CRC", StringComparer.OrdinalIgnoreCase);
                            if (hasCrcInsert)
                            {
                                var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                {
                                    // Exclude tech columns from CRC
                                    await getPkColAsync(entity.TableName),
                                    "CRC",
                                    lastModCol ?? string.Empty,
                                    _syncConfig.IsDeletedColumn,
                                    "DeleteDate",
                                    "CreationDate",
                                    "ModifiedBy",
                                    "Version"
                                };
                                var orderedCols = cols.Where(c => !exclude.Contains(c)).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                                var crcVal = (int)ComputeCrc32ForEntity(entity, orderedCols);
                                entity.Properties["CRC"] = crcVal;
                            }

                            var validCols = entity.Properties.Keys.Where(k => cols.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();

                            if (validCols.Count == 0) continue;
                            var sig = $"{entity.TableName}||{string.Join("|", validCols)}";
                            if (!insertCmdCache.TryGetValue(sig, out var tup))
                            {
                                var colList = string.Join(", ", validCols.Select(c => $"[{c}]"));
                                var paramList = string.Join(", ", validCols.Select((c, i) => $"@p{i}"));
                                var sql = $"INSERT INTO [{entity.TableName}] ({colList}) VALUES ({paramList})";
                                var cmd = new OleDbCommand(sql, connection, tx);
                                var typeMap = colTypeCache.TryGetValue(entity.TableName, out var tm)
                                    ? tm
                                    : (colTypeCache[entity.TableName] = await OleDbSchemaHelper.GetColumnTypesAsync(connection, entity.TableName));
                                for (int i = 0; i < validCols.Count; i++)
                                {
                                    var colName = validCols[i];
                                    if (!typeMap.TryGetValue(colName, out var t))
                                    {
                                        t = OleDbSchemaHelper.InferOleDbTypeFromValue(entity.Properties[colName]);
                                    }
                                    var p = new OleDbParameter($"@p{i}", t) { Value = DBNull.Value };
                                    cmd.Parameters.Add(p);
                                }

                                insertCmdCache[sig] = (cmd, validCols.ToList());
                                tup = insertCmdCache[sig];
                            }
                            // Set parameter values for this row
                            for (int i = 0; i < tup.Cols.Count; i++)
                            {
                                var p = tup.Cmd.Parameters[i];
                                p.Value = OleDbSchemaHelper.CoerceValueForOleDb(entity.Properties[tup.Cols[i]], p.OleDbType);
                            }
                            // Retry on transient Access lock errors (e.g., 3218/3260)
                            {
                                int attempts = 0;
                                while (true)
                                {
                                    try
                                    {
                                        await tup.Cmd.ExecuteNonQueryAsync();
                                        break;
                                    }
                                    catch (OleDbException ex) when (IsAccessLockException(ex) && attempts < 4)
                                    {
                                        attempts++;
                                        await Task.Delay(100 * attempts);
                                    }
                                }
                            }
                            // Determine PK value for change logging: prefer provided PK else fetch last identity
                            var pkColumn = await getPkColAsync(entity.TableName);
                            object keyVal = null;
                            if (entity.Properties.ContainsKey(pkColumn) && entity.Properties[pkColumn] != null)
                            {
                                keyVal = entity.Properties[pkColumn];
                            }
                            else
                            {
                                using (var idCmd = new OleDbCommand("SELECT @@IDENTITY", connection, tx))
                                {
                                    keyVal = await idCmd.ExecuteScalarAsync();
                                }
                            }
                            string chKey = keyVal?.ToString();
                            if (!suppressChangeLog)
                                changeTuples.Add((entity.TableName, chKey, "INSERT"));
                        }

                        // Helper to format a key literal for IN clauses (Access SQL)
                        string FormatKeyLiteral(object key)
                        {
                            if (key == null || key == DBNull.Value) return null; // skip NULLs in IN
                            switch (Type.GetTypeCode(key.GetType()))
                            {
                                case TypeCode.Byte:
                                case TypeCode.SByte:
                                case TypeCode.Int16:
                                case TypeCode.UInt16:
                                case TypeCode.Int32:
                                case TypeCode.UInt32:
                                case TypeCode.Int64:
                                case TypeCode.UInt64:
                                case TypeCode.Decimal:
                                case TypeCode.Double:
                                case TypeCode.Single:
                                    return Convert.ToString(key, CultureInfo.InvariantCulture);
                                case TypeCode.Boolean:
                                    return ((bool)key) ? "1" : "0";
                                case TypeCode.DateTime:
                                    // Use #...# for dates in Access, but PKs should rarely be dates; fall back to string
                                    var ds = ((DateTime)key).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                                    return $"#{ds}#";
                                default:
                                    var s = Convert.ToString(key, CultureInfo.InvariantCulture);
                                    s = (s ?? string.Empty).Replace("'", "''");
                                    return $"'{s}'";
                            }
                        }

                        // Cache business columns per table for CRC computation
                        var businessColsCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                        // Prefetch CRCs per table for all keys in toUpdate to avoid per-row SELECTs
                        var dbCrcCachePerTable = new Dictionary<string, Dictionary<string, int?>>(StringComparer.OrdinalIgnoreCase);
                        if (toUpdate.Count > 0)
                        {
                            var tables = toUpdate.Select(e => e.TableName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            foreach (var tbl in tables)
                            {
                                var colsTbl = await getColsAsync(tbl);
                                if (!(colsTbl.Contains("CRC", StringComparer.OrdinalIgnoreCase))) continue; // only relevant when table has CRC
                                var pkTbl = await getPkColAsync(tbl);
                                // Collect keys present in this batch
                                var keys = toUpdate.Where(e => string.Equals(e.TableName, tbl, StringComparison.OrdinalIgnoreCase))
                                                   .Select(e => e.Properties.ContainsKey(pkTbl) ? e.Properties[pkTbl] : null)
                                                   .Where(k => k != null && k != DBNull.Value)
                                                   .ToList();
                                if (keys.Count == 0) continue;
                                var keyLiterals = keys.Select(k => FormatKeyLiteral(k)).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                                var map = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                                const int chunkSize = 200;
                                for (int i = 0; i < keyLiterals.Count; i += chunkSize)
                                {
                                    var chunk = keyLiterals.Skip(i).Take(chunkSize).ToList();
                                    var inList = string.Join(",", chunk);
                                    var sql = $"SELECT [{pkTbl}] AS K, [CRC] FROM [{tbl}] WHERE [{pkTbl}] IN ({inList})";
                                    using (var cmd = new OleDbCommand(sql, connection, tx))
                                    using (var reader = await cmd.ExecuteReaderAsync())
                                    {
                                        while (await reader.ReadAsync())
                                        {
                                            var k = reader["K"];
                                            var kStr = Convert.ToString(k, CultureInfo.InvariantCulture);
                                            int? cval = reader.IsDBNull(reader.GetOrdinal("CRC")) ? (int?)null : Convert.ToInt32(reader["CRC"], CultureInfo.InvariantCulture);
                                            map[kStr] = cval;
                                        }
                                    }
                                }
                                dbCrcCachePerTable[tbl] = map;
                            }
                        }

                        // UPDATES
                        foreach (var entity in toUpdate)
                        {
                            var cols = await getColsAsync(entity.TableName);
                            var lastModCol = cols.Contains(_syncConfig.LastModifiedColumn, StringComparer.OrdinalIgnoreCase) ? _syncConfig.LastModifiedColumn : (cols.Contains("LastModified", StringComparer.OrdinalIgnoreCase) ? "LastModified" : null);
                            if (lastModCol != null)
                                entity.Properties[lastModCol] = nowUtc;

                            var pkColumn = await getPkColAsync(entity.TableName);
                            var updatable = entity.Properties.Keys.Where(k => !string.Equals(k, pkColumn, StringComparison.OrdinalIgnoreCase) && cols.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                            if (updatable.Count == 0) continue;

                            // Determine key
                            string keyColumn = pkColumn;
                            if (!entity.Properties.ContainsKey(keyColumn))
                                throw new InvalidOperationException($"Valeur de clé introuvable pour la table {entity.TableName} (colonne {keyColumn})");
                            object keyValue = entity.Properties[keyColumn];

                            // If table is T_Data_Ambre and CRC column exists, compute CRC across business fields
                            bool isAmbre = string.Equals(entity.TableName, "T_Data_Ambre", StringComparison.OrdinalIgnoreCase);
                            bool hasCrc = isAmbre && cols.Contains("CRC", StringComparer.OrdinalIgnoreCase);
                            int? crcValue = null;
                            if (hasCrc)
                            {
                                // Cache business column order per table
                                if (!businessColsCache.TryGetValue(entity.TableName, out var orderedCols))
                                {
                                    var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        pkColumn,
                                        "CRC",
                                        lastModCol ?? string.Empty,
                                        _syncConfig.IsDeletedColumn,
                                        "DeleteDate",
                                        "CreationDate",
                                        "ModifiedBy",
                                        "Version"
                                    };
                                    orderedCols = cols.Where(c => !exclude.Contains(c)).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                                    businessColsCache[entity.TableName] = orderedCols;
                                }
                                crcValue = (int)ComputeCrc32ForEntity(entity, orderedCols);
                                // Ensure CRC is part of SET
                                if (!updatable.Contains("CRC", StringComparer.OrdinalIgnoreCase))
                                    updatable.Add("CRC");
                                entity.Properties["CRC"] = crcValue.Value;
                            }

                            // Fast-path using preloaded DB CRC map
                            if (hasCrc && dbCrcCachePerTable.TryGetValue(entity.TableName, out var tableMap))
                            {
                                var keyStr = Convert.ToString(keyValue, CultureInfo.InvariantCulture);
                                if (keyStr != null && tableMap.TryGetValue(keyStr, out var dbCrc) && crcValue.HasValue && dbCrc.HasValue && dbCrc.Value == crcValue.Value)
                                {
                                    // No business change -> skip this row
                                    continue;
                                }
                            }

                            var upSig = $"{entity.TableName}||{string.Join("|", updatable)}||{keyColumn}||{(hasCrc ? "withCrc" : "noCrc")}";
                            if (!updateCmdCache.TryGetValue(upSig, out var upd))
                            {
                                var setList = string.Join(", ", updatable.Select((c, i) => $"[{c}] = @p{i}"));
                                var sql = hasCrc
                                    ? $"UPDATE [{entity.TableName}] SET {setList} WHERE [{keyColumn}] = @key AND ([CRC] <> @crc OR [CRC] IS NULL OR @crc IS NULL)"
                                    : $"UPDATE [{entity.TableName}] SET {setList} WHERE [{keyColumn}] = @key";
                                var cmd = new OleDbCommand(sql, connection, tx);
                                var typeMap = colTypeCache.TryGetValue(entity.TableName, out var tm)
                                    ? tm
                                    : (colTypeCache[entity.TableName] = await OleDbSchemaHelper.GetColumnTypesAsync(connection, entity.TableName));
                                for (int i = 0; i < updatable.Count; i++)
                                {
                                    var colName = updatable[i];
                                    if (!typeMap.TryGetValue(colName, out var t)) t = OleDbSchemaHelper.InferOleDbTypeFromValue(entity.Properties.ContainsKey(colName) ? entity.Properties[colName] : null);
                                    var p = new OleDbParameter($"@p{i}", t) { Value = DBNull.Value };
                                    cmd.Parameters.Add(p);
                                }
                                // key parameter at the end (typed)
                                {
                                    var keyType = typeMap.TryGetValue(keyColumn, out var kt) ? kt : OleDbSchemaHelper.InferOleDbTypeFromValue(keyValue);
                                    var pKey = new OleDbParameter("@key", keyType) { Value = DBNull.Value };
                                    cmd.Parameters.Add(pKey);
                                }
                                if (hasCrc)
                                {
                                    var crcType = typeMap.TryGetValue("CRC", out var ct) ? ct : OleDbType.Integer;
                                    var pCrc = new OleDbParameter("@crc", crcType) { Value = DBNull.Value };
                                    cmd.Parameters.Add(pCrc);
                                }
                                updateCmdCache[upSig] = (cmd, updatable.ToList(), updatable.Count);
                                upd = updateCmdCache[upSig];
                            }
                            // Set parameters for this row
                            for (int i = 0; i < upd.Cols.Count; i++)
                            {
                                var p = upd.Cmd.Parameters[i];
                                p.Value = OleDbSchemaHelper.CoerceValueForOleDb(entity.Properties[upd.Cols[i]], p.OleDbType);
                            }
                            {
                                var pKey = upd.Cmd.Parameters[upd.KeyIndex];
                                pKey.Value = OleDbSchemaHelper.CoerceValueForOleDb(keyValue, pKey.OleDbType);
                            }
                            if (hasCrc)
                            {
                                // last parameter is @crc
                                var pCrc = upd.Cmd.Parameters[upd.KeyIndex + 1];
                                pCrc.Value = crcValue.HasValue ? (object)OleDbSchemaHelper.CoerceValueForOleDb(crcValue.Value, pCrc.OleDbType) : DBNull.Value;
                            }
                            int affected;
                            // Retry on transient Access lock errors (e.g., 3218/3260)
                            {
                                int attempts = 0;
                                while (true)
                                {
                                    try
                                    {
                                        affected = await upd.Cmd.ExecuteNonQueryAsync();
                                        break;
                                    }
                                    catch (OleDbException ex) when (IsAccessLockException(ex) && attempts < 4)
                                    {
                                        attempts++;
                                        await Task.Delay(100 * attempts);
                                    }
                                }
                            }
                            if (affected > 0 && !suppressChangeLog)
                            {
                                string chKey = keyValue?.ToString();
                                // Encode the exact columns updated so the sync push constructs a partial update payload
                                var opColumns = updatable ?? new List<string>();
                                var opType = $"UPDATE({string.Join(",", opColumns)})";
                                changeTuples.Add((entity.TableName, chKey, opType));
                            }
                        }

                        // ARCHIVES (logical delete)
                        foreach (var entity in toArchive)
                        {
                            var cols = await getColsAsync(entity.TableName);
                            var lastModCol = cols.Contains(_syncConfig.LastModifiedColumn, StringComparer.OrdinalIgnoreCase) ? _syncConfig.LastModifiedColumn : (cols.Contains("LastModified", StringComparer.OrdinalIgnoreCase) ? "LastModified" : null);
                            var hasIsDeleted = cols.Contains(_syncConfig.IsDeletedColumn, StringComparer.OrdinalIgnoreCase);
                            var hasDeleteDate = cols.Contains("DeleteDate", StringComparer.OrdinalIgnoreCase);

                            // Determine key
                            string keyColumn = await GetPrimaryKeyColumnAsync(connection, entity.TableName) ?? "ID";
                            if (!entity.Properties.ContainsKey(keyColumn))
                                throw new InvalidOperationException($"Valeur de clé introuvable pour la table {entity.TableName} (colonne {keyColumn})");
                            object keyValue = entity.Properties[keyColumn];

                            if (hasIsDeleted || hasDeleteDate)
                            {
                                // Reuse prepared soft-delete command per table to reduce command creation overhead
                                if (!archiveCmdCache.TryGetValue(entity.TableName, out var cmd))
                                {
                                    var setParts = new List<string>();
                                    var paramNames = new List<string>();
                                    setParts.Add($"[{_syncConfig.IsDeletedColumn}] = true");
                                    if (hasDeleteDate) { setParts.Add("[DeleteDate] = @p0"); paramNames.Add("@p0"); }
                                    if (lastModCol != null) { setParts.Add($"[{lastModCol}] = @p1"); paramNames.Add("@p1"); }
                                    var sql = $"UPDATE [{entity.TableName}] SET {string.Join(", ", setParts)} WHERE [{keyColumn}] = @key";
                                    cmd = new OleDbCommand(sql, connection, tx);
                                    var typeMap = colTypeCache.TryGetValue(entity.TableName, out var tm)
                                        ? tm
                                        : (colTypeCache[entity.TableName] = await OleDbSchemaHelper.GetColumnTypesAsync(connection, entity.TableName));
                                    // Prepare parameters in fixed order if present (typed)
                                    if (hasDeleteDate)
                                    {
                                        var t = typeMap.TryGetValue("DeleteDate", out var dt) ? dt : OleDbType.Date;
                                        var p0 = new OleDbParameter("@p0", t) { Value = OleDbSchemaHelper.CoerceValueForOleDb(nowUtc, t) };
                                        cmd.Parameters.Add(p0);
                                    }
                                    if (lastModCol != null)
                                    {
                                        var t = typeMap.TryGetValue(lastModCol, out var lt) ? lt : OleDbType.Date;
                                        var p1 = new OleDbParameter("@p1", t) { Value = OleDbSchemaHelper.CoerceValueForOleDb(nowUtc, t) };
                                        cmd.Parameters.Add(p1);
                                    }
                                    {
                                        var keyType = typeMap.TryGetValue(keyColumn, out var kt) ? kt : OleDbSchemaHelper.InferOleDbTypeFromValue(keyValue);
                                        var pk = new OleDbParameter("@key", keyType) { Value = DBNull.Value };
                                        cmd.Parameters.Add(pk);
                                    }
                                    archiveCmdCache[entity.TableName] = cmd;
                                }
                                // Update parameter values per row
                                int baseIndex = 0;
                                if (hasDeleteDate)
                                {
                                    var p0 = cmd.Parameters[baseIndex++];
                                    p0.Value = OleDbSchemaHelper.CoerceValueForOleDb(nowUtc, p0.OleDbType);
                                }
                                if (lastModCol != null)
                                {
                                    var p1 = cmd.Parameters[baseIndex++];
                                    p1.Value = OleDbSchemaHelper.CoerceValueForOleDb(nowUtc, p1.OleDbType);
                                }
                                var pkParam = cmd.Parameters[baseIndex];
                                pkParam.Value = OleDbSchemaHelper.CoerceValueForOleDb(keyValue, pkParam.OleDbType); // @key
                                await cmd.ExecuteNonQueryAsync();
                            }
                            else
                            {
                                var sql = $"DELETE FROM [{entity.TableName}] WHERE [{keyColumn}] = @key";
                                using (var cmd = new OleDbCommand(sql, connection, tx))
                                {
                                    var typeMap = colTypeCache.TryGetValue(entity.TableName, out var tm)
                                        ? tm
                                        : (colTypeCache[entity.TableName] = await OleDbSchemaHelper.GetColumnTypesAsync(connection, entity.TableName));
                                    var keyType = typeMap.TryGetValue(keyColumn, out var kt) ? kt : OleDbSchemaHelper.InferOleDbTypeFromValue(keyValue);
                                    var p = new OleDbParameter("@key", keyType) { Value = OleDbSchemaHelper.CoerceValueForOleDb(keyValue, keyType) };
                                    cmd.Parameters.Add(p);
                                    await cmd.ExecuteNonQueryAsync();
                                }
                            }
                            string chKey = keyValue?.ToString();
                            if (!suppressChangeLog)
                                changeTuples.Add((entity.TableName, chKey, "DELETE"));
                        }

                        tx.Commit();

                        // Record change logs (local or control DB depending on flag)
                        if (!suppressChangeLog && changeTuples.Count > 0)
                        {
                            var tracker = new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(_currentCountryId));
                            await tracker.RecordChangesAsync(changeTuples);
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                    finally
                    {
                        // Dispose cached commands
                        foreach (var kv in archiveCmdCache)
                        {
                            try { kv.Value?.Dispose(); } catch { }
                        }
                        foreach (var kv in insertCmdCache)
                        {
                            try { kv.Value.Cmd?.Dispose(); } catch { }
                        }
                        foreach (var kv in updateCmdCache)
                        {
                            try { kv.Value.Cmd?.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Supprime une entité (avec session de change-log optionnelle)
        /// </summary>
        public async Task<bool> DeleteEntityAsync(string identifier, Entity entity, OfflineFirstAccess.ChangeTracking.IChangeLogSession changeLogSession)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (string.IsNullOrWhiteSpace(entity.TableName)) throw new ArgumentException("Entity.TableName is required");
            EnsureInitialized();

            using (var connection = new OleDbConnection(GetLocalConnectionString()))
            {
                await connection.OpenAsync();
                var tableCols = await GetTableColumnsAsync(connection, entity.TableName);
                var lastModCol = tableCols.Contains(_syncConfig.LastModifiedColumn, StringComparer.OrdinalIgnoreCase) ? _syncConfig.LastModifiedColumn : (tableCols.Contains("LastModified", StringComparer.OrdinalIgnoreCase) ? "LastModified" : null);
                var hasIsDeleted = tableCols.Contains(_syncConfig.IsDeletedColumn, StringComparer.OrdinalIgnoreCase);
                var hasDeleteDate = tableCols.Contains("DeleteDate", StringComparer.OrdinalIgnoreCase);

                // Determine key
                string keyColumn = await GetPrimaryKeyColumnAsync(connection, entity.TableName) ?? "ID";
                if (!entity.Properties.ContainsKey(keyColumn))
                    throw new InvalidOperationException($"Valeur de clé introuvable pour la table {entity.TableName} (colonne {keyColumn})");
                object keyValue = entity.Properties[keyColumn];

                if (hasIsDeleted || hasDeleteDate)
                {
                    var setParts = new List<string>();
                    var parameters = new List<object>();
                    if (hasIsDeleted) { setParts.Add($"[{_syncConfig.IsDeletedColumn}] = true"); }
                    if (hasDeleteDate) { setParts.Add("[DeleteDate] = @p0"); parameters.Add(_clock.UtcNow); }
                    if (lastModCol != null) { setParts.Add($"[{lastModCol}] = @p1"); parameters.Add(_clock.UtcNow); }
                    var sql = $"UPDATE [{entity.TableName}] SET {string.Join(", ", setParts)} WHERE [{keyColumn}] = @key";
                    using (var cmd = new OleDbCommand(sql, connection))
                    {
                        var typeMap = await OleDbSchemaHelper.GetColumnTypesAsync(connection, entity.TableName);
                        if (hasDeleteDate)
                        {
                            var t = typeMap.TryGetValue("DeleteDate", out var dt) ? dt : OleDbType.Date;
                            var p0 = new OleDbParameter("@p0", t) { Value = OleDbSchemaHelper.CoerceValueForOleDb(_clock.UtcNow, t) };
                            cmd.Parameters.Add(p0);
                        }
                        if (lastModCol != null)
                        {
                            var t = typeMap.TryGetValue(lastModCol, out var lt) ? lt : OleDbType.Date;
                            var p1 = new OleDbParameter("@p1", t) { Value = OleDbSchemaHelper.CoerceValueForOleDb(_clock.UtcNow, t) };
                            cmd.Parameters.Add(p1);
                        }
                        var keyType = typeMap.TryGetValue(keyColumn, out var kt) ? kt : OleDbSchemaHelper.InferOleDbTypeFromValue(keyValue);
                        var pKey = new OleDbParameter("@key", keyType) { Value = OleDbSchemaHelper.CoerceValueForOleDb(keyValue, keyType) };
                        cmd.Parameters.Add(pKey);
                        var affected = await cmd.ExecuteNonQueryAsync();
                        string changeKey = keyValue?.ToString();
                        if (changeLogSession != null)
                            await changeLogSession.AddAsync(entity.TableName, changeKey, "DELETE");
                        else
                            await new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(_currentCountryId)).RecordChangeAsync(entity.TableName, changeKey, "DELETE");
                        return affected > 0;
                    }
                }
                else
                {
                    // Hard delete fallback
                    var sql = $"DELETE FROM [{entity.TableName}] WHERE [{keyColumn}] = @key";
                    using (var cmd = new OleDbCommand(sql, connection))
                    {
                        var typeMap = await OleDbSchemaHelper.GetColumnTypesAsync(connection, entity.TableName);
                        var keyType = typeMap.TryGetValue(keyColumn, out var kt) ? kt : OleDbSchemaHelper.InferOleDbTypeFromValue(keyValue);
                        var pKey = new OleDbParameter("@key", keyType) { Value = OleDbSchemaHelper.CoerceValueForOleDb(keyValue, keyType) };
                        cmd.Parameters.Add(pKey);
                        var affected = await cmd.ExecuteNonQueryAsync();
                        string changeKey = keyValue?.ToString();
                        if (changeLogSession != null)
                            await changeLogSession.AddAsync(entity.TableName, changeKey, "DELETE");
                        else
                            await new OfflineFirstAccess.ChangeTracking.ChangeTracker(GetChangeLogConnectionString(_currentCountryId)).RecordChangeAsync(entity.TableName, changeKey, "DELETE");
                        return affected > 0;
                    }
                }
            }
        }
    }
}
