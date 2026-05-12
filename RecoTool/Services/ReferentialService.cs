using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Infrastructure;
using RecoTool.Infrastructure.DataAccess;

namespace RecoTool.Services
{
    /// <summary>
    /// Service dedicated to referential data access (e.g., T_User, T_param) in the referential database.
    /// Uses <see cref="Infrastructure.DataAccess.ReferentialConnectionPool"/> when available
    /// to avoid repeated open/close of DB_Referentiels.
    /// </summary>
    public class ReferentialService
    {
        private readonly IOfflineFirstService _offlineFirstService;
        private readonly string _currentUser;

        public ReferentialService(IOfflineFirstService offlineFirstService, string currentUser = null)
        {
            _offlineFirstService = offlineFirstService ?? throw new ArgumentNullException(nameof(offlineFirstService));
            _currentUser = currentUser;
        }

        private string GetReferentialConnectionString()
        {
            var refCs = _offlineFirstService?.ReferentialConnectionString;
            if (string.IsNullOrWhiteSpace(refCs))
                throw new InvalidOperationException("Referential connection string is required (inject OfflineFirstService).");
            return refCs;
        }

        /// <summary>
        /// Returns a pooled connection if available, otherwise opens a new one.
        /// If OwnsConnection is true, the caller must dispose it.
        /// </summary>
        private async Task<(OleDbConnection Connection, bool OwnsConnection)> GetConnectionAsync(CancellationToken token = default)
        {
            var pool = Infrastructure.DataAccess.ReferentialConnectionPool.Instance;
            if (pool != null)
            {
                try
                {
                    var conn = await pool.GetConnectionAsync(token).ConfigureAwait(false);
                    return (conn, false);
                }
                catch { /* fall through to ad-hoc */ }
            }
            var adhoc = new OleDbConnection(GetReferentialConnectionString());
            await adhoc.OpenAsync(token).ConfigureAwait(false);
            return (adhoc, true);
        }

        /// <summary>
        /// Get user list from T_User and ensure the current user exists.
        /// </summary>
        public async Task<List<(string Id, string Name)>> GetUsersAsync(CancellationToken cancellationToken = default)
        {
            var list = new List<(string, string)>();
            var (connection, ownsConnection) = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await OleDbAsyncExecutor.RunAsync(c =>
                {
                    // Ensure current user exists in T_User (USR_ID, USR_Name)
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(_currentUser))
                        {
                            var checkCmd = new OleDbCommand($"SELECT COUNT(*) FROM {Schema.Tables.T_User} WHERE {Schema.Columns.User.USR_ID} = ?", c);
                            checkCmd.Parameters.AddWithValue("@p1", _currentUser);
                            var obj = checkCmd.ExecuteScalar();
                            var exists = obj != null && int.TryParse(obj.ToString(), out var n) && n > 0;
                            if (!exists)
                            {
                                var insertCmd = new OleDbCommand($"INSERT INTO {Schema.Tables.T_User} ({Schema.Columns.User.USR_ID}, {Schema.Columns.User.USR_Name}) VALUES (?, ?)", c);
                                insertCmd.Parameters.AddWithValue("@p1", _currentUser);
                                insertCmd.Parameters.AddWithValue("@p2", _currentUser);
                                insertCmd.ExecuteNonQuery();
                            }
                        }
                    }
                    catch { /* best effort; not critical */ }

                    var cmd = new OleDbCommand($"SELECT {Schema.Columns.User.USR_ID}, {Schema.Columns.User.USR_Name} FROM {Schema.Tables.T_User} ORDER BY {Schema.Columns.User.USR_Name}", c);
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            var id = rdr.IsDBNull(0) ? null : rdr.GetValue(0)?.ToString();
                            var name = rdr.IsDBNull(1) ? null : rdr.GetValue(1)?.ToString();
                            if (!string.IsNullOrWhiteSpace(id))
                                list.Add((id, name ?? id));
                        }
                    }
                }, connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
            return list;
        }

        /// <summary>
        /// Reads a SQL payload from referential table T_param.Par_Value using a flexible key lookup.
        /// Accepts keys like Export_KPI, Export_PastDUE, Export_IT.
        /// </summary>
        public async Task<string> GetParamValueAsync(string paramKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(paramKey)) return null;

            var (connection, ownsConnection) = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    // Try common key column names to avoid coupling to a specific schema naming.
                    // Note: only PAR_Key has a Schema.Columns.Param constant; the *_Code / *_Name
                    // variants are legacy probes that do not appear in Schema and stay as literals.
                    // TODO: add to Schema.Columns.Param if Par_Code / Par_Name are ever standardized.
                    string[] keyColumns = { "Par_Key", "Par_Code", "Par_Name", Schema.Columns.Param.PAR_Key, "PAR_Code", "PAR_Name" };
                    foreach (var col in keyColumns)
                    {
                        try
                        {
                            var cmd = new OleDbCommand($"SELECT TOP 1 {Schema.Columns.Param.PAR_Value} FROM {Schema.Tables.T_Param} WHERE {col} = ?", c);
                            cmd.Parameters.AddWithValue("@p1", paramKey);
                            cancellationToken.ThrowIfCancellationRequested();
                            var obj = cmd.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                                return obj.ToString();
                        }
                        catch
                        {
                            // Ignore and try next column variant
                        }
                    }
                    return (string)null;
                }, connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }
    }
}
