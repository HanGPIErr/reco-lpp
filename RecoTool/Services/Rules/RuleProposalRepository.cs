using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Infrastructure;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Infrastructure.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RecoTool.Services.Rules
{
    /// <summary>
    /// Access to <c>T_RuleProposals</c> in the country reconciliation DB.
    /// Proposals are created when a rule fires in <see cref="RuleMode.Propose"/> mode.
    /// A human operator then accepts or rejects them via the Rules Health Center.
    /// </summary>
    public class RuleProposalRepository
    {
        private readonly string _connectionString;
        private readonly IClock _clock;
        private readonly ILogger<RuleProposalRepository> _logger;
        public const string TableName = Schema.Tables.T_RuleProposals;

        public RuleProposalRepository(string connectionString, IClock clock = null, ILogger<RuleProposalRepository> logger = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));
            _connectionString = connectionString;
            _clock = clock ?? SystemClock.Instance;
            _logger = logger ?? NullLogger<RuleProposalRepository>.Instance;
        }

        /// <summary>
        /// Creates the T_RuleProposals table if it does not already exist.
        /// Safe to call at startup.
        /// </summary>
        public async Task EnsureTableAsync(CancellationToken ct = default)
        {
            await OleDbAsyncExecutor.RunWithConnectionAsync<object>(_connectionString, conn =>
            {
                var schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, TableName, "TABLE" });
                if (schema != null && schema.Rows.Count > 0) return null;

                var sql = $@"CREATE TABLE [{TableName}] (
                    {Schema.Columns.RuleProposals.ProposalId} AUTOINCREMENT PRIMARY KEY,
                    {Schema.Columns.RuleProposals.RecoId} TEXT(255) NOT NULL,
                    {Schema.Columns.RuleProposals.RuleId} TEXT(100) NOT NULL,
                    {Schema.Columns.RuleProposals.Field} TEXT(50) NOT NULL,
                    {Schema.Columns.RuleProposals.OldValue} TEXT(255),
                    {Schema.Columns.RuleProposals.NewValue} TEXT(255),
                    {Schema.Columns.RuleProposals.CreatedAt} DATETIME NOT NULL,
                    {Schema.Columns.RuleProposals.CreatedBy} TEXT(100) NOT NULL,
                    {Schema.Columns.RuleProposals.Status} TEXT(20) NOT NULL,
                    {Schema.Columns.RuleProposals.DecidedBy} TEXT(100),
                    {Schema.Columns.RuleProposals.DecidedAt} DATETIME,
                    {Schema.Columns.RuleProposals.DeleteDate} DATETIME
                )";
                using (var cmd = new OleDbCommand(sql, conn)) cmd.ExecuteNonQuery();

                // Best-effort indexes
                try
                {
                    using (var c = new OleDbCommand($"CREATE INDEX IX_{TableName}_RecoId ON [{TableName}] ({Schema.Columns.RuleProposals.RecoId})", conn))
                        c.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create index IX_{TableName}_RecoId on T_RuleProposals", TableName);
                }
                try
                {
                    using (var c = new OleDbCommand($"CREATE INDEX IX_{TableName}_Status ON [{TableName}] ({Schema.Columns.RuleProposals.Status})", conn))
                        c.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create index IX_{TableName}_Status on T_RuleProposals", TableName);
                }

                return null;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Inserts a batch of proposals in a single transaction. Duplicates (same RecoId+RuleId+Field in Pending status)
        /// are skipped to keep the table clean on repeated rule evaluations.
        /// Returns number of rows inserted.
        /// </summary>
        public async Task<int> InsertProposalsAsync(IEnumerable<RuleProposal> proposals, CancellationToken ct = default)
        {
            if (proposals == null) return 0;
            var list = proposals.Where(p => p != null && !string.IsNullOrWhiteSpace(p.RecoId) && !string.IsNullOrWhiteSpace(p.RuleId) && !string.IsNullOrWhiteSpace(p.Field)).ToList();
            if (list.Count == 0) return 0;

            await EnsureTableAsync(ct).ConfigureAwait(false);

            return await OleDbAsyncExecutor.RunInTransactionAsync(_connectionString, (conn, tx) =>
            {
                int inserted = 0;

                // Pre-query existing pending duplicates
                var dupCheck = new OleDbCommand(
                    $"SELECT COUNT(*) FROM [{TableName}] WHERE {Schema.Columns.RuleProposals.RecoId}=? AND {Schema.Columns.RuleProposals.RuleId}=? AND {Schema.Columns.RuleProposals.Field}=? AND {Schema.Columns.RuleProposals.Status}='Pending' AND {Schema.Columns.RuleProposals.DeleteDate} IS NULL",
                    conn, tx);
                dupCheck.Parameters.Add("@RecoId", OleDbType.VarWChar, 255);
                dupCheck.Parameters.Add("@RuleId", OleDbType.VarWChar, 100);
                dupCheck.Parameters.Add("@Field", OleDbType.VarWChar, 50);

                var insertCmd = new OleDbCommand(
                    $@"INSERT INTO [{TableName}] ({Schema.Columns.RuleProposals.RecoId}, {Schema.Columns.RuleProposals.RuleId}, {Schema.Columns.RuleProposals.Field}, {Schema.Columns.RuleProposals.OldValue}, {Schema.Columns.RuleProposals.NewValue}, {Schema.Columns.RuleProposals.CreatedAt}, {Schema.Columns.RuleProposals.CreatedBy}, {Schema.Columns.RuleProposals.Status})
                       VALUES (?, ?, ?, ?, ?, ?, ?, ?)", conn, tx);
                insertCmd.Parameters.Add("@RecoId", OleDbType.VarWChar, 255);
                insertCmd.Parameters.Add("@RuleId", OleDbType.VarWChar, 100);
                insertCmd.Parameters.Add("@Field", OleDbType.VarWChar, 50);
                insertCmd.Parameters.Add("@OldValue", OleDbType.VarWChar, 255);
                insertCmd.Parameters.Add("@NewValue", OleDbType.VarWChar, 255);
                insertCmd.Parameters.Add("@CreatedAt", OleDbType.Date);
                insertCmd.Parameters.Add("@CreatedBy", OleDbType.VarWChar, 100);
                insertCmd.Parameters.Add("@Status", OleDbType.VarWChar, 20);

                foreach (var p in list)
                {
                    dupCheck.Parameters["@RecoId"].Value = p.RecoId;
                    dupCheck.Parameters["@RuleId"].Value = p.RuleId;
                    dupCheck.Parameters["@Field"].Value = p.Field;
                    var existing = Convert.ToInt32(dupCheck.ExecuteScalar());
                    if (existing > 0) continue;

                    insertCmd.Parameters["@RecoId"].Value = p.RecoId;
                    insertCmd.Parameters["@RuleId"].Value = p.RuleId;
                    insertCmd.Parameters["@Field"].Value = p.Field;
                    insertCmd.Parameters["@OldValue"].Value = (object)p.OldValue ?? DBNull.Value;
                    insertCmd.Parameters["@NewValue"].Value = (object)p.NewValue ?? DBNull.Value;
                    insertCmd.Parameters["@CreatedAt"].Value = p.CreatedAt == default ? _clock.UtcNow : p.CreatedAt;
                    insertCmd.Parameters["@CreatedBy"].Value = (object)p.CreatedBy ?? DBNull.Value;
                    insertCmd.Parameters["@Status"].Value = (p.Status == default ? ProposalStatus.Pending : p.Status).ToString();

                    insertCmd.ExecuteNonQuery();
                    inserted++;
                }

                return inserted;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads proposals filtered by status (null = all). Ordered by CreatedAt DESC.
        /// </summary>
        public async Task<List<RuleProposal>> LoadAsync(ProposalStatus? status = null, CancellationToken ct = default)
        {
            var list = new List<RuleProposal>();
            try
            {
                await EnsureTableAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RuleProposalRepository.LoadAsync: EnsureTable failed, returning empty list (status={Status})", status);
                return list;
            }

            return await OleDbAsyncExecutor.RunWithConnectionAsync(_connectionString, conn =>
            {
                var rows = new List<RuleProposal>();
                string sql = $"SELECT {Schema.Columns.RuleProposals.ProposalId}, {Schema.Columns.RuleProposals.RecoId}, {Schema.Columns.RuleProposals.RuleId}, {Schema.Columns.RuleProposals.Field}, {Schema.Columns.RuleProposals.OldValue}, {Schema.Columns.RuleProposals.NewValue}, {Schema.Columns.RuleProposals.CreatedAt}, {Schema.Columns.RuleProposals.CreatedBy}, {Schema.Columns.RuleProposals.Status}, {Schema.Columns.RuleProposals.DecidedBy}, {Schema.Columns.RuleProposals.DecidedAt}, {Schema.Columns.RuleProposals.DeleteDate} FROM [{TableName}] WHERE {Schema.Columns.RuleProposals.DeleteDate} IS NULL";
                if (status.HasValue) sql += $" AND {Schema.Columns.RuleProposals.Status} = ?";
                sql += $" ORDER BY {Schema.Columns.RuleProposals.CreatedAt} DESC";

                using (var cmd = new OleDbCommand(sql, conn))
                {
                    if (status.HasValue) cmd.Parameters.AddWithValue("@Status", status.Value.ToString());
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            try
                            {
                                var p = new RuleProposal
                                {
                                    ProposalId = rdr.IsDBNull(0) ? (int?)null : Convert.ToInt32(rdr.GetValue(0)),
                                    RecoId = rdr.IsDBNull(1) ? null : rdr.GetString(1),
                                    RuleId = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                                    Field = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                                    OldValue = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                                    NewValue = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                                    CreatedAt = rdr.IsDBNull(6) ? DateTime.MinValue : rdr.GetDateTime(6),
                                    CreatedBy = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                                    Status = ParseStatus(rdr.IsDBNull(8) ? null : rdr.GetString(8)),
                                    DecidedBy = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                                    DecidedAt = rdr.IsDBNull(10) ? (DateTime?)null : rdr.GetDateTime(10),
                                    DeleteDate = rdr.IsDBNull(11) ? (DateTime?)null : rdr.GetDateTime(11)
                                };
                                rows.Add(p);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "RuleProposalRepository.LoadAsync: failed to materialize one RuleProposal row (skipped)");
                            }
                        }
                    }
                }
                return rows;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Updates the status of a proposal. Used by Accept / Reject handlers.
        /// </summary>
        public async Task<bool> UpdateStatusAsync(int proposalId, ProposalStatus newStatus, string decidedBy, CancellationToken ct = default)
        {
            return await OleDbAsyncExecutor.RunWithConnectionAsync(_connectionString, conn =>
            {
                using (var cmd = new OleDbCommand(
                    $"UPDATE [{TableName}] SET {Schema.Columns.RuleProposals.Status}=?, {Schema.Columns.RuleProposals.DecidedBy}=?, {Schema.Columns.RuleProposals.DecidedAt}=? WHERE {Schema.Columns.RuleProposals.ProposalId}=?", conn))
                {
                    cmd.Parameters.AddWithValue("@Status", newStatus.ToString());
                    cmd.Parameters.AddWithValue("@DecidedBy", (object)decidedBy ?? DBNull.Value);
                    cmd.Parameters.Add("@DecidedAt", OleDbType.Date).Value = _clock.UtcNow;
                    cmd.Parameters.AddWithValue("@ProposalId", proposalId);
                    var n = cmd.ExecuteNonQuery();
                    return n > 0;
                }
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Marks all pending proposals for a given reconciliation as Stale (e.g. after a user edit on that row).
        /// </summary>
        public async Task<int> MarkRecoProposalsStaleAsync(string recoId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(recoId)) return 0;
            return await OleDbAsyncExecutor.RunWithConnectionAsync(_connectionString, conn =>
            {
                using (var cmd = new OleDbCommand(
                    $"UPDATE [{TableName}] SET {Schema.Columns.RuleProposals.Status}='Stale' WHERE {Schema.Columns.RuleProposals.RecoId}=? AND {Schema.Columns.RuleProposals.Status}='Pending' AND {Schema.Columns.RuleProposals.DeleteDate} IS NULL", conn))
                {
                    cmd.Parameters.AddWithValue("@RecoId", recoId);
                    return cmd.ExecuteNonQuery();
                }
            }, ct).ConfigureAwait(false);
        }

        private static ProposalStatus ParseStatus(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return ProposalStatus.Pending;
            if (Enum.TryParse<ProposalStatus>(s.Trim(), true, out var e)) return e;
            return ProposalStatus.Pending;
        }
    }
}
