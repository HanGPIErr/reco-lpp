using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;

namespace RecoTool.Infrastructure.DataAccess
{
    /// <summary>
    /// Lightweight reflection-based ORM for OleDb/Access queries.
    /// Extracted from ReconciliationService to be reusable across all services.
    /// Maps query result columns to DTO properties by name (case-insensitive).
    /// </summary>
    public class OleDbQueryExecutor
    {
        private readonly string _mainConnectionString;

        public OleDbQueryExecutor(string mainConnectionString)
        {
            _mainConnectionString = mainConnectionString ?? throw new ArgumentNullException(nameof(mainConnectionString));
        }

        /// <summary>
        /// Execute a query using the main connection string.
        /// </summary>
        public async Task<List<T>> QueryAsync<T>(string query, params object[] parameters) where T : new()
        {
            return await QueryAsync<T>(query, _mainConnectionString, parameters).ConfigureAwait(false);
        }

        /// <summary>
        /// Execute a query using a specific connection string.
        /// Handles Access linked-table resolution, parameterized queries, and reflection-based mapping.
        /// </summary>
        public async Task<List<T>> QueryAsync<T>(string query, string connectionString, params object[] parameters) where T : new()
        {
            var mainMdbPath = new OleDbConnectionStringBuilder(_mainConnectionString).DataSource;

            using var linkHandle = AccessLinkManager.GetHandle(mainMdbPath);
            query = await linkHandle.Helper
                .PrepareQueryAsync(query, CancellationToken.None)
                .ConfigureAwait(false);

            var results = new List<T>();

            using (var connection = new OleDbConnection(connectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var command = new OleDbCommand(query, connection))
                {
                    if (parameters != null)
                    {
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            command.Parameters.AddWithValue($"@param{i}", parameters[i] ?? DBNull.Value);
                        }
                    }

                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        var columnOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var name = reader.GetName(i);
                            columnOrdinals[name] = i;
                        }

                        var properties = typeof(T).GetProperties();
                        var propMaps = new List<(System.Reflection.PropertyInfo Prop, int Ordinal, Type TargetType)>();
                        foreach (var prop in properties)
                        {
                            try
                            {
                                if (!prop.CanWrite || prop.GetSetMethod(true) == null || prop.GetIndexParameters().Length > 0)
                                    continue;
                                if (!columnOrdinals.TryGetValue(prop.Name, out var ord))
                                    continue;
                                var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                                propMaps.Add((prop, ord, targetType));
                            }
                            catch { }
                        }

                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var item = new T();

                            foreach (var map in propMaps)
                            {
                                try
                                {
                                    if (reader.IsDBNull(map.Ordinal))
                                        continue;

                                    var value = reader.GetValue(map.Ordinal);
                                    if (value == DBNull.Value)
                                        continue;

                                    var converted = ConvertValue(value, map.TargetType);
                                    if (converted != null)
                                        map.Prop.SetValue(item, converted);
                                }
                                catch { }
                            }

                            if (typeof(T) == typeof(ReconciliationViewData))
                            {
                                try { DwingsDateHelper.NormalizeDwingsDateStrings(item as ReconciliationViewData); } catch { }
                            }

                            results.Add(item);
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Execute a scalar query (e.g., COUNT, MAX) using the main connection string.
        /// Handles Access linked-table resolution.
        /// </summary>
        public async Task<object> ScalarAsync(string query, params object[] parameters)
        {
            var mainMdbPath = new OleDbConnectionStringBuilder(_mainConnectionString).DataSource;

            using var linkHandle = AccessLinkManager.GetHandle(mainMdbPath);
            query = await linkHandle.Helper
                .PrepareQueryAsync(query, CancellationToken.None)
                .ConfigureAwait(false);

            using (var connection = new OleDbConnection(_mainConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var cmd = new OleDbCommand(query, connection))
                {
                    if (parameters != null)
                    {
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            cmd.Parameters.AddWithValue($"@param{i}", parameters[i] ?? DBNull.Value);
                        }
                    }
                    return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                }
            }
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (targetType == typeof(DateTime))
                return ConvertToDateTime(value);
            if (targetType == typeof(bool))
                return ConvertToBool(value);
            if (targetType == typeof(decimal))
                return ConvertToDecimal(value);
            if (targetType == typeof(string))
            {
                try { return Convert.ToString(value); } catch { return value?.ToString(); }
            }
            return Convert.ChangeType(value, targetType);
        }

        private static object ConvertToDateTime(object value)
        {
            if (value is DateTime dt) return dt;
            if (value is DateTimeOffset dto) return dto.UtcDateTime;

            var sVal = Convert.ToString(value);
            if (string.IsNullOrWhiteSpace(sVal)) return null;

            if (DwingsDateHelper.TryParseDwingsDate(sVal, out var parsed))
                return parsed;
            if (DateTime.TryParse(sVal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var gen))
                return gen;
            if (DateTime.TryParse(sVal, CultureInfo.GetCultureInfo("fr-FR"), DateTimeStyles.None, out gen))
                return gen;
            if (DateTime.TryParse(sVal, CultureInfo.GetCultureInfo("it-IT"), DateTimeStyles.None, out gen))
                return gen;
            try { return Convert.ToDateTime(value, CultureInfo.InvariantCulture); } catch { return null; }
        }

        private static object ConvertToBool(object value)
        {
            if (value is bool b) return b;
            if (value is byte by) return by != 0;
            if (value is short s) return s != 0;
            if (value is int i) return i != 0;
            return Convert.ToBoolean(value);
        }

        private static object ConvertToDecimal(object value)
        {
            if (value is decimal dm) return dm;
            if (value is double dd) return Convert.ToDecimal(dd);
            if (value is float ff) return Convert.ToDecimal(ff);
            return Convert.ChangeType(value, typeof(decimal));
        }
    }
}
