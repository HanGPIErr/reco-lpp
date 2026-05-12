using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Threading.Tasks;
using RecoTool.Infrastructure;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Models;

namespace RecoTool.Services
{
    /// <summary>
    /// Manages Saved Views (T_Ref_User_Fields_Preference): list, get, upsert, delete.
    /// Uses <see cref="Infrastructure.DataAccess.ReferentialConnectionPool"/> when available
    /// to avoid repeated open/close of DB_Referentiels.
    /// </summary>
    public sealed class UserViewPreferenceService : IUserViewPreferenceService
    {
        private readonly string _connString;
        private readonly string _currentUser;

        public UserViewPreferenceService(string referentialConnectionStringOrPath, string currentUser)
        {
            _connString = Infrastructure.DataAccess.DbConn.ResolveConnectionString(referentialConnectionStringOrPath);
            _currentUser = string.IsNullOrWhiteSpace(currentUser) ? Environment.UserName : currentUser;
        }

        private async Task<(OleDbConnection Connection, bool OwnsConnection)> GetConnectionAsync()
        {
            var pool = Infrastructure.DataAccess.ReferentialConnectionPool.Instance;
            if (pool != null)
            {
                try { return (await pool.GetConnectionAsync().ConfigureAwait(false), false); }
                catch { /* fall through */ }
            }
            var adhoc = new OleDbConnection(_connString);
            await adhoc.OpenAsync().ConfigureAwait(false);
            return (adhoc, true);
        }

        public async Task<List<UserFieldsPreference>> GetAllAsync()
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var list = new List<UserFieldsPreference>();
                    var cmd = new OleDbCommand($@"SELECT {Schema.Columns.UserFieldsPreference.UPF_id}, {Schema.Columns.UserFieldsPreference.UPF_Name}, {Schema.Columns.UserFieldsPreference.UPF_user}, {Schema.Columns.UserFieldsPreference.UPF_SQL}, {Schema.Columns.UserFieldsPreference.UPF_ColumnWidths} FROM {Schema.Tables.T_Ref_User_Fields_Preference} ORDER BY {Schema.Columns.UserFieldsPreference.UPF_Name}", c);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add(new UserFieldsPreference
                            {
                                UPF_id = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                                UPF_Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                                UPF_user = reader.IsDBNull(2) ? null : reader.GetString(2),
                                UPF_SQL = reader.IsDBNull(3) ? null : reader.GetString(3),
                                UPF_ColumnWidths = reader.IsDBNull(4) ? null : reader.GetString(4)
                            });
                        }
                    }
                    return list;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }

        public async Task<int> InsertAsync(string name, string sql, string columnsJson)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var cmd = new OleDbCommand($@"INSERT INTO {Schema.Tables.T_Ref_User_Fields_Preference} ({Schema.Columns.UserFieldsPreference.UPF_Name}, {Schema.Columns.UserFieldsPreference.UPF_user}, {Schema.Columns.UserFieldsPreference.UPF_SQL}, {Schema.Columns.UserFieldsPreference.UPF_ColumnWidths}) VALUES (?, ?, ?, ?)", c);
                    cmd.Parameters.AddWithValue("@p1", name ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p2", _currentUser ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p3", sql ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p4", columnsJson ?? (object)DBNull.Value);
                    var n = cmd.ExecuteNonQuery();
                    if (n > 0)
                    {
                        var idCmd = new OleDbCommand(@"SELECT @@IDENTITY", c);
                        var obj = idCmd.ExecuteScalar();
                        return obj == null || obj == DBNull.Value ? 0 : Convert.ToInt32(obj);
                    }
                    return 0;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }

        public async Task<bool> UpdateAsync(int id, string name, string sql, string columnsJson)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var cmd = new OleDbCommand($@"UPDATE {Schema.Tables.T_Ref_User_Fields_Preference} SET {Schema.Columns.UserFieldsPreference.UPF_Name} = ?, {Schema.Columns.UserFieldsPreference.UPF_user} = ?, {Schema.Columns.UserFieldsPreference.UPF_SQL} = ?, {Schema.Columns.UserFieldsPreference.UPF_ColumnWidths} = ? WHERE {Schema.Columns.UserFieldsPreference.UPF_id} = ?", c);
                    cmd.Parameters.AddWithValue("@p1", name ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p2", _currentUser ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p3", sql ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p4", columnsJson ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p5", id);
                    return cmd.ExecuteNonQuery() > 0;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }

        public async Task<int> UpsertAsync(string name, string sql, string columnsJson)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            int? existingId;
            try
            {
                existingId = await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var check = new OleDbCommand($@"SELECT TOP 1 {Schema.Columns.UserFieldsPreference.UPF_id} FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_Name} = ? AND {Schema.Columns.UserFieldsPreference.UPF_user} = ?", c);
                    check.Parameters.AddWithValue("@p1", name ?? (object)DBNull.Value);
                    check.Parameters.AddWithValue("@p2", _currentUser ?? (object)DBNull.Value);
                    var obj = check.ExecuteScalar();
                    if (obj != null && obj != DBNull.Value)
                        return (int?)Convert.ToInt32(obj);
                    return (int?)null;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }

            if (existingId.HasValue)
            {
                await UpdateAsync(existingId.Value, name, sql, columnsJson);
                return existingId.Value;
            }
            else
            {
                return await InsertAsync(name, sql, columnsJson);
            }
        }

        public async Task<UserFieldsPreference> GetByNameAsync(string name)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var cmd = new OleDbCommand($@"SELECT TOP 1 {Schema.Columns.UserFieldsPreference.UPF_id}, {Schema.Columns.UserFieldsPreference.UPF_Name}, {Schema.Columns.UserFieldsPreference.UPF_user}, {Schema.Columns.UserFieldsPreference.UPF_SQL}, {Schema.Columns.UserFieldsPreference.UPF_ColumnWidths} FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_Name} = ?", c);
                    cmd.Parameters.AddWithValue("@p1", name ?? (object)DBNull.Value);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return new UserFieldsPreference
                            {
                                UPF_id = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0)),
                                UPF_Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                                UPF_user = reader.IsDBNull(2) ? null : reader.GetString(2),
                                UPF_SQL = reader.IsDBNull(3) ? null : reader.GetString(3),
                                UPF_ColumnWidths = reader.IsDBNull(4) ? null : reader.GetString(4)
                            };
                        }
                    }
                    return (UserFieldsPreference)null;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }

        public async Task<List<string>> ListNamesAsync(string contains = null)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var result = new List<string>();
                    OleDbCommand cmd;
                    if (string.IsNullOrWhiteSpace(contains))
                    {
                        cmd = new OleDbCommand($@"SELECT DISTINCT {Schema.Columns.UserFieldsPreference.UPF_Name} FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_user} = ? ORDER BY {Schema.Columns.UserFieldsPreference.UPF_Name} ASC", c);
                        cmd.Parameters.AddWithValue("@p1", _currentUser ?? (object)DBNull.Value);
                    }
                    else
                    {
                        cmd = new OleDbCommand($@"SELECT DISTINCT {Schema.Columns.UserFieldsPreference.UPF_Name} FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_user} = ? AND {Schema.Columns.UserFieldsPreference.UPF_Name} LIKE ? ORDER BY {Schema.Columns.UserFieldsPreference.UPF_Name} ASC", c);
                        cmd.Parameters.AddWithValue("@p1", _currentUser ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@p2", "%" + contains + "%");
                    }
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            if (!rd.IsDBNull(0)) result.Add(rd.GetString(0));
                        }
                    }
                    return result;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }

        public async Task<List<(string Name, string Creator)>> ListDetailedAsync(string contains = null)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var list = new List<(string, string)>();
                    OleDbCommand cmd;
                    if (string.IsNullOrWhiteSpace(contains))
                    {
                        cmd = new OleDbCommand($@"SELECT DISTINCT {Schema.Columns.UserFieldsPreference.UPF_Name}, {Schema.Columns.UserFieldsPreference.UPF_user} FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_user} = ? ORDER BY {Schema.Columns.UserFieldsPreference.UPF_Name} ASC", c);
                        cmd.Parameters.AddWithValue("@p1", _currentUser ?? (object)DBNull.Value);
                    }
                    else
                    {
                        cmd = new OleDbCommand($@"SELECT DISTINCT {Schema.Columns.UserFieldsPreference.UPF_Name}, {Schema.Columns.UserFieldsPreference.UPF_user} FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_user} = ? AND {Schema.Columns.UserFieldsPreference.UPF_Name} LIKE ? ORDER BY {Schema.Columns.UserFieldsPreference.UPF_Name} ASC", c);
                        cmd.Parameters.AddWithValue("@p1", _currentUser ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@p2", "%" + contains + "%");
                    }
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            var name = rd.IsDBNull(0) ? string.Empty : rd.GetString(0);
                            var creator = rd.IsDBNull(1) ? string.Empty : rd.GetString(1);
                            list.Add((name, creator));
                        }
                    }
                    return list;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }

        public async Task<bool> DeleteByNameAsync(string name)
        {
            var (connection, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var cmd = new OleDbCommand($@"DELETE FROM {Schema.Tables.T_Ref_User_Fields_Preference} WHERE {Schema.Columns.UserFieldsPreference.UPF_Name} = ? AND {Schema.Columns.UserFieldsPreference.UPF_user} = ?", c);
                    cmd.Parameters.AddWithValue("@p1", name ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@p2", _currentUser ?? (object)DBNull.Value);
                    return cmd.ExecuteNonQuery() > 0;
                }, connection).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) connection?.Dispose();
            }
        }
    }
}
