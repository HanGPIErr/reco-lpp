using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Services
{
    /// <summary>
    /// Service dedicated to referential data access (e.g., T_User, T_param) in the referential database.
    /// Uses <see cref="Infrastructure.DataAccess.ReferentialConnectionPool"/> when available
    /// to avoid repeated open/close of DB_Referentiels.
    /// </summary>
    public class ReferentialService
    {
        private readonly OfflineFirstService _offlineFirstService;
        private readonly string _currentUser;

        public ReferentialService(OfflineFirstService offlineFirstService, string currentUser = null)
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
                // Ensure current user exists in T_User (USR_ID, USR_Name)
                try
                {
                    if (!string.IsNullOrWhiteSpace(_currentUser))
                    {
                        var checkCmd = new OleDbCommand("SELECT COUNT(*) FROM T_User WHERE USR_ID = ?", connection);
                        checkCmd.Parameters.AddWithValue("@p1", _currentUser);
                        var obj = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                        var exists = obj != null && int.TryParse(obj.ToString(), out var n) && n > 0;
                        if (!exists)
                        {
                            var insertCmd = new OleDbCommand("INSERT INTO T_User (USR_ID, USR_Name) VALUES (?, ?)", connection);
                            insertCmd.Parameters.AddWithValue("@p1", _currentUser);
                            insertCmd.Parameters.AddWithValue("@p2", _currentUser);
                            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch { /* best effort; not critical */ }

                var cmd = new OleDbCommand("SELECT USR_ID, USR_Name FROM T_User ORDER BY USR_Name", connection);
                using (var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rdr.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var id = rdr.IsDBNull(0) ? null : rdr.GetValue(0)?.ToString();
                        var name = rdr.IsDBNull(1) ? null : rdr.GetValue(1)?.ToString();
                        if (!string.IsNullOrWhiteSpace(id))
                            list.Add((id, name ?? id));
                    }
                }
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
                // Try common key column names to avoid coupling to a specific schema naming
                string[] keyColumns = { "Par_Key", "Par_Code", "Par_Name", "PAR_Key", "PAR_Code", "PAR_Name" };
                foreach (var col in keyColumns)
                {
                    try
                    {
                        var cmd = new OleDbCommand($"SELECT TOP 1 Par_Value FROM T_param WHERE {col} = ?", connection);
                        cmd.Parameters.AddWithValue("@p1", paramKey);
                        cancellationToken.ThrowIfCancellationRequested();
                        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                        if (obj != null && obj != DBNull.Value)
                            return obj.ToString();
                    }
                    catch
                    {
                        // Ignore and try next column variant
                    }
                }
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
            return null;
        }
    }
}
