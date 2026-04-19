using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OfflineFirstAccess.Helpers;
using RecoTool.Services.DTOs;
// ViewDataEnricher exposes the UserField/Assignee name caches we reuse to render
// raw FK ids as semantic labels inside diff tooltips.

namespace RecoTool.Services.Snapshots
{
    /// <summary>
    /// Computes diffs between the live country databases and the snapshots produced by
    /// <see cref="SnapshotService"/>. Stateless, thread-safe; cache-free by design — callers that
    /// need caching can wrap the result sets themselves (typically the view-model layer).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why load both sides in memory?</b> Access supports cross-database joins
    /// (<c>FROM t1 INNER JOIN t2 IN 'other.accdb' ON …</c>) but they need a <c>FULL OUTER JOIN</c>
    /// for the symmetric diff which Access doesn't have natively — emulating with two
    /// <c>LEFT JOIN</c>s + <c>UNION</c> is slower, fragile, and harder to extend. A typical
    /// T_Reconciliation has ~20k rows × ~30 columns → a few MB of managed dictionaries, comparing
    /// in pure C# is ~200 ms and trivially readable.
    /// </para>
    /// <para>
    /// <b>AMBRE diff is row-level only</b> (added / archived): business side says AMBRE payloads
    /// are effectively immutable between imports — once a line is reconciled it's archived, the
    /// rest is new data. So we skip field-level diff on <c>T_Data_Ambre</c> and only flag
    /// rows that appear or disappear.
    /// </para>
    /// <para>
    /// <b>DWINGS diff is where the interesting churn lives</b> — payment statuses flip (pending →
    /// validated → settled, MT status updates, payment method fixes, etc.). We diff
    /// <c>T_DW_Data</c> on a short-list of status fields, then fan the per-BGPMT / per-invoice
    /// changes out to every reconciliation row that references them — so the UI can highlight
    /// the exact cell that moved.
    /// </para>
    /// </remarks>
    public sealed class SnapshotComparisonService
    {
        private readonly OfflineFirstService _ofs;
        private readonly SnapshotService _snapshots;

        public SnapshotComparisonService(OfflineFirstService ofs, SnapshotService snapshots)
        {
            _ofs = ofs ?? throw new ArgumentNullException(nameof(ofs));
            _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        }

        // Columns we diff on T_Reconciliation. ID + DeleteDate are metadata handled separately;
        // audit fields (Version, LastModified, ModifiedBy) are intentionally excluded — they flip
        // on every mutation and would swamp the diff with pure noise.
        private static readonly string[] ReconciliationDiffColumns = new[]
        {
            "DWINGS_GuaranteeID", "DWINGS_InvoiceID", "DWINGS_BGPMT",
            "Action", "Assignee", "Comments", "InternalInvoiceReference",
            "FirstClaimDate", "LastClaimDate", "ToRemind", "ToRemindDate",
            "ACK", "SwiftCode", "PaymentReference", "MbawData", "SpiritData",
            "KPI", "IncidentType", "RiskyItem", "ReasonNonRisky",
            "IncNumber", "TriggerDate", "ActionStatus", "ActionDate",
        };

        // DWINGS invoice fields we diff + their grid MappingName. The FieldName stored in the
        // emitted RowChange is the MappingName so the per-cell converter picks it up directly —
        // no translation layer needed downstream.
        private static readonly (string DbColumn, string FieldName)[] DwingsInvoiceDiffFields = new[]
        {
            ("MT_STATUS",                "I_MT_STATUS"),
            ("T_INVOICE_STATUS",         "I_T_INVOICE_STATUS"),
            ("T_PAYMENT_REQUEST_STATUS", "I_T_PAYMENT_REQUEST_STATUS"),
            ("PAYMENT_METHOD",           "I_PAYMENT_METHOD"),
            ("ERROR_MESSAGE",            "I_ERROR_MESSAGE"),
            ("COMM_ID_EMAIL",            "HasEmail"),
        };

        // DB-column array derived once — the OleDb SELECT list + the Format pass both consume it.
        private static readonly string[] DwingsInvoiceDbColumns =
            DwingsInvoiceDiffFields.Select(f => f.DbColumn).ToArray();

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the set of row IDs that differ between the last snapshot and the live
        /// <c>T_Reconciliation</c>. Used by <c>ReconciliationView</c> at load time to flag
        /// <c>HasRecentActivity</c> on each row — one DB read, no per-row lookups.
        /// </summary>
        public async Task<HashSet<string>> GetRowIdsChangedSinceLastRunAsync(string countryId)
        {
            // Derives the ID set from the full per-row diff so DWINGS-only churn still surfaces
            // here (and in the UI's left-edge indicator). Cost is one reco load + one DWINGS load
            // — same order of magnitude as the previous reco-only path.
            var byRow = await GetChangesByRowSinceLastRunAsync(countryId).ConfigureAwait(false);
            return new HashSet<string>(byRow.Keys, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns a <c>rowId → list of changes</c> map for the last run, computed in a single
        /// pass over the two DBs. Used by the per-row tooltip so each visible row can display its
        /// diff without further queries.
        /// </summary>
        public async Task<Dictionary<string, List<RowChange>>> GetChangesByRowSinceLastRunAsync(string countryId)
        {
            var map = new Dictionary<string, List<RowChange>>(StringComparer.OrdinalIgnoreCase);

            // Pull newer snapshots published by other users before resolving the last-run paths.
            // Rate-limited inside EnsureLatestAsync so repeated view refreshes don't hit the share.
            try { await _snapshots.EnsureLatestAsync(countryId).ConfigureAwait(false); }
            catch { /* best-effort — fall back to whatever local snapshots we already have */ }

            var paths = _snapshots.GetPathsForLastRun(countryId);
            if (paths == null || string.IsNullOrEmpty(paths.RecoPath) || !File.Exists(paths.RecoPath))
                return map;

            var liveCs = _ofs.GetCountryConnectionString(countryId);
            if (string.IsNullOrWhiteSpace(liveCs)) return map;

            try
            {
                var changes = await ComputeAllChangesAsync(countryId, paths, liveCs).ConfigureAwait(false);
                foreach (var c in changes)
                {
                    if (string.IsNullOrEmpty(c.RowId)) continue;
                    if (!map.TryGetValue(c.RowId, out var list))
                    {
                        list = new List<RowChange>(4);
                        map[c.RowId] = list;
                    }
                    list.Add(c);
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot diff (by-row) failed for {countryId}: {ex.Message}");
            }
            return map;
        }

        /// <summary>
        /// Full impact report for a given run. The panel UI binds directly to
        /// <see cref="RunImpactReport"/>.
        /// </summary>
        public async Task<RunImpactReport> GetImpactForRunAsync(string countryId, Guid runId)
        {
            var report = new RunImpactReport();

            // Pull first so a run just published by a colleague shows up in the impact panel
            // without the user having to manually refresh.
            try { await _snapshots.EnsureLatestAsync(countryId).ConfigureAwait(false); }
            catch { /* best-effort */ }

            var paths = _snapshots.GetPathsForRun(countryId, runId);
            if (paths == null) return report;

            var runs = _snapshots.ListRuns(countryId);
            report.Run = runs.FirstOrDefault(r => r.ImportRunId == runId);
            if (report.Run == null) return report;

            var liveCs = _ofs.GetCountryConnectionString(countryId);
            if (string.IsNullOrWhiteSpace(liveCs)) return report;

            try
            {
                var changes = await ComputeAllChangesAsync(countryId, paths, liveCs).ConfigureAwait(false);
                AggregateImpact(changes, report);
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot diff (impact) failed for {countryId}/{runId}: {ex.Message}");
            }
            return report;
        }

        /// <summary>
        /// Per-row history across every run whose snapshot is still on disk. Entries are returned
        /// newest first. Ideal for the tooltip + right-click "full history" popup.
        /// </summary>
        public async Task<IReadOnlyList<RowChange>> GetRowHistoryAsync(string countryId, string rowId)
        {
            var result = new List<RowChange>();
            if (string.IsNullOrWhiteSpace(rowId)) return result;

            // Pull shared snapshots so the history popup reflects ALL imports across users,
            // not just the ones this machine produced locally.
            try { await _snapshots.EnsureLatestAsync(countryId).ConfigureAwait(false); }
            catch { /* best-effort */ }

            var runs = _snapshots.ListRuns(countryId);
            if (runs.Count == 0) return result;

            var liveCs = _ofs.GetCountryConnectionString(countryId);
            if (string.IsNullOrWhiteSpace(liveCs)) return result;

            // Walk the runs from newest to oldest, diffing each consecutive pair.
            // "Most recent entry" == current vs latest snapshot.
            // "Next entry"         == latest snapshot vs one before, etc.
            try
            {
                var currentRow = await LoadSingleRowAsync(liveCs, "T_Reconciliation", ReconciliationDiffColumns, rowId, isConnStr: true).ConfigureAwait(false);

                for (int i = 0; i < runs.Count; i++)
                {
                    var snap = _snapshots.GetPathsForRun(countryId, runs[i].ImportRunId);
                    if (snap == null || string.IsNullOrEmpty(snap.RecoPath) || !File.Exists(snap.RecoPath))
                        continue;

                    var snapRow = await LoadSingleRowAsync(snap.RecoPath, "T_Reconciliation", ReconciliationDiffColumns, rowId).ConfigureAwait(false);
                    var diffs = BuildFieldDiffs(snapRow, currentRow, rowId, $"Run:{runs[i].ImportRunId}", runs[i].StartedUtc);
                    result.AddRange(diffs);

                    // Shift: current for the next iteration becomes the snapshot we just read,
                    // so the next diff describes what changed during THAT run.
                    currentRow = snapRow;
                    if (currentRow == null) break;
                }
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Snapshot row history failed for {countryId}/{rowId}: {ex.Message}");
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // Internals
        // ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes the full change list for a run: reconciliation row diff (added / archived /
        /// field-changed) + DWINGS invoice field diff fanned out to every reco row that references
        /// the affected BGPMT or INVOICE_ID.
        /// </summary>
        private async Task<List<RowChange>> ComputeAllChangesAsync(string countryId, SnapshotPaths paths, string liveRecoCs)
        {
            var changes = new List<RowChange>();
            // The change "happened" at snapshot time, not at diff-computation time. Using
            // StartedUtc here means every user computing the same diff sees the SAME date
            // ("Changed since <import timestamp>") — which is what the tooltip header shows.
            var runTs = paths.StartedUtc;

            // ── Reconciliation diff ───────────────────────────────────────────────────────────
            var oldRecoRows = await LoadRowsAsync(paths.RecoPath, "T_Reconciliation", ReconciliationDiffColumns, includeDeleteDate: true).ConfigureAwait(false);
            var newRecoRows = await LoadRowsAsync(liveRecoCs, "T_Reconciliation", ReconciliationDiffColumns, includeDeleteDate: true, isConnStr: true).ConfigureAwait(false);

            // Added since snapshot (row seen in live but not in snapshot, and not archived in live).
            foreach (var kv in newRecoRows)
            {
                if (oldRecoRows.ContainsKey(kv.Key)) continue;
                if (IsDeleted(kv.Value)) continue;
                changes.Add(new RowChange
                {
                    RowId = kv.Key,
                    Kind = ChangeKind.AmbreAdded,
                    Source = "Snapshot",
                    TimestampUtc = runTs,
                });
            }

            // Archived since snapshot (was non-null in snap, DeleteDate set in live).
            foreach (var kv in newRecoRows)
            {
                if (!oldRecoRows.TryGetValue(kv.Key, out var oldRow)) continue;
                if (IsDeleted(oldRow) || !IsDeleted(kv.Value)) continue;
                changes.Add(new RowChange
                {
                    RowId = kv.Key,
                    Kind = ChangeKind.AmbreArchived,
                    Source = "Snapshot",
                    TimestampUtc = runTs,
                });
            }

            // Field-level diffs on rows alive in both sides.
            foreach (var kv in newRecoRows)
            {
                if (!oldRecoRows.TryGetValue(kv.Key, out var oldRow)) continue;
                if (IsDeleted(oldRow) || IsDeleted(kv.Value)) continue;
                changes.AddRange(BuildFieldDiffs(oldRow, kv.Value, kv.Key, "Reco", runTs));
            }

            // ── DWINGS diff → fan out to reco rows ────────────────────────────────────────────
            try
            {
                var dwingsChanges = await ComputeDwingsChangesAsync(countryId, paths, newRecoRows, runTs).ConfigureAwait(false);
                if (dwingsChanges != null && dwingsChanges.Count > 0)
                    changes.AddRange(dwingsChanges);
            }
            catch (Exception ex)
            {
                // DWINGS diff is best-effort — a missing snapshot or a schema mismatch on the
                // shared DW must never block the reco-level indicator.
                LogManager.Warning($"Snapshot DWINGS diff skipped for {countryId}: {ex.Message}");
            }

            return changes;
        }

        /// <summary>
        /// Diffs <c>T_DW_Data</c> between snapshot and live on the curated status fields, then
        /// projects each change to every reco row that references the affected BGPMT or INVOICE_ID.
        /// The emitted <see cref="RowChange.FieldName"/> is the grid <c>MappingName</c> (e.g.
        /// <c>I_MT_STATUS</c>) so the per-cell converter matches it without translation.
        /// </summary>
        private async Task<List<RowChange>> ComputeDwingsChangesAsync(
            string countryId,
            SnapshotPaths paths,
            Dictionary<string, Dictionary<string, object>> liveRecoRows,
            DateTime ts)
        {
            var result = new List<RowChange>();

            // Need both a live DW DB AND a snapshotted one. Either missing → silent no-op.
            if (paths == null || string.IsNullOrEmpty(paths.DwingsPath) || !File.Exists(paths.DwingsPath))
                return result;

            var liveDwPath = _ofs.GetLocalDWDatabasePath(countryId);
            if (string.IsNullOrWhiteSpace(liveDwPath) || !File.Exists(liveDwPath))
                return result;

            // T_DW_Data PK is BGPMT. Load both sides keyed by BGPMT.
            var oldInvoices = await LoadDwingsDataAsync(paths.DwingsPath).ConfigureAwait(false);
            var newInvoices = await LoadDwingsDataAsync(liveDwPath).ConfigureAwait(false);

            if (oldInvoices.Count == 0 && newInvoices.Count == 0)
                return result;

            // Build reco lookups so each DW diff can be attributed to its referencing reco rows.
            // Same BGPMT or INVOICE_ID may be referenced by multiple reco rows → lists.
            var recoByBgpmt = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var recoByInvoice = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in liveRecoRows)
            {
                var row = kv.Value;
                if (IsDeleted(row)) continue; // archived reco → no UI visibility anyway

                row.TryGetValue("DWINGS_BGPMT", out var bgpmtObj);
                row.TryGetValue("DWINGS_InvoiceID", out var invObj);
                var bgpmt = Convert.ToString(bgpmtObj, CultureInfo.InvariantCulture);
                var inv = Convert.ToString(invObj, CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(bgpmt))
                {
                    if (!recoByBgpmt.TryGetValue(bgpmt, out var list)) recoByBgpmt[bgpmt] = list = new List<string>(1);
                    list.Add(kv.Key);
                }
                if (!string.IsNullOrWhiteSpace(inv))
                {
                    if (!recoByInvoice.TryGetValue(inv, out var list)) recoByInvoice[inv] = list = new List<string>(1);
                    list.Add(kv.Key);
                }
            }

            // Walk BGPMTs common to both sides, emit a RowChange per changed field per reco row.
            foreach (var kv in newInvoices)
            {
                if (!oldInvoices.TryGetValue(kv.Key, out var oldInv)) continue; // brand-new BGPMT
                var newInv = kv.Value;

                foreach (var field in DwingsInvoiceDiffFields)
                {
                    oldInv.TryGetValue(field.DbColumn, out var ov);
                    newInv.TryGetValue(field.DbColumn, out var nv);
                    if (ValuesEqual(ov, nv)) continue;

                    // Find every reco row that points to this BGPMT. Fall back to INVOICE_ID when
                    // the reco row only carries the invoice link.
                    var recoIds = ResolveRecoIdsForInvoice(kv.Key, newInv, recoByBgpmt, recoByInvoice);
                    if (recoIds.Count == 0) continue;

                    var oldFmt = FormatForField(field.FieldName, ov);
                    var newFmt = FormatForField(field.FieldName, nv);
                    foreach (var rid in recoIds)
                    {
                        result.Add(new RowChange
                        {
                            Id = Guid.NewGuid(),
                            RowId = rid,
                            Kind = ChangeKind.FieldChanged,
                            Source = "DWINGS",
                            FieldName = field.FieldName,
                            OldValue = oldFmt,
                            NewValue = newFmt,
                            TimestampUtc = ts,
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves the reco row IDs impacted by a DWINGS invoice change. Tries BGPMT first
        /// (1:1 match) then falls back to INVOICE_ID (1:N — one invoice can feed several reco
        /// rows when amounts are split). Returns a de-duped list.
        /// </summary>
        private static List<string> ResolveRecoIdsForInvoice(
            string bgpmt,
            Dictionary<string, object> invoiceRow,
            Dictionary<string, List<string>> recoByBgpmt,
            Dictionary<string, List<string>> recoByInvoice)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>(2);

            if (!string.IsNullOrEmpty(bgpmt) && recoByBgpmt.TryGetValue(bgpmt, out var byBgpmt))
                foreach (var id in byBgpmt) if (seen.Add(id)) result.Add(id);

            invoiceRow.TryGetValue("INVOICE_ID", out var invObj);
            var invId = Convert.ToString(invObj, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(invId) && recoByInvoice.TryGetValue(invId, out var byInv))
                foreach (var id in byInv) if (seen.Add(id)) result.Add(id);

            return result;
        }

        /// <summary>
        /// Loads <c>T_DW_Data</c> keyed by <c>BGPMT</c>. Also keeps <c>INVOICE_ID</c> in the row
        /// bag so <see cref="ResolveRecoIdsForInvoice"/> can look up the fallback join key without
        /// a second query.
        /// </summary>
        private static async Task<Dictionary<string, Dictionary<string, object>>> LoadDwingsDataAsync(string dbPath)
        {
            var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var cs = $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={dbPath};Mode=Read";

            var wanted = new List<string> { "BGPMT", "INVOICE_ID" };
            wanted.AddRange(DwingsInvoiceDbColumns);
            var selectList = string.Join(", ", wanted.Select(c => $"[{c}]"));

            using (var conn = new OleDbConnection(cs))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = new OleDbCommand($"SELECT {selectList} FROM [T_DW_Data]", conn))
                using (var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    var ords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in wanted)
                    {
                        try { ords[c] = rd.GetOrdinal(c); }
                        catch { /* missing column on older DW schema — skip */ }
                    }
                    if (!ords.TryGetValue("BGPMT", out var ordBgpmt)) return result;

                    while (await rd.ReadAsync().ConfigureAwait(false))
                    {
                        if (rd.IsDBNull(ordBgpmt)) continue;
                        var bgpmt = rd.GetValue(ordBgpmt)?.ToString();
                        if (string.IsNullOrEmpty(bgpmt)) continue;

                        var row = new Dictionary<string, object>(ords.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in ords)
                            row[kv.Key] = rd.IsDBNull(kv.Value) ? null : rd.GetValue(kv.Value);
                        result[bgpmt] = row;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Loads a table into <c>rowId → columnName → value</c>. Accepts either a direct file path
        /// (<c>isConnStr=false</c>) or a pre-built connection string (<c>isConnStr=true</c>).
        /// </summary>
        private static async Task<Dictionary<string, Dictionary<string, object>>> LoadRowsAsync(
            string source, string tableName, string[] columns, bool includeDeleteDate, bool isConnStr = false)
        {
            var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            var cs = isConnStr ? source : $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={source};Mode=Read";

            var cols = new List<string> { "ID" };
            cols.AddRange(columns);
            if (includeDeleteDate) cols.Add("DeleteDate");
            var selectList = string.Join(", ", cols.Select(c => $"[{c}]"));

            using (var conn = new OleDbConnection(cs))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = new OleDbCommand($"SELECT {selectList} FROM [{tableName}]", conn))
                using (var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    int ordId = rd.GetOrdinal("ID");
                    var fieldOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in cols)
                    {
                        try { fieldOrdinals[c] = rd.GetOrdinal(c); }
                        catch { /* column missing on older schemas, skip silently */ }
                    }

                    while (await rd.ReadAsync().ConfigureAwait(false))
                    {
                        if (rd.IsDBNull(ordId)) continue;
                        var id = rd.GetValue(ordId)?.ToString();
                        if (string.IsNullOrEmpty(id)) continue;

                        var row = new Dictionary<string, object>(fieldOrdinals.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in fieldOrdinals)
                        {
                            if (kv.Value < 0) continue;
                            row[kv.Key] = rd.IsDBNull(kv.Value) ? null : rd.GetValue(kv.Value);
                        }
                        result[id] = row;
                    }
                }
            }
            return result;
        }

        private static async Task<Dictionary<string, object>> LoadSingleRowAsync(
            string source, string tableName, string[] columns, string rowId, bool isConnStr = false)
        {
            var cs = isConnStr ? source : $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={source};Mode=Read";
            var cols = new List<string> { "ID" };
            cols.AddRange(columns);
            cols.Add("DeleteDate");
            var selectList = string.Join(", ", cols.Select(c => $"[{c}]"));

            using (var conn = new OleDbConnection(cs))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = new OleDbCommand($"SELECT {selectList} FROM [{tableName}] WHERE [ID]=?", conn))
                {
                    cmd.Parameters.Add("@Id", OleDbType.VarWChar, 255).Value = rowId;
                    using (var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        if (!await rd.ReadAsync().ConfigureAwait(false)) return null;

                        var row = new Dictionary<string, object>(cols.Count, StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < rd.FieldCount; i++)
                        {
                            var name = rd.GetName(i);
                            row[name] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                        }
                        return row;
                    }
                }
            }
        }

        private static IEnumerable<RowChange> BuildFieldDiffs(
            Dictionary<string, object> oldRow,
            Dictionary<string, object> newRow,
            string rowId, string source, DateTime ts)
        {
            // Row removed or added entirely — we only emit field diffs when both sides exist.
            if (oldRow == null || newRow == null) yield break;

            foreach (var col in ReconciliationDiffColumns)
            {
                oldRow.TryGetValue(col, out var ov);
                newRow.TryGetValue(col, out var nv);
                if (ValuesEqual(ov, nv)) continue;

                yield return new RowChange
                {
                    Id = Guid.NewGuid(),
                    RowId = rowId,
                    Kind = ChangeKind.FieldChanged,
                    Source = source,
                    FieldName = col,
                    OldValue = FormatForField(col, ov),
                    NewValue = FormatForField(col, nv),
                    TimestampUtc = ts,
                };
            }
        }

        private static bool IsDeleted(Dictionary<string, object> row)
        {
            if (row == null) return false;
            if (!row.TryGetValue("DeleteDate", out var v)) return false;
            return v != null;
        }

        /// <summary>
        /// Compares two boxed values. Handles <c>null</c>/<see cref="DBNull"/> as equivalent,
        /// promotes numeric comparisons to the widest type (so <c>int 1</c> == <c>double 1.0</c>),
        /// and compares <see cref="DateTime"/> at second precision to ignore OLE DB round-trip jitter.
        /// </summary>
        private static bool ValuesEqual(object a, object b)
        {
            if (IsNullish(a) && IsNullish(b)) return true;
            if (IsNullish(a) || IsNullish(b)) return false;

            if (a is DateTime da && b is DateTime db)
                return da.Ticks / TimeSpan.TicksPerSecond == db.Ticks / TimeSpan.TicksPerSecond;

            if (IsNumeric(a) && IsNumeric(b))
            {
                var x = Convert.ToDouble(a, CultureInfo.InvariantCulture);
                var y = Convert.ToDouble(b, CultureInfo.InvariantCulture);
                return Math.Abs(x - y) < 0.0001; // avoid floating-point false positives on amounts.
            }

            if (a is bool ba && b is bool bb) return ba == bb;

            return string.Equals(
                Convert.ToString(a, CultureInfo.InvariantCulture),
                Convert.ToString(b, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static bool IsNullish(object v) => v == null || v == DBNull.Value ||
                                                   (v is string s && s.Length == 0);

        private static bool IsNumeric(object v) =>
            v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint ||
            v is long || v is ulong || v is float || v is double || v is decimal;

        private static string Format(object v)
        {
            if (IsNullish(v)) return null;
            switch (v)
            {
                case DateTime dt: return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case decimal m:   return m.ToString(CultureInfo.InvariantCulture);
                case double d:    return d.ToString(CultureInfo.InvariantCulture);
                case float f:     return f.ToString(CultureInfo.InvariantCulture);
                case bool b:      return b ? "True" : "False";
                default:          return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Same contract as <see cref="Format"/> but routes by <paramref name="fieldName"/> so
        /// raw FK ids, booleans and dates become human-readable labels:
        /// <list type="bullet">
        ///   <item><c>Action / KPI / IncidentType / ReasonNonRisky</c> → UserField name via
        ///         <see cref="ViewDataEnricher.TryGetUserFieldName"/>.</item>
        ///   <item><c>Assignee</c> → user display name via
        ///         <see cref="ViewDataEnricher.TryGetAssigneeName"/>.</item>
        ///   <item><c>ActionStatus</c> → "DONE" / "PENDING".</item>
        ///   <item><c>ToRemind / ACK / RiskyItem / HasEmail</c> → "Yes" / "No".</item>
        ///   <item>Date-bearing columns → <c>dd/MM/yyyy HH:mm</c> (local time).</item>
        /// </list>
        /// Falls back to <see cref="Format"/> when:
        /// <list type="bullet">
        ///   <item>the field has no special handling (plain text / numeric columns),</item>
        ///   <item>the cache has not been primed yet (e.g. diff is triggered before the grid
        ///         enriches its data) — we emit the raw id so the user sees <b>something</b>
        ///         instead of an empty label.</item>
        /// </list>
        /// </summary>
        private static string FormatForField(string fieldName, object rawValue)
        {
            if (IsNullish(rawValue)) return null;
            if (string.IsNullOrEmpty(fieldName)) return Format(rawValue);

            // UserField FK columns — resolve id → name via the shared ViewDataEnricher cache.
            if (IsUserFieldColumn(fieldName))
            {
                int? id = TryCoerceInt(rawValue);
                if (id.HasValue)
                {
                    var name = ViewDataEnricher.TryGetUserFieldName(id);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
                return Format(rawValue); // cache miss → fall back to raw so the user sees the id
            }

            if (string.Equals(fieldName, "Assignee", StringComparison.OrdinalIgnoreCase))
            {
                var s = Convert.ToString(rawValue, CultureInfo.InvariantCulture);
                var name = ViewDataEnricher.TryGetAssigneeName(s);
                return string.IsNullOrEmpty(name) ? s : name;
            }

            if (string.Equals(fieldName, "ActionStatus", StringComparison.OrdinalIgnoreCase))
                return TryCoerceBool(rawValue) == true ? "DONE" : "PENDING";

            if (IsYesNoBoolColumn(fieldName))
            {
                var b = TryCoerceBool(rawValue);
                if (b.HasValue) return b.Value ? "Yes" : "No";
                return Format(rawValue);
            }

            // HasEmail on DWINGS side arrives as a possibly-non-empty string (the COMM_ID_EMAIL
            // raw value). Non-empty ⇒ Yes, empty ⇒ handled by the IsNullish check above.
            if (string.Equals(fieldName, "HasEmail", StringComparison.OrdinalIgnoreCase))
                return "Yes";

            // Dates: stored as DateTime in the reader. Use short local format — the ISO format
            // from Format() is correct but noisy for the diff badge.
            if (rawValue is DateTime dt && IsDateColumn(fieldName))
                return dt.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);

            return Format(rawValue);
        }

        private static bool IsUserFieldColumn(string fieldName) =>
            string.Equals(fieldName, "Action",         StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "KPI",            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "IncidentType",   StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "ReasonNonRisky", StringComparison.OrdinalIgnoreCase);

        private static bool IsYesNoBoolColumn(string fieldName) =>
            string.Equals(fieldName, "ToRemind",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "ACK",       StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "RiskyItem", StringComparison.OrdinalIgnoreCase);

        private static bool IsDateColumn(string fieldName) =>
            string.Equals(fieldName, "ActionDate",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "ToRemindDate",   StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "FirstClaimDate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "LastClaimDate",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fieldName, "TriggerDate",    StringComparison.OrdinalIgnoreCase);

        // Access stores booleans as byte/short/int depending on driver path; <see cref="Convert.ToBoolean"/>
        // handles all three. Any failure ⇒ null so the caller falls back to the raw display.
        private static bool? TryCoerceBool(object v)
        {
            if (IsNullish(v)) return null;
            try
            {
                if (v is bool b) return b;
                return Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        private static int? TryCoerceInt(object v)
        {
            if (IsNullish(v)) return null;
            try
            {
                if (v is int i) return i;
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        /// <summary>
        /// Groups raw changes by rule/field/alert buckets. Mirrors the aggregation used by the
        /// prior journal service so the UI bindings remain identical.
        /// </summary>
        private static void AggregateImpact(List<RowChange> entries, RunImpactReport report)
        {
            if (entries == null || entries.Count == 0) return;

            var fieldGroups = entries
                .Where(e => e.Kind == ChangeKind.FieldChanged && !string.IsNullOrWhiteSpace(e.FieldName))
                .GroupBy(e => e.FieldName, StringComparer.OrdinalIgnoreCase);

            foreach (var g in fieldGroups.OrderByDescending(g => g.Count()))
            {
                var impact = new FieldImpact
                {
                    FieldName = g.Key,
                    Count = g.Select(x => x.RowId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                };
                foreach (var rowId in g.Where(x => !string.IsNullOrEmpty(x.RowId))
                                       .Select(x => x.RowId)
                                       .Distinct(StringComparer.OrdinalIgnoreCase))
                    impact.RowIds.Add(rowId);
                report.MaterialChanges.Add(impact);
            }

            // RiskyItem and Action are now formatted with semantic labels (Yes/No and the
            // UserField name respectively) — but if the diff runs before the UI caches were
            // primed, the value falls back to the raw form. Accept both shapes so the alerts
            // fire consistently either way.
            AddAlertIfAny(report, "Flipped to RiskyItem",
                entries.Where(e => string.Equals(e.FieldName, "RiskyItem", StringComparison.OrdinalIgnoreCase)
                                && (string.Equals(e.NewValue, "Yes",  StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e.NewValue, "True", StringComparison.OrdinalIgnoreCase))));

            AddAlertIfAny(report, "Set to Investigate",
                entries.Where(e => string.Equals(e.FieldName, "Action", StringComparison.OrdinalIgnoreCase)
                                && (string.Equals(e.NewValue, "Investigate", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e.NewValue, "7",            StringComparison.Ordinal))));

            AddAlertIfAny(report, "FirstClaim opened",
                entries.Where(e => string.Equals(e.FieldName, "FirstClaimDate", StringComparison.OrdinalIgnoreCase)
                                && string.IsNullOrEmpty(e.OldValue)
                                && !string.IsNullOrEmpty(e.NewValue)));

            AddAlertIfAny(report, "Archived",
                entries.Where(e => e.Kind == ChangeKind.AmbreArchived));

            AddAlertIfAny(report, "Added",
                entries.Where(e => e.Kind == ChangeKind.AmbreAdded));

            // DWINGS signal — one alert per status kind so the panel highlights what moved
            // (and the user can click through to select the impacted rows). The FieldName for
            // each entry is the grid MappingName we set upstream, so the lookup is a string match.
            AddAlertIfAny(report, "MT status moved",
                entries.Where(e => string.Equals(e.FieldName, "I_MT_STATUS", StringComparison.OrdinalIgnoreCase)));

            AddAlertIfAny(report, "Invoice status changed",
                entries.Where(e => string.Equals(e.FieldName, "I_T_INVOICE_STATUS", StringComparison.OrdinalIgnoreCase)));

            AddAlertIfAny(report, "Payment request status changed",
                entries.Where(e => string.Equals(e.FieldName, "I_T_PAYMENT_REQUEST_STATUS", StringComparison.OrdinalIgnoreCase)));

            AddAlertIfAny(report, "Payment method changed",
                entries.Where(e => string.Equals(e.FieldName, "I_PAYMENT_METHOD", StringComparison.OrdinalIgnoreCase)));

            AddAlertIfAny(report, "New DWINGS error",
                entries.Where(e => string.Equals(e.FieldName, "I_ERROR_MESSAGE", StringComparison.OrdinalIgnoreCase)
                                && string.IsNullOrEmpty(e.OldValue)
                                && !string.IsNullOrEmpty(e.NewValue)));
        }

        private static void AddAlertIfAny(RunImpactReport report, string label, IEnumerable<RowChange> matches)
        {
            var ids = matches.Where(x => !string.IsNullOrEmpty(x.RowId))
                             .Select(x => x.RowId)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList();
            if (ids.Count == 0) return;
            var alert = new AlertImpact { Label = label, Count = ids.Count };
            alert.RowIds.AddRange(ids);
            report.Alerts.Add(alert);
        }
    }
}
