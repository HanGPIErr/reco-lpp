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
    /// OleDb-backed implementation of <see cref="IUserFieldsRepository"/>.
    ///
    /// <para>
    /// <c>T_Ref_User_Fields</c> lives in the shared referential database, so a single
    /// fixed connection string is enough. The connection string is resolved lazily via a
    /// <c>Func&lt;string&gt;</c> so callers can defer construction until the referential
    /// DB path is known (and so that connection-string changes after startup are picked up).
    /// </para>
    /// </summary>
    public sealed class UserFieldsRepository : IUserFieldsRepository
    {
        private readonly Func<string> _connectionStringFactory;
        private readonly ILogger<UserFieldsRepository> _logger;

        /// <param name="connectionStringFactory">
        /// Returns the referential OleDb connection string. Called once per query. Must not return null/empty.
        /// </param>
        /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger{T}.Instance"/>.</param>
        public UserFieldsRepository(Func<string> connectionStringFactory, ILogger<UserFieldsRepository> logger = null)
        {
            _connectionStringFactory = connectionStringFactory ?? throw new ArgumentNullException(nameof(connectionStringFactory));
            _logger = logger ?? NullLogger<UserFieldsRepository>.Instance;
        }

        public async Task<IReadOnlyList<UserField>> GetAllAsync(CancellationToken ct = default)
        {
            var cs = _connectionStringFactory();
            if (string.IsNullOrWhiteSpace(cs)) return Array.Empty<UserField>();

            var sql = BuildSelect()
                    + $" ORDER BY [{Schema.Columns.UserFields.USR_Category}], [{Schema.Columns.UserFields.USR_FieldName}]";

            return await OleDbAsyncExecutor
                .RunWithConnectionAsync(cs, conn => ReadAll(conn, sql, null), ct)
                .ConfigureAwait(false);
        }

        public async Task<UserField> GetByIdAsync(int id, CancellationToken ct = default)
        {
            var cs = _connectionStringFactory();
            if (string.IsNullOrWhiteSpace(cs)) return null;

            var sql = BuildSelect()
                    + $" WHERE [{Schema.Columns.UserFields.USR_ID}] = ?";

            var rows = await OleDbAsyncExecutor
                .RunWithConnectionAsync(cs, conn => ReadAll(conn, sql, new object[] { id }), ct)
                .ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }

        public async Task<IReadOnlyList<UserField>> GetByCategoryAsync(string category, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(category)) return Array.Empty<UserField>();
            var cs = _connectionStringFactory();
            if (string.IsNullOrWhiteSpace(cs)) return Array.Empty<UserField>();

            // Access is case-insensitive on text comparisons by default — no need for LCase().
            var sql = BuildSelect()
                    + $" WHERE [{Schema.Columns.UserFields.USR_Category}] = ?"
                    + $" ORDER BY [{Schema.Columns.UserFields.USR_FieldName}]";

            return await OleDbAsyncExecutor
                .RunWithConnectionAsync(cs, conn => ReadAll(conn, sql, new object[] { category }), ct)
                .ConfigureAwait(false);
        }

        private static string BuildSelect()
        {
            return "SELECT "
                 + $"[{Schema.Columns.UserFields.USR_ID}], "
                 + $"[{Schema.Columns.UserFields.USR_Category}], "
                 + $"[{Schema.Columns.UserFields.USR_FieldName}], "
                 + $"[{Schema.Columns.UserFields.USR_FieldDescription}], "
                 + $"[{Schema.Columns.UserFields.USR_Pivot}], "
                 + $"[{Schema.Columns.UserFields.USR_Receivable}], "
                 + $"[{Schema.Columns.UserFields.USR_Color}] "
                 + $"FROM [{Schema.Tables.T_Ref_User_Fields}]";
        }

        private List<UserField> ReadAll(OleDbConnection conn, string sql, object[] parameters)
        {
            var result = new List<UserField>();
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
                _logger.LogError(ex, "UserFieldsRepository: read failed (sql={Sql})", sql);
                throw;
            }
            return result;
        }

        private static UserField MapRow(OleDbDataReader r)
        {
            return new UserField
            {
                USR_ID = GetInt32(r, Schema.Columns.UserFields.USR_ID),
                USR_Category = GetString(r, Schema.Columns.UserFields.USR_Category),
                USR_FieldName = GetString(r, Schema.Columns.UserFields.USR_FieldName),
                USR_FieldDescription = GetString(r, Schema.Columns.UserFields.USR_FieldDescription),
                USR_Pivot = GetBool(r, Schema.Columns.UserFields.USR_Pivot),
                USR_Receivable = GetBool(r, Schema.Columns.UserFields.USR_Receivable),
                USR_Color = GetString(r, Schema.Columns.UserFields.USR_Color),
            };
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

        private static int GetInt32(OleDbDataReader r, string name)
        {
            var i = GetOrdinalSafe(r, name);
            if (i < 0 || r.IsDBNull(i)) return 0;
            try { return Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static bool GetBool(OleDbDataReader r, string name)
        {
            var i = GetOrdinalSafe(r, name);
            if (i < 0 || r.IsDBNull(i)) return false;
            try { return Convert.ToBoolean(r.GetValue(i), CultureInfo.InvariantCulture); }
            catch { return false; }
        }
    }
}
