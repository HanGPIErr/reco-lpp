using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecoTool.Domain.Repositories;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Models;

namespace RecoTool.Infrastructure.Repositories
{
    /// <summary>
    /// OleDb-backed implementation of <see cref="IDataAmbreRepository"/>.
    ///
    /// <para>
    /// Each country's Ambre rows live in a per-country <c>.accdb</c> file. The repository
    /// receives a <c>Func&lt;string, string&gt;</c> that maps a country id to the matching
    /// OleDb connection string, so callers can plug in any resolution strategy
    /// (e.g. <c>IOfflineFirstService.GetAmbreConnectionString</c>) without coupling
    /// infrastructure to that service.
    /// </para>
    ///
    /// <para>
    /// All I/O is dispatched through <see cref="OleDbAsyncExecutor.RunWithConnectionAsync{T}"/>
    /// so the synchronous OleDb calls never run on the UI thread.
    /// </para>
    /// </summary>
    public sealed class DataAmbreRepository : IDataAmbreRepository
    {
        private readonly Func<string, string> _connectionStringFactory;
        private readonly ILogger<DataAmbreRepository> _logger;

        /// <param name="connectionStringFactory">
        /// Function mapping a country id (e.g. <c>"FR"</c>) to an OleDb connection string for
        /// the matching per-country Ambre <c>.accdb</c>. May return null/empty to signal that
        /// the country has no Ambre database — callers receive empty results in that case.
        /// </param>
        /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger{T}.Instance"/>.</param>
        public DataAmbreRepository(Func<string, string> connectionStringFactory, ILogger<DataAmbreRepository> logger = null)
        {
            _connectionStringFactory = connectionStringFactory ?? throw new ArgumentNullException(nameof(connectionStringFactory));
            _logger = logger ?? NullLogger<DataAmbreRepository>.Instance;
        }

        public async Task<IReadOnlyList<DataAmbre>> GetAllAsync(string countryId, bool includeDeleted = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return Array.Empty<DataAmbre>();
            var cs = _connectionStringFactory(countryId);
            if (string.IsNullOrWhiteSpace(cs)) return Array.Empty<DataAmbre>();

            var sql = includeDeleted
                ? $"SELECT * FROM [{Schema.Tables.T_Data_Ambre}] ORDER BY [{Schema.Columns.Ambre.Operation_Date}] ASC"
                : $"SELECT * FROM [{Schema.Tables.T_Data_Ambre}] WHERE [{Schema.Columns.Ambre.DeleteDate}] IS NULL ORDER BY [{Schema.Columns.Ambre.Operation_Date}] ASC";

            return await OleDbAsyncExecutor
                .RunWithConnectionAsync(cs, conn => ReadAll(conn, sql, null), ct)
                .ConfigureAwait(false);
        }

        public async Task<DataAmbre> GetByIdAsync(string countryId, string id, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(countryId) || string.IsNullOrWhiteSpace(id)) return null;
            var cs = _connectionStringFactory(countryId);
            if (string.IsNullOrWhiteSpace(cs)) return null;

            var sql = $"SELECT TOP 1 * FROM [{Schema.Tables.T_Data_Ambre}] " +
                      $"WHERE [{Schema.Columns.Ambre.ID}] = ? AND [{Schema.Columns.Ambre.DeleteDate}] IS NULL";

            var rows = await OleDbAsyncExecutor
                .RunWithConnectionAsync(cs, conn => ReadAll(conn, sql, new object[] { id }), ct)
                .ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }

        public async Task<IReadOnlyList<DataAmbre>> GetByAccountAsync(string countryId, string accountId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(countryId) || string.IsNullOrWhiteSpace(accountId)) return Array.Empty<DataAmbre>();
            var cs = _connectionStringFactory(countryId);
            if (string.IsNullOrWhiteSpace(cs)) return Array.Empty<DataAmbre>();

            var sql = $"SELECT * FROM [{Schema.Tables.T_Data_Ambre}] " +
                      $"WHERE [{Schema.Columns.Ambre.Account_ID}] = ? AND [{Schema.Columns.Ambre.DeleteDate}] IS NULL " +
                      $"ORDER BY [{Schema.Columns.Ambre.Operation_Date}] ASC";

            return await OleDbAsyncExecutor
                .RunWithConnectionAsync(cs, conn => ReadAll(conn, sql, new object[] { accountId }), ct)
                .ConfigureAwait(false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Synchronous row mapping. Hand-written for readability — DataAmbre has
        // a small column set so we don't bother with reflection here.
        // ─────────────────────────────────────────────────────────────────────
        private List<DataAmbre> ReadAll(OleDbConnection conn, string sql, object[] parameters)
        {
            var result = new List<DataAmbre>();
            try
            {
                using (var cmd = new OleDbCommand(sql, conn))
                {
                    if (parameters != null)
                    {
                        for (int i = 0; i < parameters.Length; i++)
                            cmd.Parameters.AddWithValue("@p" + i, parameters[i] ?? DBNull.Value);
                    }
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            result.Add(MapRow(rdr));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DataAmbreRepository: read failed (sql={Sql})", sql);
                throw;
            }
            return result;
        }

        private static DataAmbre MapRow(OleDbDataReader r)
        {
            var d = new DataAmbre
            {
                ID = GetString(r, Schema.Columns.Ambre.ID),
                Account_ID = GetString(r, Schema.Columns.Ambre.Account_ID),
                CCY = GetString(r, Schema.Columns.Ambre.CCY),
                Country = GetString(r, Schema.Columns.Ambre.Country),
                Event_Num = GetString(r, Schema.Columns.Ambre.Event_Num),
                Folder = GetString(r, Schema.Columns.Ambre.Folder),
                Pivot_MbawIDFromLabel = GetString(r, Schema.Columns.Ambre.Pivot_MbawIDFromLabel),
                Pivot_TransactionCodesFromLabel = GetString(r, Schema.Columns.Ambre.Pivot_TransactionCodesFromLabel),
                Pivot_TRNFromLabel = GetString(r, Schema.Columns.Ambre.Pivot_TRNFromLabel),
                RawLabel = GetString(r, Schema.Columns.Ambre.RawLabel),
                Receivable_DWRefFromAmbre = GetString(r, Schema.Columns.Ambre.Receivable_DWRefFromAmbre),
                LocalSignedAmount = GetDecimal(r, Schema.Columns.Ambre.LocalSignedAmount),
                Operation_Date = GetNullableDateTime(r, Schema.Columns.Ambre.Operation_Date),
                Reconciliation_Num = GetString(r, Schema.Columns.Ambre.Reconciliation_Num),
                Receivable_InvoiceFromAmbre = GetString(r, Schema.Columns.Ambre.Receivable_InvoiceFromAmbre),
                ReconciliationOrigin_Num = GetString(r, Schema.Columns.Ambre.ReconciliationOrigin_Num),
                SignedAmount = GetDecimal(r, Schema.Columns.Ambre.SignedAmount),
                Value_Date = GetNullableDateTime(r, Schema.Columns.Ambre.Value_Date),
                CreationDate = GetNullableDateTime(r, Schema.Columns.Ambre.CreationDate),
                DeleteDate = GetNullableDateTime(r, Schema.Columns.Ambre.DeleteDate),
                ModifiedBy = GetString(r, Schema.Columns.Ambre.ModifiedBy),
                LastModified = GetNullableDateTime(r, Schema.Columns.Ambre.LastModified),
            };
            // Version is the row-version int (BaseEntity.Version)
            try
            {
                var v = GetOrdinalSafe(r, Schema.Columns.Ambre.Version);
                if (v >= 0 && !r.IsDBNull(v))
                    d.Version = Convert.ToInt32(r.GetValue(v), CultureInfo.InvariantCulture);
            }
            catch { /* version column may be absent in older schemas */ }
            return d;
        }

        private static int GetOrdinalSafe(OleDbDataReader r, string name)
        {
            try { return r.GetOrdinal(name); } catch { return -1; }
        }

        private static string GetString(OleDbDataReader r, string name)
        {
            var i = GetOrdinalSafe(r, name);
            if (i < 0 || r.IsDBNull(i)) return null;
            return Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
        }

        private static decimal GetDecimal(OleDbDataReader r, string name)
        {
            var i = GetOrdinalSafe(r, name);
            if (i < 0 || r.IsDBNull(i)) return 0m;
            try { return Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture); }
            catch { return 0m; }
        }

        private static DateTime? GetNullableDateTime(OleDbDataReader r, string name)
        {
            var i = GetOrdinalSafe(r, name);
            if (i < 0 || r.IsDBNull(i)) return null;
            try { return Convert.ToDateTime(r.GetValue(i), CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
