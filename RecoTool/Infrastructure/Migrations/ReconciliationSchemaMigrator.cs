using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Infrastructure.Migrations
{
    /// <summary>
    /// Idempotent schema migrator for the country Reconciliation database (T_Reconciliation).
    /// Adds any column that is declared in <see cref="RequiredColumns"/> but absent from the live table.
    /// Safe to call on every import: ALTER TABLE statements are only emitted for missing columns,
    /// and each failure is isolated (best-effort).
    ///
    /// CALL SITE: invoke this early in the AMBRE import flow, AFTER the local database has been refreshed
    /// from the network (CopyNetworkToLocal) and BEFORE UpdateReconciliationTable. The global import
    /// lock guarantees no other process can publish over our changes during the rest of the pipeline.
    /// The final CopyLocalToNetwork step will propagate the new schema to all peers transparently.
    /// </summary>
    public static class ReconciliationSchemaMigrator
    {
        /// <summary>
        /// List of columns that must exist on T_Reconciliation. New columns introduced by future
        /// phases should be appended here (keep this file as the single source of truth for the schema
        /// drift on the user side).
        /// Access DDL rules: TEXT is capped at 255 chars; use MEMO for longer fields.
        /// </summary>
        public static readonly IReadOnlyList<(string Name, string Ddl)> RequiredColumns = new List<(string, string)>
        {
            // --- Phase 1 robustness: per-field user-edit protection + audit trail ---
            ("LastModifiedByUser", "DATETIME"),
            ("UserEditedFields",   "TEXT(255)"),
            ("LastRuleAppliedId",  "TEXT(100)"),
            ("LastRuleAppliedAt",  "DATETIME"),
            // --- Phase 2: Partially-paid BGI tracking ---
            // When > 0, the row is excluded from the bulk Trigger flow until the user
            // resets it to 0 (= "fully paid"). NULL means the field has never been set.
            ("RemainingAmount",    "CURRENCY"),
        };

        /// <summary>
        /// Result of a migration run, useful for logging.
        /// </summary>
        public sealed class MigrationReport
        {
            public string TableName { get; set; }
            public List<string> Added { get; } = new List<string>();
            public List<string> Skipped { get; } = new List<string>();
            public List<string> Failed { get; } = new List<string>();
            public bool IsNoOp => Added.Count == 0 && Failed.Count == 0;
            public override string ToString()
            {
                return $"[{TableName}] added={Added.Count} ({string.Join(",", Added)}) failed={Failed.Count} ({string.Join(",", Failed)})";
            }
        }

        /// <summary>
        /// Ensures every column listed in <see cref="RequiredColumns"/> is present on <c>T_Reconciliation</c>
        /// of the database referenced by <paramref name="connectionString"/>. Never throws: failures are
        /// captured in the returned report so the caller can log/warn without aborting the import.
        /// </summary>
        public static async Task<MigrationReport> EnsureReconciliationColumnsAsync(
            string connectionString,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("connectionString is required", nameof(connectionString));

            return await EnsureColumnsAsync(connectionString, "T_Reconciliation", RequiredColumns, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Generic entry point: ensures a set of columns exists on <paramref name="tableName"/>.
        /// Used by the reconciliation migration above, but reusable for other tables if needed.
        /// </summary>
        public static async Task<MigrationReport> EnsureColumnsAsync(
            string connectionString,
            string tableName,
            IEnumerable<(string Name, string Ddl)> columns,
            CancellationToken ct = default)
        {
            var report = new MigrationReport { TableName = tableName };
            if (columns == null) return report;

            using (var conn = new OleDbConnection(connectionString))
            {
                try
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    report.Failed.Add($"<open>: {ex.Message}");
                    return report;
                }

                // Introspect existing columns ONCE to avoid a schema round-trip per column.
                DataTable schema;
                try
                {
                    schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, new object[] { null, null, tableName, null });
                }
                catch (Exception ex)
                {
                    report.Failed.Add($"<schema>: {ex.Message}");
                    return report;
                }

                if (schema == null)
                {
                    // Table does not exist at all — we do not create it here; this migrator only
                    // fixes column drift on an existing table (the table itself is managed by
                    // DatabaseRecreationService).
                    report.Failed.Add($"<table {tableName} not found>");
                    return report;
                }

                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataRow row in schema.Rows)
                {
                    var n = Convert.ToString(row["COLUMN_NAME"]);
                    if (!string.IsNullOrWhiteSpace(n)) existingNames.Add(n);
                }

                foreach (var (name, ddl) in columns)
                {
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ddl)) continue;

                    if (existingNames.Contains(name))
                    {
                        report.Skipped.Add(name);
                        continue;
                    }

                    try
                    {
                        using (var cmd = new OleDbCommand($"ALTER TABLE [{tableName}] ADD COLUMN [{name}] {ddl}", conn))
                        {
                            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                        report.Added.Add(name);
                    }
                    catch (Exception ex)
                    {
                        report.Failed.Add($"{name}: {ex.Message}");
                    }
                }
            }

            return report;
        }
    }
}
