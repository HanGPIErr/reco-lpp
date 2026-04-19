using OfflineFirstAccess.Helpers;
using RecoTool.Models;
using RecoTool.Services.DTOs;
using RecoTool.Services.Rules;
using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Services
{
    /// <summary>
    /// CRUD operations for <see cref="Reconciliation"/> rows:
    /// <list type="bullet">
    /// <item><see cref="GetOrCreateReconciliationAsync"/> / <see cref="GetReconciliationByIdAsync"/> — reads with fallback.</item>
    /// <item><see cref="SaveReconciliationAsync(Reconciliation)"/>, batch + single variants — write pipeline with
    /// rule-evaluation hook on edit, ChangeLog recording, and materialised-view cache invalidation.</item>
    /// <item><see cref="SimulateRulesAsync"/> / <see cref="GetRuleDebugInfoAsync"/> — read-only rule evaluation
    /// helpers used by the diagnostics and rule-debug windows.</item>
    /// </list>
    /// </summary>
    public partial class ReconciliationService
    {
        /// <summary>
        /// Loads an existing reconciliation row by ID, or returns an in-memory stub created from the
        /// matching Ambre line when no row has been persisted yet. Callers normally feed the result back
        /// into <see cref="SaveReconciliationsAsync"/> after edits — the stub will INSERT, the existing row UPDATEs.
        /// </summary>
        public async Task<Reconciliation> GetOrCreateReconciliationAsync(string id)
        {
            var query = "SELECT * FROM T_Reconciliation WHERE ID = ? AND DeleteDate IS NULL";
            // Explicit connection-string overload to avoid the (query, id) overload being resolved by mistake.
            var existing = await _queryExecutor.QueryAsync<Reconciliation>(query, _connectionString, id).ConfigureAwait(false);

            if (existing.Any())
                return existing.First();

            return Reconciliation.CreateForAmbreLine(id);
        }

        /// <summary>
        /// Fetches a reconciliation row by ID without falling back to a stub. Returns <c>null</c> when the row
        /// does not exist (unlike <see cref="GetOrCreateReconciliationAsync"/>).
        /// </summary>
        public async Task<Reconciliation> GetReconciliationByIdAsync(string countryId, string id)
        {
            var query = "SELECT * FROM T_Reconciliation WHERE ID = ? AND DeleteDate IS NULL";
            var existing = await _queryExecutor.QueryAsync<Reconciliation>(query, _connectionString, id).ConfigureAwait(false);
            return existing.FirstOrDefault();
        }

        /// <summary>
        /// Dry-run rule evaluation over the given IDs (or every active row when <paramref name="ids"/> is null).
        /// Never writes to the database — only evaluates and returns the per-row outcome. Uses
        /// <see cref="RulesEngine.EvaluateAllForDebugAsync"/> so dead rules and non-matching reasons surface in
        /// the debug UI.
        /// </summary>
        public async Task<List<RuleSimulationRow>> SimulateRulesAsync(
            IEnumerable<string> ids,
            RuleScope scope,
            IProgress<(int done, int total)> progress = null,
            CancellationToken ct = default)
        {
            var result = new List<RuleSimulationRow>();
            try
            {
                var currentCountryId = _offlineFirstService?.CurrentCountryId;
                if (string.IsNullOrWhiteSpace(currentCountryId) || _rulesEngine == null) return result;
                if (_countries == null || !_countries.TryGetValue(currentCountryId, out var country) || country == null) return result;

                List<string> targetIds;
                if (ids != null)
                {
                    targetIds = ids.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                else
                {
                    // All active rows in current country
                    var view = await GetReconciliationViewAsync(currentCountryId, null, false).ConfigureAwait(false);
                    targetIds = (view ?? new List<ReconciliationViewData>())
                        .Where(v => v != null && !v.IsDeleted && !string.IsNullOrWhiteSpace(v.ID))
                        .Select(v => v.ID).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                if (targetIds.Count == 0) return result;

                // Pre-warm DWINGS caches to avoid per-row loads
                await EnsureDwingsCachesInitializedAsync().ConfigureAwait(false);

                int done = 0;
                int total = targetIds.Count;
                foreach (var id in targetIds)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var amb = await GetAmbreRowByIdAsync(currentCountryId, id).ConfigureAwait(false);
                        if (amb == null || amb.IsDeleted) continue;

                        var reco = await GetOrCreateReconciliationAsync(id).ConfigureAwait(false);
                        if (reco == null) continue;

                        bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);
                        var ctx = await BuildRuleContextAsync(amb, reco, country, currentCountryId, isPivot).ConfigureAwait(false);

                        var res = await _rulesEngine.EvaluateAsync(ctx, scope, ct).ConfigureAwait(false);
                        result.Add(new RuleSimulationRow
                        {
                            ReconciliationId = id,
                            IsPivot = isPivot,
                            MatchedRuleId = res?.Rule?.RuleId,
                            MatchedRulePriority = res?.Rule?.Priority,
                            ProposedActionId = res?.NewActionIdSelf,
                            ProposedKpiId = res?.NewKpiIdSelf,
                            ApplyTo = res?.Rule?.ApplyTo,
                            CurrentActionId = reco.Action,
                            CurrentKpiId = reco.KPI,
                            UserMessage = res?.UserMessage
                        });
                    }
                    catch { /* swallow per-row */ }
                    finally
                    {
                        done++;
                        try { progress?.Report((done, total)); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SimulateRulesAsync failed: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Returns the full evaluation trail for a single reconciliation ID: the assembled
        /// <see cref="RuleContext"/> and the list of every rule evaluated with its pass/fail reasons.
        /// Used by the rule debugger UI to surface why a given rule did or did not match.
        /// </summary>
        public async Task<(RuleContext Context, List<RuleDebugEvaluation> Evaluations)> GetRuleDebugInfoAsync(string reconciliationId)
        {
            try
            {
                var currentCountryId = _offlineFirstService?.CurrentCountryId;
                if (string.IsNullOrWhiteSpace(currentCountryId) || _rulesEngine == null)
                    return (null, null);
                if (_countries == null || !_countries.TryGetValue(currentCountryId, out var countryCtx) || countryCtx == null)
                    return (null, null);

                var amb = await GetAmbreRowByIdAsync(currentCountryId, reconciliationId).ConfigureAwait(false);
                var r = await GetOrCreateReconciliationAsync(reconciliationId).ConfigureAwait(false);
                if (amb == null || r == null) return (null, null);

                bool isPivot = amb.IsPivotAccount(countryCtx.CNT_AmbrePivot);
                var ctx = await BuildRuleContextAsync(amb, r, countryCtx, currentCountryId, isPivot).ConfigureAwait(false);
                var evaluations = await _rulesEngine.EvaluateAllForDebugAsync(ctx, RuleScope.Edit).ConfigureAwait(false);
                return (ctx, evaluations);
            }
            catch { return (null, null); }
        }

        /// <summary>Saves a single reconciliation, applying edit-scope rules by default. Thin wrapper over the batch API.</summary>
        public async Task<bool> SaveReconciliationAsync(Reconciliation reconciliation)
        {
            return await SaveReconciliationsAsync(new[] { reconciliation });
        }

        /// <summary>Saves a single reconciliation, explicit control over whether edit-scope rules run.</summary>
        public async Task<bool> SaveReconciliationAsync(Reconciliation reconciliation, bool applyRulesOnEdit)
        {
            return await SaveReconciliationsAsync(new[] { reconciliation }, applyRulesOnEdit);
        }

        /// <summary>
        /// Batch save pipeline. For each row:
        /// <list type="number">
        /// <item>Optionally evaluates edit-scope rules (applied + logged) when <paramref name="applyRulesOnEdit"/>.</item>
        /// <item>INSERT or UPDATE inside a single transaction via <see cref="SaveSingleReconciliationAsync"/>.</item>
        /// <item>Records a ChangeLog entry per modified row so the background sync can push them later.</item>
        /// </list>
        /// After commit, invalidates both the view-level caches (<see cref="InvalidateReconciliationViewCache(string)"/>)
        /// and the in-memory <c>_recoViewDataCache</c>, then patches the cached view rows in place via
        /// <see cref="UpdateRecoViewCaches"/> to keep the UI reactive without a full reload.
        /// <para>
        /// Rule-engine errors are swallowed so a buggy rule cannot block the user save; ChangeLog failures are
        /// also swallowed but logged (they would cause background sync to miss the row until reconstruction).
        /// </para>
        /// </summary>
        public async Task<bool> SaveReconciliationsAsync(IEnumerable<Reconciliation> reconciliations, bool applyRulesOnEdit = true)
        {
            try
            {
                using (var connection = new OleDbConnection(_connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);
                    using (var transaction = connection.BeginTransaction())
                    {
                        try
                        {
                            var changeTuples = new List<(string TableName, string RecordId, string OperationType)>();
                            var updatedRows = new List<Reconciliation>();
                            foreach (var reconciliation in reconciliations)
                            {
                                if (applyRulesOnEdit)
                                {
                                    try
                                    {
                                        var currentCountryId = _offlineFirstService?.CurrentCountryId;
                                        if (!string.IsNullOrWhiteSpace(currentCountryId) && _rulesEngine != null)
                                        {
                                            Country countryCtx = null;
                                            if (_countries != null && _countries.TryGetValue(currentCountryId, out var c)) countryCtx = c;
                                            if (countryCtx != null)
                                            {
                                                var amb = await GetAmbreRowByIdAsync(currentCountryId, reconciliation.ID).ConfigureAwait(false);
                                                if (amb != null)
                                                {
                                                    bool isPivot = amb.IsPivotAccount(countryCtx.CNT_AmbrePivot);
                                                    var ctx = await BuildRuleContextAsync(amb, reconciliation, countryCtx, currentCountryId, isPivot).ConfigureAwait(false);
                                                    var res = await _rulesEngine.EvaluateAsync(ctx, RuleScope.Edit).ConfigureAwait(false);
                                                    RuleApplicationHelper.ApplyAndLog(res, reconciliation, _currentUser, "edit", currentCountryId, RaiseRuleApplied, ProposalRepository);
                                                    if (res?.NewActionIdSelf.HasValue == true) EnsureActionDefaults(reconciliation);
                                                }
                                            }
                                        }
                                    }
                                    catch { /* do not block user saves on rules engine errors */ }
                                }

                                var op = await SaveSingleReconciliationAsync(connection, transaction, reconciliation).ConfigureAwait(false);
                                if (!string.Equals(op, "NOOP", StringComparison.OrdinalIgnoreCase))
                                {
                                    changeTuples.Add(("T_Reconciliation", reconciliation.ID, op));
                                    updatedRows.Add(reconciliation);
                                }
                            }

                            transaction.Commit();

                            // Invalidate caches so next view refresh recomputes flags (e.g., IsMatchedAcrossAccounts)
                            try
                            {
                                var countryId = _offlineFirstService?.CurrentCountryId;
                                if (!string.IsNullOrWhiteSpace(countryId))
                                    InvalidateReconciliationViewCache(countryId);
                                else
                                    InvalidateReconciliationViewCache();
                            }
                            catch { }

                            // Record changes in ChangeLog (stored locally via OfflineFirstService configuration)
                            try
                            {
                                if (_offlineFirstService != null && changeTuples.Count > 0)
                                {
                                    var countryId = _offlineFirstService.CurrentCountryId;
                                    if (!string.IsNullOrWhiteSpace(countryId))
                                    {
                                        using (var session = await _offlineFirstService.BeginChangeLogSessionAsync(countryId).ConfigureAwait(false))
                                        {
                                            foreach (var t in changeTuples)
                                            {
                                                await session.AddAsync(t.TableName, t.RecordId, t.OperationType).ConfigureAwait(false);
                                            }
                                            await session.CommitAsync().ConfigureAwait(false);
                                        }
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                // Swallow change-log errors to not block user saves.
                                // Diagnostic only: log once to help track missing pushes (background sync reads ChangeLog).
                                try { LogManager.Warning("ChangeLog recording failed in SaveReconciliationsAsync; background sync will skip these rows unless reconstructed."); } catch { }
                            }

                            // Invalidate Lazy<Task> coalescing cache so next loads fetch fresh data from DB
                            try { _recoViewCache.Clear(); } catch { }

                            // Incrementally update materialized view lists with the modified reconciliation fields
                            try { UpdateRecoViewCaches(updatedRows); } catch { }

                            // Synchronization is handled by background services (e.g., SyncMonitor),
                            // which read pending items from ChangeLog and then perform PUSH followed by PULL.
                            // No direct push is triggered here.
                            return true;
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Erreur lors de la sauvegarde des réconciliations: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// After a successful save, patches the already-cached view rows in place so the UI reflects the
        /// change without reloading from disk. Only Reconciliation-side columns are updated — Ambre columns
        /// come from a different table and do not change on save.
        /// </summary>
        private void UpdateRecoViewCaches(IEnumerable<Reconciliation> updated)
        {
            if (updated == null) return;
            foreach (var kv in _recoViewDataCache)
            {
                var list = kv.Value;
                if (list == null) continue;
                // Update in place by ID (AMBRE row always exists; reconciliation fields are nullable)
                foreach (var r in updated)
                {
                    var row = list.FirstOrDefault(x => string.Equals(x.ID, r.ID, StringComparison.OrdinalIgnoreCase));
                    if (row == null) continue;
                    row.DWINGS_GuaranteeID = r.DWINGS_GuaranteeID;
                    row.DWINGS_InvoiceID = r.DWINGS_InvoiceID;
                    row.DWINGS_BGPMT = r.DWINGS_BGPMT;
                    row.Action = r.Action;
                    row.ActionStatus = r.ActionStatus;
                    row.ActionDate = r.ActionDate;
                    row.Assignee = r.Assignee;
                    row.Comments = r.Comments;
                    row.InternalInvoiceReference = r.InternalInvoiceReference;
                    row.FirstClaimDate = r.FirstClaimDate;
                    row.LastClaimDate = r.LastClaimDate;
                    row.ToRemind = r.ToRemind;
                    row.ToRemindDate = r.ToRemindDate;
                    row.ACK = r.ACK;
                    row.SwiftCode = r.SwiftCode;
                    row.PaymentReference = r.PaymentReference;
                    row.KPI = r.KPI;
                    row.IncidentType = r.IncidentType;
                    row.RiskyItem = r.RiskyItem == true;
                    row.ReasonNonRisky = r.ReasonNonRisky;
                    row.IncNumber = r.IncNumber;
                    row.MbawData = r.MbawData;
                    row.SpiritData = r.SpiritData;
                    row.TriggerDate = r.TriggerDate;
                }
            }
        }

        /// <summary>
        /// Core UPSERT for a single reconciliation in the context of an open OleDb transaction.
        /// Returns the change operation type string used by the ChangeLog:
        /// <list type="bullet">
        /// <item><c>"NOOP"</c> — existing row with no business-field change (no UPDATE executed).</item>
        /// <item><c>"UPDATE(field1,field2,…)"</c> — partial update encoding the changed columns for sync.</item>
        /// <item><c>"INSERT"</c> — new row with full column list.</item>
        /// </list>
        /// UPDATE is dynamic to avoid touching untouched columns (reduces write amplification and
        /// preserves concurrent edits on unrelated columns).
        /// </summary>
        private async Task<string> SaveSingleReconciliationAsync(OleDbConnection connection, OleDbTransaction transaction, Reconciliation reconciliation)
        {
            // Vérifier si l'enregistrement existe (par ID)
            var checkQuery = "SELECT COUNT(*) FROM T_Reconciliation WHERE ID = ?";
            using (var checkCmd = new OleDbCommand(checkQuery, connection, transaction))
            {
                checkCmd.Parameters.AddWithValue("@ID", reconciliation.ID);
                var exists = (int)await checkCmd.ExecuteScalarAsync().ConfigureAwait(false) > 0;

                // If the row exists, compare business fields to avoid no-op updates
                if (exists)
                {
                    var changed = new List<string>();
                    var selectCmd = new OleDbCommand(@"SELECT 
                                [DWINGS_GuaranteeID], [DWINGS_InvoiceID], [DWINGS_BGPMT],
                                [Action], [ActionStatus], [ActionDate], [Assignee], [Comments], [InternalInvoiceReference],
                                [FirstClaimDate], [LastClaimDate], [ToRemind], [ToRemindDate],
                                [ACK], [SwiftCode], [PaymentReference], [KPI],
                                [IncidentType], [RiskyItem], [ReasonNonRisky], [IncNumber],
                                [MbawData], [SpiritData], [TriggerDate],
                                [LastModifiedByUser], [UserEditedFields], [LastRuleAppliedId], [LastRuleAppliedAt]
                              FROM T_Reconciliation WHERE [ID] = ?", connection, transaction);
                    selectCmd.Parameters.AddWithValue("@ID", reconciliation.ID);
                    using (var rdr = await selectCmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        if (await rdr.ReadAsync().ConfigureAwait(false))
                        {
                            object DbVal(int i) => rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                            bool Equal(object a, object b) => (a == null && b == null) || (a != null && a.Equals(b));

                            bool? DbBool(object o)
                            {
                                if (o == null) return null;
                                try
                                {
                                    if (o is bool bb) return bb;
                                    if (o is byte by) return by != 0;
                                    if (o is short s) return s != 0;
                                    if (o is int ii) return ii != 0;
                                    return Convert.ToBoolean(o);
                                }
                                catch { return null; }
                            }

                            // Build the list of changed business fields
                            if (!Equal(DbVal(0), (object)reconciliation.DWINGS_GuaranteeID)) changed.Add("DWINGS_GuaranteeID");
                            if (!Equal(DbVal(1), (object)reconciliation.DWINGS_InvoiceID)) changed.Add("DWINGS_InvoiceID");
                            if (!Equal(DbVal(2), (object)reconciliation.DWINGS_BGPMT)) changed.Add("DWINGS_BGPMT");
                            if (!Equal(DbVal(3), (object)reconciliation.Action)) changed.Add("Action");
                            if (!Equal(DbVal(4), (object)reconciliation.ActionStatus)) changed.Add("ActionStatus");
                            if (!Equal(DbVal(5), reconciliation.ActionDate.HasValue ? (object)reconciliation.ActionDate.Value : null)) changed.Add("ActionDate");
                            if (!Equal(DbVal(6), (object)reconciliation.Assignee)) changed.Add("Assignee");
                            if (!Equal(DbVal(7), (object)reconciliation.Comments)) changed.Add("Comments");
                            if (!Equal(DbVal(8), (object)reconciliation.InternalInvoiceReference)) changed.Add("InternalInvoiceReference");
                            if (!Equal(DbVal(9), reconciliation.FirstClaimDate.HasValue ? (object)reconciliation.FirstClaimDate.Value : null)) changed.Add("FirstClaimDate");
                            if (!Equal(DbVal(10), reconciliation.LastClaimDate.HasValue ? (object)reconciliation.LastClaimDate.Value : null)) changed.Add("LastClaimDate");
                            if (!Equal(DbBool(DbVal(11)), (object)reconciliation.ToRemind)) changed.Add("ToRemind");
                            if (!Equal(DbVal(12), reconciliation.ToRemindDate.HasValue ? (object)reconciliation.ToRemindDate.Value : null)) changed.Add("ToRemindDate");
                            if (!Equal(DbBool(DbVal(13)), (object)reconciliation.ACK)) changed.Add("ACK");
                            if (!Equal(DbVal(14), (object)reconciliation.SwiftCode)) changed.Add("SwiftCode");
                            if (!Equal(DbVal(15), (object)reconciliation.PaymentReference)) changed.Add("PaymentReference");
                            if (!Equal(DbVal(16), (object)reconciliation.KPI)) changed.Add("KPI");
                            if (!Equal(DbVal(17), (object)reconciliation.IncidentType)) changed.Add("IncidentType");
                            if (!Equal(DbBool(DbVal(18)), (object)reconciliation.RiskyItem)) changed.Add("RiskyItem");
                            if (!Equal(DbVal(19), (object)reconciliation.ReasonNonRisky)) changed.Add("ReasonNonRisky");
                            if (!Equal(DbVal(20), (object)reconciliation.IncNumber)) changed.Add("IncNumber");
                            if (!Equal(DbVal(21), (object)reconciliation.MbawData)) changed.Add("MbawData");
                            if (!Equal(DbVal(22), (object)reconciliation.SpiritData)) changed.Add("SpiritData");
                            if (!Equal(DbVal(23), reconciliation.TriggerDate.HasValue ? (object)reconciliation.TriggerDate.Value : null)) changed.Add("TriggerDate");
                            // Audit & user-edit protection columns (ordinals 24..27)
                            if (!Equal(DbVal(24), reconciliation.LastModifiedByUser.HasValue ? (object)reconciliation.LastModifiedByUser.Value : null)) changed.Add("LastModifiedByUser");
                            if (!Equal(DbVal(25), (object)reconciliation.UserEditedFields)) changed.Add("UserEditedFields");
                            if (!Equal(DbVal(26), (object)reconciliation.LastRuleAppliedId)) changed.Add("LastRuleAppliedId");
                            if (!Equal(DbVal(27), reconciliation.LastRuleAppliedAt.HasValue ? (object)reconciliation.LastRuleAppliedAt.Value : null)) changed.Add("LastRuleAppliedAt");

                            if (changed.Count == 0)
                            {
                                // No business-field change: skip UPDATE and ChangeLog
                                LogManager.Debug($"Reconciliation NOOP: ID={reconciliation.ID} - no business-field changes detected.");
                                return "NOOP";
                            }
                        }
                    }

                    // Apply update with refreshed modification metadata (partial update of changed fields only)
                    LogManager.Debug($"Reconciliation UPDATE detected: ID={reconciliation.ID} Changed=[{string.Join(",", changed)}]");
                    reconciliation.ModifiedBy = _currentUser;
                    reconciliation.LastModified = DateTime.UtcNow;

                    // Build dynamic UPDATE statement
                    var setClauses = new List<string>();
                    foreach (var col in changed)
                    {
                        setClauses.Add($"[{col}] = ?");
                    }
                    // Always update metadata
                    setClauses.Add("[ModifiedBy] = ?");
                    setClauses.Add("[LastModified] = ?");
                    var updateQuery = $"UPDATE T_Reconciliation SET {string.Join(", ", setClauses)} WHERE [ID] = ?";

                    using (var cmd = new OleDbCommand(updateQuery, connection, transaction))
                    {
                        // Add parameters in the same order as placeholders
                        foreach (var col in changed)
                        {
                            switch (col)
                            {
                                case "DWINGS_GuaranteeID":
                                    cmd.Parameters.AddWithValue("@DWINGS_GuaranteeID", reconciliation.DWINGS_GuaranteeID ?? (object)DBNull.Value);
                                    break;
                                case "DWINGS_InvoiceID":
                                    cmd.Parameters.AddWithValue("@DWINGS_InvoiceID", reconciliation.DWINGS_InvoiceID ?? (object)DBNull.Value);
                                    break;
                                case "DWINGS_BGPMT":
                                    cmd.Parameters.AddWithValue("@DWINGS_BGPMT", reconciliation.DWINGS_BGPMT ?? (object)DBNull.Value);
                                    break;
                                case "Action":
                                    cmd.Parameters.AddWithValue("@Action", reconciliation.Action ?? (object)DBNull.Value);
                                    break;
                                case "ActionStatus":
                                    {
                                        var p = cmd.Parameters.Add("@ActionStatus", OleDbType.Boolean);
                                        p.Value = reconciliation.ActionStatus.HasValue ? (object)reconciliation.ActionStatus.Value : DBNull.Value;
                                        break;
                                    }
                                case "ActionDate":
                                    {
                                        var p = cmd.Parameters.Add("@ActionDate", OleDbType.Date);
                                        p.Value = reconciliation.ActionDate.HasValue ? (object)reconciliation.ActionDate.Value : DBNull.Value;
                                        break;
                                    }
                                case "Assignee":
                                    cmd.Parameters.AddWithValue("@Assignee", string.IsNullOrWhiteSpace(reconciliation.Assignee) ? (object)DBNull.Value : reconciliation.Assignee);
                                    break;
                                case "Comments":
                                    {
                                        var p = cmd.Parameters.Add("@Comments", OleDbType.LongVarWChar);
                                        p.Value = reconciliation.Comments ?? (object)DBNull.Value;
                                        break;
                                    }
                                case "InternalInvoiceReference":
                                    cmd.Parameters.AddWithValue("@InternalInvoiceReference", reconciliation.InternalInvoiceReference ?? (object)DBNull.Value);
                                    break;
                                case "FirstClaimDate":
                                    {
                                        var p = cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date);
                                        p.Value = reconciliation.FirstClaimDate.HasValue ? (object)reconciliation.FirstClaimDate.Value : DBNull.Value;
                                        break;
                                    }
                                case "LastClaimDate":
                                    {
                                        var p = cmd.Parameters.Add("@LastClaimDate", OleDbType.Date);
                                        p.Value = reconciliation.LastClaimDate.HasValue ? (object)reconciliation.LastClaimDate.Value : DBNull.Value;
                                        break;
                                    }
                                case "ToRemind":
                                    {
                                        var p = cmd.Parameters.Add("@ToRemind", OleDbType.Boolean);
                                        p.Value = reconciliation.ToRemind;
                                        break;
                                    }
                                case "ToRemindDate":
                                    {
                                        var p = cmd.Parameters.Add("@ToRemindDate", OleDbType.Date);
                                        p.Value = reconciliation.ToRemindDate.HasValue ? (object)reconciliation.ToRemindDate.Value : DBNull.Value;
                                        break;
                                    }
                                case "ACK":
                                    {
                                        var p = cmd.Parameters.Add("@ACK", OleDbType.Boolean);
                                        p.Value = reconciliation.ACK;
                                        break;
                                    }
                                case "SwiftCode":
                                    cmd.Parameters.AddWithValue("@SwiftCode", reconciliation.SwiftCode ?? (object)DBNull.Value);
                                    break;
                                case "PaymentReference":
                                    cmd.Parameters.AddWithValue("@PaymentReference", reconciliation.PaymentReference ?? (object)DBNull.Value);
                                    break;
                                case "MbawData":
                                    {
                                        var p = cmd.Parameters.Add("@MbawData", OleDbType.LongVarWChar);
                                        p.Value = reconciliation.MbawData ?? (object)DBNull.Value;
                                        break;
                                    }
                                case "SpiritData":
                                    {
                                        var p = cmd.Parameters.Add("@SpiritData", OleDbType.LongVarWChar);
                                        p.Value = reconciliation.SpiritData ?? (object)DBNull.Value;
                                        break;
                                    }
                                case "KPI":
                                    {
                                        var p = cmd.Parameters.Add("@KPI", OleDbType.Integer);
                                        p.Value = reconciliation.KPI.HasValue ? (object)reconciliation.KPI.Value : DBNull.Value;
                                        break;
                                    }
                                case "IncidentType":
                                    {
                                        var p = cmd.Parameters.Add("@IncidentType", OleDbType.Integer);
                                        p.Value = reconciliation.IncidentType.HasValue ? (object)reconciliation.IncidentType.Value : DBNull.Value;
                                        break;
                                    }
                                case "RiskyItem":
                                    {
                                        var p = cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean);
                                        p.Value = reconciliation.RiskyItem.HasValue ? (object)reconciliation.RiskyItem.Value : DBNull.Value;
                                        break;
                                    }
                                case "ReasonNonRisky":
                                    {
                                        var p = cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer);
                                        p.Value = reconciliation.ReasonNonRisky.HasValue ? (object)reconciliation.ReasonNonRisky.Value : DBNull.Value;
                                        break;
                                    }
                                case "IncNumber":
                                    cmd.Parameters.AddWithValue("@IncNumber", reconciliation.IncNumber ?? (object)DBNull.Value);
                                    break;
                                case "TriggerDate":
                                    {
                                        var p = cmd.Parameters.Add("@TriggerDate", OleDbType.Date);
                                        p.Value = reconciliation.TriggerDate.HasValue ? (object)reconciliation.TriggerDate.Value : DBNull.Value;
                                        break;
                                    }
                                case "LastModifiedByUser":
                                    {
                                        var p = cmd.Parameters.Add("@LastModifiedByUser", OleDbType.Date);
                                        p.Value = reconciliation.LastModifiedByUser.HasValue ? (object)reconciliation.LastModifiedByUser.Value : DBNull.Value;
                                        break;
                                    }
                                case "UserEditedFields":
                                    cmd.Parameters.AddWithValue("@UserEditedFields", reconciliation.UserEditedFields ?? (object)DBNull.Value);
                                    break;
                                case "LastRuleAppliedId":
                                    cmd.Parameters.AddWithValue("@LastRuleAppliedId", reconciliation.LastRuleAppliedId ?? (object)DBNull.Value);
                                    break;
                                case "LastRuleAppliedAt":
                                    {
                                        var p = cmd.Parameters.Add("@LastRuleAppliedAt", OleDbType.Date);
                                        p.Value = reconciliation.LastRuleAppliedAt.HasValue ? (object)reconciliation.LastRuleAppliedAt.Value : DBNull.Value;
                                        break;
                                    }
                            }
                        }

                        // Metadata
                        cmd.Parameters.AddWithValue("@ModifiedBy", reconciliation.ModifiedBy ?? (object)DBNull.Value);
                        var pMod = cmd.Parameters.Add("@LastModified", OleDbType.Date);
                        pMod.Value = reconciliation.LastModified.HasValue ? (object)reconciliation.LastModified.Value : DBNull.Value;

                        // WHERE ID
                        cmd.Parameters.AddWithValue("@ID", reconciliation.ID);

                        // Debug SQL and parameters
                        try
                        {
                            var paramDbg = string.Join(" | ", cmd.Parameters
                                .Cast<OleDbParameter>()
                                .Select(p =>
                                {
                                    var val = p.Value;
                                    string display = val == null || val is DBNull ? "NULL" : (val is byte[] b ? $"byte[{b.Length}]" : val.ToString());
                                    return $"{p.ParameterName} type={p.OleDbType} value={display}";
                                }));
                            LogManager.Debug($"Reconciliation UPDATE SQL: {updateQuery} | Params: {paramDbg}");
                        }
                        catch { }

                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        // Encode changed fields for partial update during sync
                        var op = $"UPDATE({string.Join(",", changed)})";
                        LogManager.Debug($"Reconciliation UPDATE operation encoded: {op}");
                        return op;
                    }
                }
                else
                {
                    // Prepare metadata for insert
                    if (!reconciliation.CreationDate.HasValue)
                        reconciliation.CreationDate = DateTime.UtcNow;
                    reconciliation.ModifiedBy = _currentUser;
                    reconciliation.LastModified = DateTime.UtcNow;

                    var insertQuery = @"INSERT INTO T_Reconciliation 
                             ([ID], [DWINGS_GuaranteeID], [DWINGS_InvoiceID], [DWINGS_BGPMT],
                              [Action], [ActionStatus], [ActionDate], [Assignee], [Comments], [InternalInvoiceReference], [FirstClaimDate], [LastClaimDate],
                              [ToRemind], [ToRemindDate], [ACK], [SwiftCode], [PaymentReference], [MbawData], [SpiritData], [KPI],
                              [IncidentType], [RiskyItem], [ReasonNonRisky], [TriggerDate],
                              [LastModifiedByUser], [UserEditedFields], [LastRuleAppliedId], [LastRuleAppliedAt],
                              [CreationDate], [ModifiedBy], [LastModified])
                             VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";

                    using (var cmd = new OleDbCommand(insertQuery, connection, transaction))
                    {
                        AddReconciliationParameters(cmd, reconciliation, isInsert: true);
                        LogManager.Debug($"Reconciliation INSERT: ID={reconciliation.ID}");
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        return "INSERT";
                    }
                }
            }
        }

        /// <summary>
        /// Binds all parameters needed for the full INSERT statement of a reconciliation row, matching the
        /// column list in <see cref="SaveSingleReconciliationAsync"/>. Not used for dynamic UPDATE — that
        /// path builds its parameter list from the diff of changed columns.
        /// </summary>
        private void AddReconciliationParameters(OleDbCommand cmd, Reconciliation reconciliation, bool isInsert)
        {
            if (isInsert)
            {
                // ID as stable key
                cmd.Parameters.AddWithValue("@ID", reconciliation.ID ?? (object)DBNull.Value);
            }

            cmd.Parameters.AddWithValue("@DWINGS_GuaranteeID", reconciliation.DWINGS_GuaranteeID ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DWINGS_InvoiceID", reconciliation.DWINGS_InvoiceID ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DWINGS_BGPMT", reconciliation.DWINGS_BGPMT ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Action", reconciliation.Action ?? (object)DBNull.Value);
            var pActionStatus = cmd.Parameters.Add("@ActionStatus", OleDbType.Boolean);
            pActionStatus.Value = reconciliation.ActionStatus.HasValue ? (object)reconciliation.ActionStatus.Value : DBNull.Value;
            var pActionDate = cmd.Parameters.Add("@ActionDate", OleDbType.Date);
            pActionDate.Value = reconciliation.ActionDate.HasValue ? (object)reconciliation.ActionDate.Value : DBNull.Value;
            cmd.Parameters.AddWithValue("@Assignee", string.IsNullOrWhiteSpace(reconciliation.Assignee) ? (object)DBNull.Value : reconciliation.Assignee);
            var pComments = cmd.Parameters.Add("@Comments", OleDbType.LongVarWChar);
            pComments.Value = reconciliation.Comments ?? (object)DBNull.Value;
            cmd.Parameters.AddWithValue("@InternalInvoiceReference", reconciliation.InternalInvoiceReference ?? (object)DBNull.Value);
            var pFirst = cmd.Parameters.Add("@FirstClaimDate", OleDbType.Date);
            pFirst.Value = reconciliation.FirstClaimDate.HasValue ? (object)reconciliation.FirstClaimDate.Value : DBNull.Value;
            var pLast = cmd.Parameters.Add("@LastClaimDate", OleDbType.Date);
            pLast.Value = reconciliation.LastClaimDate.HasValue ? (object)reconciliation.LastClaimDate.Value : DBNull.Value;
            var pToRemind = cmd.Parameters.Add("@ToRemind", OleDbType.Boolean);
            pToRemind.Value = reconciliation.ToRemind;
            var pRem = cmd.Parameters.Add("@ToRemindDate", OleDbType.Date);
            pRem.Value = reconciliation.ToRemindDate.HasValue ? (object)reconciliation.ToRemindDate.Value : DBNull.Value;
            var pAck = cmd.Parameters.Add("@ACK", OleDbType.Boolean);
            pAck.Value = reconciliation.ACK;
            cmd.Parameters.AddWithValue("@SwiftCode", reconciliation.SwiftCode ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@PaymentReference", reconciliation.PaymentReference ?? (object)DBNull.Value);
            var pMbaw = cmd.Parameters.Add("@MbawData", OleDbType.LongVarWChar);
            pMbaw.Value = reconciliation.MbawData ?? (object)DBNull.Value;
            var pSpirit = cmd.Parameters.Add("@SpiritData", OleDbType.LongVarWChar);
            pSpirit.Value = reconciliation.SpiritData ?? (object)DBNull.Value;
            var pKpi = cmd.Parameters.Add("@KPI", OleDbType.Integer);
            pKpi.Value = reconciliation.KPI.HasValue ? (object)reconciliation.KPI.Value : DBNull.Value;
            var pInc = cmd.Parameters.Add("@IncidentType", OleDbType.Integer);
            pInc.Value = reconciliation.IncidentType.HasValue ? (object)reconciliation.IncidentType.Value : DBNull.Value;
            var pRisky = cmd.Parameters.Add("@RiskyItem", OleDbType.Boolean);
            pRisky.Value = reconciliation.RiskyItem.HasValue ? (object)reconciliation.RiskyItem.Value : DBNull.Value;
            var pReason = cmd.Parameters.Add("@ReasonNonRisky", OleDbType.Integer);
            pReason.Value = reconciliation.ReasonNonRisky.HasValue ? (object)reconciliation.ReasonNonRisky.Value : DBNull.Value;
            var pTrigDate = cmd.Parameters.Add("@TriggerDate", OleDbType.Date);
            pTrigDate.Value = reconciliation.TriggerDate.HasValue ? (object)reconciliation.TriggerDate.Value : DBNull.Value;

            if (isInsert)
            {
                // Audit & user-edit protection (positions match the INSERT statement order)
                var pLmbu = cmd.Parameters.Add("@LastModifiedByUser", OleDbType.Date);
                pLmbu.Value = reconciliation.LastModifiedByUser.HasValue ? (object)reconciliation.LastModifiedByUser.Value : DBNull.Value;
                cmd.Parameters.AddWithValue("@UserEditedFields", reconciliation.UserEditedFields ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@LastRuleAppliedId", reconciliation.LastRuleAppliedId ?? (object)DBNull.Value);
                var pLra = cmd.Parameters.Add("@LastRuleAppliedAt", OleDbType.Date);
                pLra.Value = reconciliation.LastRuleAppliedAt.HasValue ? (object)reconciliation.LastRuleAppliedAt.Value : DBNull.Value;

                var pCreate = cmd.Parameters.Add("@CreationDate", OleDbType.Date);
                pCreate.Value = reconciliation.CreationDate.HasValue ? (object)reconciliation.CreationDate.Value : DBNull.Value;
            }

            cmd.Parameters.AddWithValue("@ModifiedBy", reconciliation.ModifiedBy ?? (object)DBNull.Value);
            var pMod = cmd.Parameters.Add("@LastModified", OleDbType.Date);
            pMod.Value = reconciliation.LastModified.HasValue ? (object)reconciliation.LastModified.Value : DBNull.Value;

            if (!isInsert)
                cmd.Parameters.AddWithValue("@ID", reconciliation.ID);
        }
    }
}
