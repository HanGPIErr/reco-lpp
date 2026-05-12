using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Linq;
using System.Threading.Tasks;
using RecoTool.Infrastructure;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Models;

namespace RecoTool.Services
{
    /// <summary>
    /// Manages shared/global ToDo list items stored in the referential DB (T_Ref_TodoList).
    /// Uses <see cref="Infrastructure.DataAccess.ReferentialConnectionPool"/> when available
    /// to avoid repeated open/close of DB_Referentiels.
    /// </summary>
    public sealed class UserTodoListService : IUserTodoListService
    {
        private readonly string _connString;

        public UserTodoListService(string referentialConnectionStringOrPath)
        {
            _connString = Infrastructure.DataAccess.DbConn.ResolveConnectionString(referentialConnectionStringOrPath);
        }

        /// <summary>
        /// Returns a pooled connection if available, otherwise opens a new one.
        /// If the returned tuple has OwnsConnection=true, the caller must dispose it.
        /// </summary>
        private async Task<(OleDbConnection Connection, bool OwnsConnection)> GetConnectionAsync()
        {
            var pool = Infrastructure.DataAccess.ReferentialConnectionPool.Instance;
            if (pool != null)
            {
                try
                {
                    var conn = await pool.GetConnectionAsync().ConfigureAwait(false);
                    return (conn, false); // pool owns it
                }
                catch { /* fall through to ad-hoc */ }
            }

            // Fallback: ad-hoc connection
            var adhoc = new OleDbConnection(_connString);
            await adhoc.OpenAsync().ConfigureAwait(false);
            return (adhoc, true);
        }

        public async Task<bool> EnsureTableAsync()
        {
            const string table = Schema.Tables.T_Ref_TodoList;
            var (conn, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    try
                    {
                        var schema = c.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, table, "TABLE" });
                        if (schema == null || schema.Rows.Count == 0)
                        {
                            // Create table
                            var ddl = $@"CREATE TABLE [{table}] (
{Schema.Columns.TodoList.TDL_id} AUTOINCREMENT PRIMARY KEY,
{Schema.Columns.TodoList.TDL_Name} TEXT(100),
{Schema.Columns.TodoList.TDL_FilterName} TEXT(100),
{Schema.Columns.TodoList.TDL_ViewName} TEXT(100),
{Schema.Columns.TodoList.TDL_Account} TEXT(50),
{Schema.Columns.TodoList.TDL_Order} INTEGER,
{Schema.Columns.TodoList.TDL_Active} YESNO,
{Schema.Columns.TodoList.TDL_CountryId} TEXT(20)
)";
                            using (var cmd = new OleDbCommand(ddl, c))
                            {
                                cmd.ExecuteNonQuery();
                            }
                            try
                            {
                                using (var idx = new OleDbCommand($"CREATE UNIQUE INDEX UX_{table}_NameCountry ON [{table}] ({Schema.Columns.TodoList.TDL_Name}, {Schema.Columns.TodoList.TDL_CountryId})", c))
                                {
                                    idx.ExecuteNonQuery();
                                }
                            }
                            catch { /* best-effort index */ }
                            return true;
                        }
                        else
                        {
                            EnsureMissingColumns(c, table);
                            return true;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }, conn).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) conn?.Dispose();
            }
        }

        private static void EnsureMissingColumns(OleDbConnection conn, string table)
        {
            var cols = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, new object[] { null, null, table, null });
            void Ensure(string name, string ddl)
            {
                bool exists = cols != null && cols.Rows.Cast<DataRow>().Any(r => string.Equals(Convert.ToString(r["COLUMN_NAME"]), name, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    try
                    {
                        using (var cmd = new OleDbCommand($"ALTER TABLE [{table}] ADD COLUMN [{name}] {ddl}", conn))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch { }
                }
            }
            Ensure(Schema.Columns.TodoList.TDL_Name, "TEXT(100)");
            Ensure(Schema.Columns.TodoList.TDL_FilterName, "TEXT(100)");
            Ensure(Schema.Columns.TodoList.TDL_ViewName, "TEXT(100)");
            Ensure(Schema.Columns.TodoList.TDL_Account, "TEXT(50)");
            Ensure(Schema.Columns.TodoList.TDL_Order, "INTEGER");
            Ensure(Schema.Columns.TodoList.TDL_CountryId, "TEXT(20)"); // Kept for backward compatibility but not used in filters
        }

        public async Task<List<TodoListItem>> ListAsync(string countryId)
        {
            var (conn, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    var list = new List<TodoListItem>();
                    // No filter on TDL_CountryId: all todos are shared across countries
                    var cmd = new OleDbCommand($"SELECT {Schema.Columns.TodoList.TDL_id}, {Schema.Columns.TodoList.TDL_Name}, {Schema.Columns.TodoList.TDL_FilterName}, {Schema.Columns.TodoList.TDL_ViewName}, {Schema.Columns.TodoList.TDL_Account}, {Schema.Columns.TodoList.TDL_Order}, {Schema.Columns.TodoList.TDL_Active}, {Schema.Columns.TodoList.TDL_CountryId} FROM {Schema.Tables.T_Ref_TodoList} WHERE ({Schema.Columns.TodoList.TDL_Active} <> 0 OR {Schema.Columns.TodoList.TDL_Active} IS NULL) ORDER BY {Schema.Columns.TodoList.TDL_Order}, {Schema.Columns.TodoList.TDL_Name}", c);
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            list.Add(new TodoListItem
                            {
                                TDL_id = rdr.IsDBNull(0) ? 0 : Convert.ToInt32(rdr.GetValue(0)),
                                TDL_Name = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                                TDL_FilterName = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                                TDL_ViewName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                                TDL_Account = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                                TDL_Order = rdr.IsDBNull(5) ? (int?)null : Convert.ToInt32(rdr.GetValue(5)),
                                TDL_Active = rdr.IsDBNull(6) ? true : Convert.ToBoolean(rdr.GetValue(6)),
                                TDL_CountryId = rdr.IsDBNull(7) ? null : rdr.GetString(7)
                            });
                        }
                    }
                    return list;
                }, conn).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) conn?.Dispose();
            }
        }

        public async Task<int> UpsertAsync(TodoListItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.TDL_Name)) return 0;
            var (conn, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    // Check by name only (no country filter)
                    int? existingId = null;
                    using (var check = new OleDbCommand($"SELECT TOP 1 {Schema.Columns.TodoList.TDL_id} FROM {Schema.Tables.T_Ref_TodoList} WHERE {Schema.Columns.TodoList.TDL_Name} = ?", c))
                    {
                        check.Parameters.AddWithValue("@p1", item.TDL_Name);
                        var obj = check.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value) existingId = Convert.ToInt32(obj);
                    }

                    if (existingId.HasValue)
                    {
                        using (var cmd = new OleDbCommand($@"UPDATE {Schema.Tables.T_Ref_TodoList} SET
{Schema.Columns.TodoList.TDL_FilterName} = ?, {Schema.Columns.TodoList.TDL_ViewName} = ?, {Schema.Columns.TodoList.TDL_Account} = ?, {Schema.Columns.TodoList.TDL_Order} = ?, {Schema.Columns.TodoList.TDL_Active} = ?, {Schema.Columns.TodoList.TDL_CountryId} = ?
WHERE {Schema.Columns.TodoList.TDL_id} = ?", c))
                        {
                            cmd.Parameters.AddWithValue("@p1", (object)item.TDL_FilterName ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p2", (object)item.TDL_ViewName ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p3", (object)item.TDL_Account ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p4", (object)(item.TDL_Order.HasValue ? item.TDL_Order.Value : (int?)null) ?? DBNull.Value);
                            cmd.Parameters.Add("@p5", OleDbType.Boolean).Value = item.TDL_Active;
                            cmd.Parameters.AddWithValue("@p6", (object)item.TDL_CountryId ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p7", existingId.Value);
                            cmd.ExecuteNonQuery();
                            return existingId.Value;
                        }
                    }
                    else
                    {
                        using (var cmd = new OleDbCommand($@"INSERT INTO {Schema.Tables.T_Ref_TodoList} ({Schema.Columns.TodoList.TDL_Name}, {Schema.Columns.TodoList.TDL_FilterName}, {Schema.Columns.TodoList.TDL_ViewName}, {Schema.Columns.TodoList.TDL_Account}, {Schema.Columns.TodoList.TDL_Order}, {Schema.Columns.TodoList.TDL_Active}, {Schema.Columns.TodoList.TDL_CountryId}) VALUES (?, ?, ?, ?, ?, ?, ?)", c))
                        {
                            cmd.Parameters.AddWithValue("@p1", (object)item.TDL_Name ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p2", (object)item.TDL_FilterName ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p3", (object)item.TDL_ViewName ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p4", (object)item.TDL_Account ?? DBNull.Value);
                            cmd.Parameters.AddWithValue("@p5", (object)(item.TDL_Order.HasValue ? item.TDL_Order.Value : (int?)null) ?? DBNull.Value);
                            cmd.Parameters.Add("@p6", OleDbType.Boolean).Value = item.TDL_Active;
                            cmd.Parameters.AddWithValue("@p7", (object)item.TDL_CountryId ?? DBNull.Value);
                            var n = cmd.ExecuteNonQuery();
                            if (n > 0)
                            {
                                using (var idCmd = new OleDbCommand("SELECT @@IDENTITY", c))
                                {
                                    var obj = idCmd.ExecuteScalar();
                                    return obj == null || obj == DBNull.Value ? 0 : Convert.ToInt32(obj);
                                }
                            }
                            return 0;
                        }
                    }
                }, conn).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) conn?.Dispose();
            }
        }

        public async Task<bool> DeleteAsync(int id)
        {
            var (conn, ownsConnection) = await GetConnectionAsync().ConfigureAwait(false);
            try
            {
                return await OleDbAsyncExecutor.RunAsync(c =>
                {
                    using (var cmd = new OleDbCommand($"DELETE FROM {Schema.Tables.T_Ref_TodoList} WHERE {Schema.Columns.TodoList.TDL_id} = ?", c))
                    {
                        cmd.Parameters.AddWithValue("@p1", id);
                        var n = cmd.ExecuteNonQuery();
                        return n > 0;
                    }
                }, conn).ConfigureAwait(false);
            }
            finally
            {
                if (ownsConnection) conn?.Dispose();
            }
        }
    }
}
