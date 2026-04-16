using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
        public const string TableName = "T_RuleProposals";

        public RuleProposalRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));
            _connectionString = connectionString;
        }

        /// <summary>
        /// Creates the T_RuleProposals table if it does not already exist.
        /// Safe to call at startup.
        /// </summary>
        public async Task EnsureTableAsync(CancellationToken ct = default)
        {
            using (var conn = new OleDbConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                var schema = conn.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, new object[] { null, null, TableName, "TABLE" });
                if (schema != null && schema.Rows.Count > 0) return;

                var sql = $@"CREATE TABLE [{TableName}] (
                    ProposalId AUTOINCREMENT PRIMARY KEY,
                    RecoId TEXT(255) NOT NULL,
                    RuleId TEXT(100) NOT NULL,
                    Field TEXT(50) NOT NULL,
                    OldValue TEXT(255),
                    NewValue TEXT(255),
                    CreatedAt DATETIME NOT NULL,
                    CreatedBy TEXT(100) NOT NULL,
                    Status TEXT(20) NOT NULL,
                    DecidedBy TEXT(100),
                    DecidedAt DATETIME,
                    DeleteDate DATETIME
                )";
                using (var cmd = new OleDbCommand(sql, conn)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                // Best-effort indexes
                try
                {
                    using (var c = new OleDbCommand($"CREATE INDEX IX_{TableName}_RecoId ON [{TableName}] (RecoId)", conn))
                        await c.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch { }
                try
                {
                    using (var c = new OleDbCommand($"CREATE INDEX IX_{TableName}_Status ON [{TableName}] (Status)", conn))
                        await c.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch { }
            }
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
            int inserted = 0;

            using (var conn = new OleDbConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        // Pre-query existing pending duplicates
                        var dupCheck = new OleDbCommand(
                            $"SELECT COUNT(*) FROM [{TableName}] WHERE RecoId=? AND RuleId=? AND Field=? AND Status='Pending' AND DeleteDate IS NULL",
                            conn, tx);
                        dupCheck.Parameters.Add("@RecoId", OleDbType.VarWChar, 255);
                        dupCheck.Parameters.Add("@RuleId", OleDbType.VarWChar, 100);
                        dupCheck.Parameters.Add("@Field", OleDbType.VarWChar, 50);

                        var insertCmd = new OleDbCommand(
                            $@"INSERT INTO [{TableName}] (RecoId, RuleId, Field, OldValue, NewValue, CreatedAt, CreatedBy, Status)
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
                            var existing = Convert.ToInt32(await dupCheck.ExecuteScalarAsync(ct).ConfigureAwait(false));
                            if (existing > 0) continue;

                            insertCmd.Parameters["@RecoId"].Value = p.RecoId;
                            insertCmd.Parameters["@RuleId"].Value = p.RuleId;
                            insertCmd.Parameters["@Field"].Value = p.Field;
                            insertCmd.Parameters["@OldValue"].Value = (object)p.OldValue ?? DBNull.Value;
                            insertCmd.Parameters["@NewValue"].Value = (object)p.NewValue ?? DBNull.Value;
                            insertCmd.Parameters["@CreatedAt"].Value = p.CreatedAt == default ? DateTime.UtcNow : p.CreatedAt;
                            insertCmd.Parameters["@CreatedBy"].Value = (object)p.CreatedBy ?? DBNull.Value;
                            insertCmd.Parameters["@Status"].Value = (p.Status == default ? ProposalStatus.Pending : p.Status).ToString();

                            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            inserted++;
                        }
                        tx.Commit();
                    }
                    catch
                    {
                        try { tx.Rollback(); } catch { }
                        throw;
                    }
                }
            }

            return inserted;
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
            catch { return list; }

            using (var conn = new OleDbConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                string sql = $"SELECT ProposalId, RecoId, RuleId, Field, OldValue, NewValue, CreatedAt, CreatedBy, Status, DecidedBy, DecidedAt, DeleteDate FROM [{TableName}] WHERE DeleteDate IS NULL";
                if (status.HasValue) sql += " AND Status = ?";
                sql += " ORDER BY CreatedAt DESC";

                using (var cmd = new OleDbCommand(sql, conn))
                {
                    if (status.HasValue) cmd.Parameters.AddWithValue("@Status", status.Value.ToString());
                    using (var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
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
                                list.Add(p);
                            }
                            catch { }
                        }
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// Updates the status of a proposal. Used by Accept / Reject handlers.
        /// </summary>
        public async Task<bool> UpdateStatusAsync(int proposalId, ProposalStatus newStatus, string decidedBy, CancellationToken ct = default)
        {
            using (var conn = new OleDbConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new OleDbCommand(
                    $"UPDATE [{TableName}] SET Status=?, DecidedBy=?, DecidedAt=? WHERE ProposalId=?", conn))
                {
                    cmd.Parameters.AddWithValue("@Status", newStatus.ToString());
                    cmd.Parameters.AddWithValue("@DecidedBy", (object)decidedBy ?? DBNull.Value);
                    cmd.Parameters.Add("@DecidedAt", OleDbType.Date).Value = DateTime.UtcNow;
                    cmd.Parameters.AddWithValue("@ProposalId", proposalId);
                    var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    return n > 0;
                }
            }
        }

        /// <summary>
        /// Marks all pending proposals for a given reconciliation as Stale (e.g. after a user edit on that row).
        /// </summary>
        public async Task<int> MarkRecoProposalsStaleAsync(string recoId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(recoId)) return 0;
            using (var conn = new OleDbConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = new OleDbCommand(
                    $"UPDATE [{TableName}] SET Status='Stale' WHERE RecoId=? AND Status='Pending' AND DeleteDate IS NULL", conn))
                {
                    cmd.Parameters.AddWithValue("@RecoId", recoId);
                    return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        private static ProposalStatus ParseStatus(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return ProposalStatus.Pending;
            if (Enum.TryParse<ProposalStatus>(s.Trim(), true, out var e)) return e;
            return ProposalStatus.Pending;
        }
    }
}
