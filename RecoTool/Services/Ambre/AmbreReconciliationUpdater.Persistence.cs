using OfflineFirstAccess.Helpers;
using RecoTool.Helpers;
using RecoTool.Infrastructure;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RecoTool.Services.Ambre
{
    /// <summary>
    /// Persistence layer for <see cref="AmbreReconciliationUpdater"/>:
    /// materializes reconciliation changes into the country Access DB
    /// (INSERT new rows, archive/unarchive existing ones, back-fill DWINGS references)
    /// plus utility queries over the local country database.
    /// </summary>
    public partial class AmbreReconciliationUpdater
    {
        private async Task<List<string>> GetUnlinkedReconciliationIdsAsync(string countryId)
        {
            try
            {
                var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
                var sql = $"SELECT [{Schema.Columns.Reconciliation.ID}] FROM [{Schema.Tables.T_Reconciliation}] WHERE [{Schema.Columns.Reconciliation.DeleteDate}] IS NULL AND (" +
                          $"([{Schema.Columns.Reconciliation.DWINGS_InvoiceID}] IS NULL OR [{Schema.Columns.Reconciliation.DWINGS_InvoiceID}] = '') OR " +
                          $"([{Schema.Columns.Reconciliation.DWINGS_BGPMT}] IS NULL OR [{Schema.Columns.Reconciliation.DWINGS_BGPMT}] = '') OR " +
                          $"([{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}] IS NULL OR [{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}] = '')" +
                          ")";

                var ids = await OleDbAsyncExecutor.RunWithConnectionAsync(connectionString, conn =>
                {
                    var collected = new List<string>();
                    using (var cmd = new OleDbCommand(sql, conn))
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            var id = rdr.IsDBNull(0) ? null : Convert.ToString(rdr.GetValue(0));
                            if (!string.IsNullOrWhiteSpace(id)) collected.Add(id);
                        }
                    }
                    return collected;
                }).ConfigureAwait(false);

                return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { }

            return new List<string>();
        }

        private async Task<List<DataAmbre>> LoadAmbreRowsByIdsAsync(string ambreConnectionString, List<string> ids)
        {
            var rows = new List<DataAmbre>();
            if (string.IsNullOrWhiteSpace(ambreConnectionString) || ids == null || ids.Count == 0) return rows;

            const int batchSize = 200;
            for (int start = 0; start < ids.Count; start += batchSize)
            {
                var batch = ids.Skip(start).Take(batchSize).ToList();
                var sb = new StringBuilder();
                sb.Append($"SELECT * FROM {Schema.Tables.T_Data_Ambre} WHERE {Schema.Columns.Ambre.ID} IN (");
                for (int i = 0; i < batch.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("?");
                }
                sb.Append($") AND {Schema.Columns.Ambre.DeleteDate} IS NULL");
                var sql = sb.ToString();

                var batchRows = await OleDbAsyncExecutor.RunWithConnectionAsync(ambreConnectionString, conn =>
                {
                    var result = new List<DataAmbre>();
                    using (var cmd = new OleDbCommand(sql, conn))
                    {
                        foreach (var id in batch)
                            cmd.Parameters.AddWithValue("@ID", id);

                        using (var rdr = cmd.ExecuteReader())
                        {
                            var props = typeof(DataAmbre)
                                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                .Where(p => p.CanWrite)
                                .ToList();

                            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < rdr.FieldCount; i++)
                            {
                                var name = rdr.GetName(i);
                                if (!string.IsNullOrWhiteSpace(name) && !ordinals.ContainsKey(name)) ordinals[name] = i;
                            }

                            while (rdr.Read())
                            {
                                var item = new DataAmbre();
                                foreach (var p in props)
                                {
                                    if (!ordinals.TryGetValue(p.Name, out var idx)) continue;
                                    if (rdr.IsDBNull(idx)) continue;

                                    try
                                    {
                                        var val = rdr.GetValue(idx);
                                        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                                        if (t.IsEnum)
                                        {
                                            var enumVal = Convert.ChangeType(val, Enum.GetUnderlyingType(t), CultureInfo.InvariantCulture);
                                            p.SetValue(item, Enum.ToObject(t, enumVal));
                                        }
                                        else
                                        {
                                            p.SetValue(item, Convert.ChangeType(val, t, CultureInfo.InvariantCulture));
                                        }
                                    }
                                    catch { }
                                }
                                result.Add(item);
                            }
                        }
                    }
                    return result;
                }).ConfigureAwait(false);

                rows.AddRange(batchRows);
            }

            return rows;
        }

        private async Task ApplyReconciliationChangesAsync(
            List<Reconciliation> toInsert,
            List<DataAmbre> toUpdate,
            List<DataAmbre> toArchive,
            string countryId)
        {
            var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);

            await OleDbAsyncExecutor.RunWithConnectionAsync(connectionString, conn =>
            {
                // Unarchive updated records
                if (toUpdate.Any())
                {
                    UnarchiveRecords(conn, toUpdate);
                }

                // Archive deleted records
                if (toArchive.Any())
                {
                    ArchiveRecords(conn, toArchive);
                }

                // Insert new reconciliations
                if (toInsert.Any())
                {
                    InsertReconciliations(conn, toInsert);
                }
                return 0;
            }).ConfigureAwait(false);
        }

        private async Task UpdateDwingsReferencesForUpdatesAsync(
            List<DataAmbre> updatedRecords,
            Country country,
            string countryId)
        {
            try
            {
                if (updatedRecords == null || updatedRecords.Count == 0) return;
                var invoices = await _reconciliationService.GetDwingsInvoicesAsync();
                var dwList = invoices?.ToList() ?? new List<DwingsInvoiceDto>();

                var guarantees = await _reconciliationService.GetDwingsGuaranteesAsync();
                var dwGuaranteeList = guarantees?.ToList();

                // PERF: Build lookups once before resolution loop
                var lookup = new DwingsInvoiceLookup(dwList);
                _dwingsResolver.SetInvoiceLookup(lookup);
                _dwingsResolver.PreBuildLookups(dwList, dwGuaranteeList);

                // PERF: Phase 1 — resolve all references in memory (CPU-bound string matching)
                var resolvedRefs = new List<(string Id, Ambre.DwingsTokens Refs)>();
                foreach (var amb in updatedRecords)
                {
                    if (amb == null || string.IsNullOrWhiteSpace(amb.ID)) continue;
                    bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);
                    var refs = _dwingsResolver.ResolveReferences(amb, isPivot, dwList, dwGuaranteeList);
                    if (refs != null && (!string.IsNullOrWhiteSpace(refs.InvoiceId)
                                      || !string.IsNullOrWhiteSpace(refs.CommissionId)
                                      || !string.IsNullOrWhiteSpace(refs.GuaranteeId)))
                    {
                        resolvedRefs.Add((amb.ID, refs));
                    }
                }

                if (resolvedRefs.Count == 0) return;

                // PERF: Phase 2 — single prepared UPDATE per row (merges 3 separate UPDATEs into 1)
                var connectionString = _offlineFirstService.GetCountryConnectionString(countryId);
                await OleDbAsyncExecutor.RunInTransactionAsync(connectionString, (conn, tx) =>
                {
                    var nowUtc = _clock.UtcNow;

                    using (var cmd = new OleDbCommand(
                        $"UPDATE [{Schema.Tables.T_Reconciliation}] SET " +
                        $"[{Schema.Columns.Reconciliation.DWINGS_InvoiceID}] = IIF(([{Schema.Columns.Reconciliation.DWINGS_InvoiceID}] IS NULL OR [{Schema.Columns.Reconciliation.DWINGS_InvoiceID}] = '') AND ? <> '', ?, [{Schema.Columns.Reconciliation.DWINGS_InvoiceID}]), " +
                        $"[{Schema.Columns.Reconciliation.DWINGS_BGPMT}] = IIF(([{Schema.Columns.Reconciliation.DWINGS_BGPMT}] IS NULL OR [{Schema.Columns.Reconciliation.DWINGS_BGPMT}] = '') AND ? <> '', ?, [{Schema.Columns.Reconciliation.DWINGS_BGPMT}]), " +
                        $"[{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}] = IIF(([{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}] IS NULL OR [{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}] = '') AND ? <> '', ?, [{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}]), " +
                        $"[{Schema.Columns.Reconciliation.LastModified}]=?, [{Schema.Columns.Reconciliation.ModifiedBy}]=? " +
                        $"WHERE [{Schema.Columns.Reconciliation.ID}]=?", conn, tx))
                    {
                        cmd.Parameters.Add("@InvCheck", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@InvVal", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@BgpmtCheck", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@BgpmtVal", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@GuarCheck", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@GuarVal", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@LastModified", OleDbType.Date);
                        cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255);

                        foreach (var item in resolvedRefs)
                        {
                            var inv = item.Refs.InvoiceId ?? string.Empty;
                            var bgp = item.Refs.CommissionId ?? string.Empty;
                            var guar = item.Refs.GuaranteeId ?? string.Empty;

                            cmd.Parameters["@InvCheck"].Value = inv;
                            cmd.Parameters["@InvVal"].Value = inv;
                            cmd.Parameters["@BgpmtCheck"].Value = bgp;
                            cmd.Parameters["@BgpmtVal"].Value = bgp;
                            cmd.Parameters["@GuarCheck"].Value = guar;
                            cmd.Parameters["@GuarVal"].Value = guar;
                            cmd.Parameters["@LastModified"].Value = nowUtc;
                            cmd.Parameters["@ModifiedBy"].Value = _currentUser;
                            cmd.Parameters["@ID"].Value = item.Id;

                            cmd.ExecuteNonQuery();
                        }
                    }

                    LogManager.Info($"[PERF] Backfilled DWINGS refs for {resolvedRefs.Count} records");
                    return 0;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Backfill DWINGS refs failed: {ex.Message}");
            }
        }

        private void UnarchiveRecords(OleDbConnection conn, List<DataAmbre> records)
        {
            var ids = records
                .Select(d => d?.ID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ids.Any()) return;

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var nowUtc = _clock.UtcNow;

                    // OPTIMIZATION: Batch update with IN clause (Access supports up to ~1000 items)
                    const int batchSize = 500;
                    int totalCount = 0;

                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        var batch = ids.Skip(i).Take(batchSize).ToList();
                        var inClause = string.Join(",", batch.Select((_, idx) => $"?"));

                        using (var cmd = new OleDbCommand(
                            $"UPDATE [{Schema.Tables.T_Reconciliation}] SET [{Schema.Columns.Reconciliation.DeleteDate}]=NULL, [{Schema.Columns.Reconciliation.LastModified}]=?, [{Schema.Columns.Reconciliation.ModifiedBy}]=? " +
                            $"WHERE [{Schema.Columns.Reconciliation.ID}] IN ({inClause}) AND [{Schema.Columns.Reconciliation.DeleteDate}] IS NOT NULL", conn, tx))
                        {
                            cmd.Parameters.Add("@LastModified", OleDbType.Date).Value = nowUtc;
                            cmd.Parameters.AddWithValue("@ModifiedBy", _currentUser);
                            foreach (var id in batch)
                            {
                                cmd.Parameters.AddWithValue("@ID", id);
                            }
                            totalCount += cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                    LogManager.Info($"Unarchived {totalCount} reconciliation record(s)");
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private void ArchiveRecords(OleDbConnection conn, List<DataAmbre> records)
        {
            var ids = records
                .Select(d => d?.ID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ids.Any()) return;

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var nowUtc = _clock.UtcNow;

                    // OPTIMIZATION: Batch update with IN clause
                    const int batchSize = 500;
                    int totalCount = 0;

                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        var batch = ids.Skip(i).Take(batchSize).ToList();
                        var inClause = string.Join(",", batch.Select((_, idx) => $"?"));

                        using (var cmd = new OleDbCommand(
                            $"UPDATE [{Schema.Tables.T_Reconciliation}] SET [{Schema.Columns.Reconciliation.DeleteDate}]=?, [{Schema.Columns.Reconciliation.LastModified}]=?, [{Schema.Columns.Reconciliation.ModifiedBy}]=? " +
                            $"WHERE [{Schema.Columns.Reconciliation.ID}] IN ({inClause}) AND [{Schema.Columns.Reconciliation.DeleteDate}] IS NULL", conn, tx))
                        {
                            cmd.Parameters.Add("@DeleteDate", OleDbType.Date).Value = nowUtc;
                            cmd.Parameters.Add("@LastModified", OleDbType.Date).Value = nowUtc;
                            cmd.Parameters.AddWithValue("@ModifiedBy", _currentUser);
                            foreach (var id in batch)
                            {
                                cmd.Parameters.AddWithValue("@ID", id);
                            }
                            totalCount += cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                    LogManager.Info($"Archived {totalCount} reconciliation record(s)");
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private void InsertReconciliations(OleDbConnection conn, List<Reconciliation> reconciliations)
        {
            // Get existing IDs to ensure insert-only
            var existingIds = GetExistingIds(conn, reconciliations.Select(r => r.ID).ToList());
            var toInsert = reconciliations.Where(r => !existingIds.Contains(r.ID)).ToList();
            if (toInsert.Count == 0) return;

            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    int insertedCount = 0;

                    // PERF: Create a single prepared command and reuse it for all inserts
                    // (avoids 20k+ OleDbCommand allocations + parameter setup)
                    using (var cmd = new OleDbCommand($@"INSERT INTO [{Schema.Tables.T_Reconciliation}] (
                        [{Schema.Columns.Reconciliation.ID}],[{Schema.Columns.Reconciliation.DWINGS_GuaranteeID}],[{Schema.Columns.Reconciliation.DWINGS_InvoiceID}],[{Schema.Columns.Reconciliation.DWINGS_BGPMT}],
                        [{Schema.Columns.Reconciliation.Action}],[{Schema.Columns.Reconciliation.ActionStatus}],[{Schema.Columns.Reconciliation.ActionDate}],[{Schema.Columns.Reconciliation.Comments}],[{Schema.Columns.Reconciliation.InternalInvoiceReference}],[{Schema.Columns.Reconciliation.FirstClaimDate}],[{Schema.Columns.Reconciliation.LastClaimDate}],
                        [{Schema.Columns.Reconciliation.ToRemind}],[{Schema.Columns.Reconciliation.ToRemindDate}],[{Schema.Columns.Reconciliation.ACK}],[{Schema.Columns.Reconciliation.SwiftCode}],[{Schema.Columns.Reconciliation.PaymentReference}],[{Schema.Columns.Reconciliation.MbawData}],[{Schema.Columns.Reconciliation.SpiritData}],[{Schema.Columns.Reconciliation.KPI}],
                        [{Schema.Columns.Reconciliation.IncidentType}],[{Schema.Columns.Reconciliation.RiskyItem}],[{Schema.Columns.Reconciliation.ReasonNonRisky}],[{Schema.Columns.Reconciliation.CreationDate}],[{Schema.Columns.Reconciliation.ModifiedBy}],[{Schema.Columns.Reconciliation.LastModified}]
                    ) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", conn, tx))
                    {
                        // Pre-create parameters once with explicit types
                        cmd.Parameters.Add("@ID", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@DWINGS_GuaranteeID", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@DWINGS_InvoiceID", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@DWINGS_BGPMT", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@Action", OleDbType.Integer);
                        cmd.Parameters.Add("@ActionStatus", OleDbType.Boolean);
                        cmd.Parameters.Add("@ActionDate", OleDbType.Date);
                        cmd.Parameters.Add("@Comments", OleDbType.LongVarWChar, int.MaxValue);
                        cmd.Parameters.Add("@InternalInvoiceReference", OleDbType.VarWChar, 255);
                        
                        cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date);
                        cmd.Parameters.Add("@LastClaimDate", OleDbType.Date);
                        cmd.Parameters.Add("@ToRemind", OleDbType.Boolean);
                        cmd.Parameters.Add("@ToRemindDate", OleDbType.Date);
                        cmd.Parameters.Add("@ACK", OleDbType.Boolean);
                        
                        cmd.Parameters.Add("@SwiftCode", OleDbType.VarWChar, 255);
                        cmd.Parameters.Add("@PaymentReference", OleDbType.VarWChar, 255);
                        // Long text fields
                        var pMbaw = cmd.Parameters.Add("@MbawData", OleDbType.LongVarWChar, int.MaxValue);
                        pMbaw.Value = DBNull.Value;
                        var pSpirit = cmd.Parameters.Add("@SpiritData", OleDbType.LongVarWChar, int.MaxValue);
                        pSpirit.Value = DBNull.Value;
                        
                        cmd.Parameters.Add("@KPI", OleDbType.Integer);
                        cmd.Parameters.Add("@IncidentType", OleDbType.Integer);
                        cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean);
                        cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer);
                        cmd.Parameters.Add("@CreationDate", OleDbType.Date);
                        
                        cmd.Parameters.Add("@ModifiedBy", OleDbType.VarWChar, 255);
                        
                        cmd.Parameters.Add("@LastModified", OleDbType.Date);

                        foreach (var rec in toInsert)
                        {
                            cmd.Parameters["@ID"].Value = (object)rec.ID ?? DBNull.Value;
                            cmd.Parameters["@DWINGS_GuaranteeID"].Value = (object)rec.DWINGS_GuaranteeID ?? DBNull.Value;
                            cmd.Parameters["@DWINGS_InvoiceID"].Value = (object)rec.DWINGS_InvoiceID ?? DBNull.Value;
                            cmd.Parameters["@DWINGS_BGPMT"].Value = (object)rec.DWINGS_BGPMT ?? DBNull.Value;
                            
                            cmd.Parameters["@Action"].Value = rec.Action.HasValue ? (object)rec.Action.Value : DBNull.Value;
                            cmd.Parameters["@ActionStatus"].Value = rec.ActionStatus.HasValue ? (object)rec.ActionStatus.Value : DBNull.Value;
                            cmd.Parameters["@ActionDate"].Value = rec.ActionDate.HasValue ? (object)rec.ActionDate.Value : DBNull.Value;
                            cmd.Parameters["@Comments"].Value = (object)rec.Comments ?? DBNull.Value;
                            cmd.Parameters["@InternalInvoiceReference"].Value = (object)rec.InternalInvoiceReference ?? DBNull.Value;
                            
                            cmd.Parameters["@FirstClaimDate"].Value = 
                                rec.FirstClaimDate.HasValue ? (object)rec.FirstClaimDate.Value : DBNull.Value;
                            cmd.Parameters["@LastClaimDate"].Value = 
                                rec.LastClaimDate.HasValue ? (object)rec.LastClaimDate.Value : DBNull.Value;
                            cmd.Parameters["@ToRemind"].Value = rec.ToRemind;
                            cmd.Parameters["@ToRemindDate"].Value = 
                                rec.ToRemindDate.HasValue ? (object)rec.ToRemindDate.Value : DBNull.Value;
                            cmd.Parameters["@ACK"].Value = rec.ACK;
                            
                            cmd.Parameters["@SwiftCode"].Value = (object)rec.SwiftCode ?? DBNull.Value;
                            cmd.Parameters["@PaymentReference"].Value = (object)rec.PaymentReference ?? DBNull.Value;
                            pMbaw.Value = rec.MbawData ?? (object)DBNull.Value;
                            pSpirit.Value = rec.SpiritData ?? (object)DBNull.Value;
                            
                            cmd.Parameters["@KPI"].Value = 
                                rec.KPI.HasValue ? (object)rec.KPI.Value : DBNull.Value;
                            cmd.Parameters["@IncidentType"].Value = 
                                rec.IncidentType.HasValue ? (object)rec.IncidentType.Value : DBNull.Value;
                            cmd.Parameters["@RiskyItem"].Value = 
                                rec.RiskyItem.HasValue ? (object)rec.RiskyItem.Value : DBNull.Value;
                            cmd.Parameters["@ReasonNonRisky"].Value = 
                                rec.ReasonNonRisky.HasValue ? (object)rec.ReasonNonRisky.Value : DBNull.Value;
                            cmd.Parameters["@CreationDate"].Value = 
                                rec.CreationDate.HasValue ? (object)rec.CreationDate.Value : DBNull.Value;
                            
                            cmd.Parameters["@ModifiedBy"].Value = (object)rec.ModifiedBy ?? DBNull.Value;
                            
                            cmd.Parameters["@LastModified"].Value =
                                rec.LastModified.HasValue ? (object)rec.LastModified.Value : DBNull.Value;

                            insertedCount += cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                    LogManager.Info($"Inserted {insertedCount} new reconciliation record(s)");
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private HashSet<string> GetExistingIds(OleDbConnection conn, List<string> ids)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!ids.Any()) return existing;

            const int chunkSize = 500;
            for (int i = 0; i < ids.Count; i += chunkSize)
            {
                var chunk = ids.Skip(i).Take(chunkSize).ToList();
                var placeholders = string.Join(",", Enumerable.Repeat("?", chunk.Count));

                using (var cmd = new OleDbCommand(
                    $"SELECT [{Schema.Columns.Reconciliation.ID}] FROM [{Schema.Tables.T_Reconciliation}] WHERE [{Schema.Columns.Reconciliation.ID}] IN ({placeholders})", conn))
                {
                    foreach (var id in chunk)
                        cmd.Parameters.AddWithValue("@ID", id);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var id = reader[0]?.ToString();
                            if (!string.IsNullOrWhiteSpace(id))
                                existing.Add(id);
                        }
                    }
                }
            }

            return existing;
        }
    }
}
