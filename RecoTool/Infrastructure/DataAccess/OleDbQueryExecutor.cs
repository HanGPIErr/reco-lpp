using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
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
    ///
    /// PERF: Property setters are compiled once per (Type, PropertyInfo) via Expression trees
    /// and cached statically. With 20k+ rows and 80+ columns, this saves ~1.6M reflection
    /// SetValue calls per ReconciliationView load. Compiled delegates run 5-10x faster.
    /// </summary>
    public class OleDbQueryExecutor
    {
        private readonly string _mainConnectionString;

        public OleDbQueryExecutor(string mainConnectionString)
        {
            _mainConnectionString = mainConnectionString ?? throw new ArgumentNullException(nameof(mainConnectionString));
        }

        // ────────────────────────────────────────────────────────────────────
        // Compiled-setter cache
        //
        // Reflection's PropertyInfo.SetValue() does runtime type-checking,
        // boxing/unboxing, and method dispatch on every call. For a single
        // ReconciliationView load (~20k rows × ~80 mapped cols = 1.6M calls),
        // that overhead dominates the row-mapping time.
        //
        // We replace it with an Expression-compiled Action<object, object>
        // delegate that's roughly equivalent to direct IL: unbox + setter call.
        // The delegate is built once per property the first time we map a row
        // of that DTO type, then served from a static ConcurrentDictionary.
        // ────────────────────────────────────────────────────────────────────
        private static readonly ConcurrentDictionary<Type, ColumnSetter[]> _setterMapCache
            = new ConcurrentDictionary<Type, ColumnSetter[]>();

        private sealed class ColumnSetter
        {
            public string PropertyName;
            public Type TargetType; // Nullable underlying type when applicable
            public Action<object, object> Set;
            // Fallback path used if Expression-compilation throws for some
            // exotic property (init-only, ref-returns, etc.). Kept so we
            // never lose the ability to map a column.
            public PropertyInfo Reflected;
        }

        private static ColumnSetter[] GetOrBuildSetterMap(Type t)
        {
            return _setterMapCache.GetOrAdd(t, type =>
            {
                var list = new List<ColumnSetter>(64);
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        if (!prop.CanWrite) continue;
                        var setMethod = prop.GetSetMethod(nonPublic: true);
                        if (setMethod == null) continue;
                        if (prop.GetIndexParameters().Length > 0) continue;

                        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                        Action<object, object> compiled = null;
                        try { compiled = CompileSetter(prop, setMethod); }
                        catch { /* fall back to reflection below */ }

                        list.Add(new ColumnSetter
                        {
                            PropertyName = prop.Name,
                            TargetType = targetType,
                            Set = compiled,
                            Reflected = compiled == null ? prop : null
                        });
                    }
                    catch { /* skip props that can't be inspected */ }
                }
                return list.ToArray();
            });
        }

        private static Action<object, object> CompileSetter(PropertyInfo prop, MethodInfo setMethod)
        {
            // (object instance, object value) =>
            //     ((TDecl)instance).Setter((TProp)value);
            var instance = Expression.Parameter(typeof(object), "instance");
            var value = Expression.Parameter(typeof(object), "value");

            var castInstance = Expression.Convert(instance, prop.DeclaringType);
            var castValue = Expression.Convert(value, prop.PropertyType);
            var setCall = Expression.Call(castInstance, setMethod, castValue);

            return Expression.Lambda<Action<object, object>>(setCall, instance, value).Compile();
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

            // Build (or fetch from cache) the per-property compiled setters for T.
            // This happens once per Type per process — reused across all queries.
            var setterMap = GetOrBuildSetterMap(typeof(T));

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
                        // Build the column-ordinal lookup once for this reader.
                        var columnOrdinals = new Dictionary<string, int>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            columnOrdinals[reader.GetName(i)] = i;
                        }

                        // Project the cached setter map onto THIS query's columns:
                        //   keep only the setters whose property name matches a returned column.
                        // Using a struct array (instead of List of (struct)) for tighter cache locality.
                        var activeMaps = new ActiveMap[setterMap.Length];
                        int activeCount = 0;
                        for (int i = 0; i < setterMap.Length; i++)
                        {
                            var s = setterMap[i];
                            if (columnOrdinals.TryGetValue(s.PropertyName, out var ord))
                            {
                                activeMaps[activeCount++] = new ActiveMap
                                {
                                    Setter = s,
                                    Ordinal = ord
                                };
                            }
                        }

                        bool isReconciliationViewData = typeof(T) == typeof(ReconciliationViewData);

                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var item = new T();

                            // Tight loop: index by int (no foreach allocator), array access (no virtcall).
                            // Note: we copy the struct (small: 1 class ref + 1 int = 16 bytes) instead of
                            // using `ref var map = ref activeMaps[i]` which would require C# 11+ inside an
                            // async method (the project targets C# 8 on .NET Framework 4.8). The copy cost
                            // is negligible vs the actual reflection/setter call cost.
                            for (int i = 0; i < activeCount; i++)
                            {
                                var map = activeMaps[i];
                                try
                                {
                                    if (reader.IsDBNull(map.Ordinal)) continue;

                                    var raw = reader.GetValue(map.Ordinal);
                                    if (raw == DBNull.Value) continue;

                                    var converted = ConvertValue(raw, map.Setter.TargetType);
                                    if (converted == null) continue;

                                    if (map.Setter.Set != null)
                                        map.Setter.Set(item, converted);
                                    else if (map.Setter.Reflected != null)
                                        map.Setter.Reflected.SetValue(item, converted); // fallback
                                }
                                catch { /* per-cell mapping errors stay non-fatal — same as before */ }
                            }

                            if (isReconciliationViewData)
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

        // Per-row map struct — kept tiny so stack/array layout is dense.
        private struct ActiveMap
        {
            public ColumnSetter Setter;
            public int Ordinal;
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
            // Hot path: most cells return the matching CLR type directly.
            if (value.GetType() == targetType) return value;

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
