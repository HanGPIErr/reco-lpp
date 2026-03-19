using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Threading.Tasks;
using RecoTool.Models;
using RecoTool.Services.DTOs;

namespace RecoTool.Services.Rules
{
    /// <summary>
    /// Builds RuleContext for truth-table rule evaluation.
    /// Extracted from ReconciliationService to isolate rule context construction logic.
    /// </summary>
    public class RuleContextBuilder
    {
        private readonly ReconciliationService _reconciliationService;
        private readonly OfflineFirstService _offlineFirstService;

        public RuleContextBuilder(ReconciliationService reconciliationService, OfflineFirstService offlineFirstService)
        {
            _reconciliationService = reconciliationService;
            _offlineFirstService = offlineFirstService;
        }

        public async Task<RuleContext> BuildAsync(DataAmbre a, Reconciliation r, Country country, string countryId, bool isPivot, bool? isGrouped = null, bool? isAmountMatch = null)
        {
            var transformationService = new TransformationService(new List<Country> { country });
            TransactionType? tx = await DetermineTransactionTypeAsync(a, r, isPivot, transformationService).ConfigureAwait(false);
            string txName = tx.HasValue ? Enum.GetName(typeof(TransactionType), tx.Value) : null;

            string guaranteeType = await ResolveGuaranteeTypeAsync(r, isPivot).ConfigureAwait(false);
            var sign = a.SignedAmount >= 0 ? "C" : "D";

            bool? hasDw = !string.IsNullOrWhiteSpace(r?.DWINGS_InvoiceID)
                          || !string.IsNullOrWhiteSpace(r?.DWINGS_GuaranteeID)
                          || !string.IsNullOrWhiteSpace(r?.DWINGS_BGPMT)
                          || !string.IsNullOrWhiteSpace(r?.InternalInvoiceReference);

            decimal? missingAmount = null;
            if (!isGrouped.HasValue || !isAmountMatch.HasValue)
            {
                var flags = await CalculateGroupingFlagsAsync(a, r, country, countryId).ConfigureAwait(false);
                isGrouped = flags.isGrouped ?? isGrouped;
                isAmountMatch = flags.isAmountMatch ?? isAmountMatch;
                missingAmount = flags.missingAmount;
            }

            var today = DateTime.Today;
            bool? triggerDateIsNull = r?.TriggerDate.HasValue == true ? (bool?)false : (r != null ? (bool?)true : null);
            int? daysSinceTrigger = r?.TriggerDate.HasValue == true ? (int?)(today - r.TriggerDate.Value.Date).TotalDays : null;
            int? operationDaysAgo = a?.Operation_Date.HasValue == true ? (int?)(today - a.Operation_Date.Value.Date).TotalDays : null;
            bool? isFirstRequest = r?.FirstClaimDate.HasValue == true ? (bool?)false : (r != null ? (bool?)true : null);
            int? daysSinceReminder = r?.LastClaimDate.HasValue == true ? (int?)(today - r.LastClaimDate.Value.Date).TotalDays : null;

            var (mtStatus, hasCommEmail, bgiInitiated) = await ResolveDwingsInvoiceFieldsAsync(r).ConfigureAwait(false);

            bool? isNewLineFlag = null;
            try { if (r != null && r.CreationDate.HasValue) isNewLineFlag = r.CreationDate.Value.Date == today; } catch { }

            return new RuleContext
            {
                CountryId = countryId,
                IsPivot = isPivot,
                GuaranteeType = guaranteeType,
                TransactionType = txName,
                HasDwingsLink = hasDw,
                IsGrouped = isGrouped,
                IsAmountMatch = isAmountMatch,
                MissingAmount = missingAmount,
                Sign = sign,
                Bgi = r?.DWINGS_InvoiceID,
                TriggerDateIsNull = triggerDateIsNull,
                DaysSinceTrigger = daysSinceTrigger,
                OperationDaysAgo = operationDaysAgo,
                IsMatched = hasDw,
                HasManualMatch = null,
                IsFirstRequest = isFirstRequest,
                IsNewLine = isNewLineFlag,
                DaysSinceReminder = daysSinceReminder,
                CurrentActionId = r?.Action,
                MtStatus = mtStatus,
                HasCommIdEmail = hasCommEmail,
                IsBgiInitiated = bgiInitiated
            };
        }

        private async Task<TransactionType?> DetermineTransactionTypeAsync(DataAmbre a, Reconciliation r, bool isPivot, TransformationService transformationService)
        {
            if (isPivot)
                return transformationService.DetermineTransactionType(a.RawLabel, isPivot, a.Category);

            string paymentMethod = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(r?.DWINGS_InvoiceID))
                {
                    var invoices = await _reconciliationService.GetDwingsInvoicesAsync().ConfigureAwait(false);
                    var inv = invoices?.FirstOrDefault(i => string.Equals(i?.INVOICE_ID, r.DWINGS_InvoiceID, StringComparison.OrdinalIgnoreCase));
                    paymentMethod = inv?.PAYMENT_METHOD;
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(paymentMethod))
            {
                var upperMethod = paymentMethod.Trim().ToUpperInvariant().Replace(' ', '_');
                if (Enum.TryParse<TransactionType>(upperMethod, true, out var parsed))
                    return parsed;
            }

            return transformationService.DetermineTransactionType(a.RawLabel, isPivot, null);
        }

        private async Task<string> ResolveGuaranteeTypeAsync(Reconciliation r, bool isPivot)
        {
            if (isPivot || string.IsNullOrWhiteSpace(r?.DWINGS_GuaranteeID)) return null;
            try
            {
                var guarantees = await _reconciliationService.GetDwingsGuaranteesAsync().ConfigureAwait(false);
                var guar = guarantees?.FirstOrDefault(g => string.Equals(g?.GUARANTEE_ID, r.DWINGS_GuaranteeID, StringComparison.OrdinalIgnoreCase));
                return guar?.GUARANTEE_TYPE;
            }
            catch { return null; }
        }

        private async Task<(string mtStatus, bool? hasCommEmail, bool? bgiInitiated)> ResolveDwingsInvoiceFieldsAsync(Reconciliation r)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(r?.DWINGS_InvoiceID))
                {
                    var invoices = await _reconciliationService.GetDwingsInvoicesAsync().ConfigureAwait(false);
                    var inv = invoices?.FirstOrDefault(i => string.Equals(i?.INVOICE_ID, r.DWINGS_InvoiceID, StringComparison.OrdinalIgnoreCase));
                    if (inv != null)
                    {
                        bool? bgiInit = !string.IsNullOrWhiteSpace(inv.T_INVOICE_STATUS)
                            ? (bool?)string.Equals(inv.T_INVOICE_STATUS, "INITIATED", StringComparison.OrdinalIgnoreCase)
                            : null;
                        return (inv.MT_STATUS, inv.COMM_ID_EMAIL, bgiInit);
                    }
                }
            }
            catch { }
            return (null, null, null);
        }

        #region Grouping Flags

        public async Task<(bool? isGrouped, bool? isAmountMatch, decimal? missingAmount)> CalculateGroupingFlagsAsync(
            DataAmbre a, Reconciliation r, Country country, string countryId)
        {
            try
            {
                if (r == null || a == null) return (null, null, null);

                string groupingRef = GetGroupingReference(r);
                if (string.IsNullOrWhiteSpace(groupingRef))
                    return (false, false, null);

                var ambreCs = _offlineFirstService?.GetAmbreConnectionString(countryId);
                var reconCs = _offlineFirstService?.GetCountryConnectionString(countryId);
                if (string.IsNullOrWhiteSpace(ambreCs) || string.IsNullOrWhiteSpace(reconCs))
                    return (null, null, null);

                var relatedLines = await LoadRelatedLinesAsync(groupingRef, reconCs, ambreCs).ConfigureAwait(false);
                var batch = CalculateGroupingFlagsBatch(relatedLines, country);
                if (batch.TryGetValue(a.ID, out var flags))
                    return (flags.isGrouped, flags.isAmountMatch, flags.missingAmount);
                return (null, null, null);
            }
            catch { return (null, null, null); }
        }

        public Dictionary<string, (bool isGrouped, bool isAmountMatch, decimal? missingAmount)> CalculateGroupingFlagsBatch(
            List<DataAmbre> ambreLines, Country country)
        {
            var result = new Dictionary<string, (bool isGrouped, bool isAmountMatch, decimal? missingAmount)>(StringComparer.OrdinalIgnoreCase);
            if (ambreLines == null || ambreLines.Count == 0) return result;

            var pivotAccount = country?.CNT_AmbrePivot;
            var receivableAccount = country?.CNT_AmbreReceivable;

            // For batch, we need reconciliations — but this simplified version works on pre-loaded lines
            // Group by Account_ID to determine P/R sides
            foreach (var line in ambreLines)
            {
                // Default: not grouped
                if (!result.ContainsKey(line.ID))
                    result[line.ID] = (false, false, null);
            }

            bool hasPivot = ambreLines.Any(l => string.Equals(l.Account_ID, pivotAccount, StringComparison.OrdinalIgnoreCase));
            bool hasReceivable = ambreLines.Any(l => string.Equals(l.Account_ID, receivableAccount, StringComparison.OrdinalIgnoreCase));
            bool isGrouped = hasPivot && hasReceivable;

            if (isGrouped)
            {
                decimal receivableTotal = ambreLines.Where(l => string.Equals(l.Account_ID, receivableAccount, StringComparison.OrdinalIgnoreCase)).Sum(l => l.SignedAmount);
                decimal pivotTotal = ambreLines.Where(l => string.Equals(l.Account_ID, pivotAccount, StringComparison.OrdinalIgnoreCase)).Sum(l => l.SignedAmount);
                decimal? missingAmount = receivableTotal + pivotTotal;
                bool isAmountMatch = Math.Abs(missingAmount.Value) < 0.01m;

                foreach (var line in ambreLines)
                    result[line.ID] = (true, isAmountMatch, missingAmount);
            }

            return result;
        }

        private static string GetGroupingReference(Reconciliation r)
        {
            if (!string.IsNullOrWhiteSpace(r.DWINGS_BGPMT)) return r.DWINGS_BGPMT;
            if (!string.IsNullOrWhiteSpace(r.DWINGS_InvoiceID)) return r.DWINGS_InvoiceID;
            if (!string.IsNullOrWhiteSpace(r.DWINGS_GuaranteeID)) return r.DWINGS_GuaranteeID;
            if (!string.IsNullOrWhiteSpace(r.InternalInvoiceReference)) return r.InternalInvoiceReference;
            return null;
        }

        private static async Task<List<DataAmbre>> LoadRelatedLinesAsync(string groupingRef, string reconCs, string ambreCs)
        {
            var relatedLines = new List<DataAmbre>();
            using (var reconConn = new OleDbConnection(reconCs))
            using (var ambreConn = new OleDbConnection(ambreCs))
            {
                await reconConn.OpenAsync().ConfigureAwait(false);
                await ambreConn.OpenAsync().ConfigureAwait(false);

                var relatedIds = new List<string>();
                using (var cmd = reconConn.CreateCommand())
                {
                    cmd.CommandText = @"SELECT ID FROM T_Reconciliation 
                                      WHERE (DWINGS_BGPMT = ? OR DWINGS_InvoiceID = ? OR DWINGS_GuaranteeID = ? OR InternalInvoiceReference = ?)";
                    for (int i = 0; i < 4; i++)
                        cmd.Parameters.Add($"@ref{i}", OleDbType.VarWChar).Value = groupingRef;

                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var id = reader["ID"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(id)) relatedIds.Add(id);
                        }
                    }
                }

                foreach (var id in relatedIds)
                {
                    using (var cmd = ambreConn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT ID, Account_ID, SignedAmount FROM T_Data_Ambre WHERE ID = ? AND DeleteDate IS NULL";
                        cmd.Parameters.Add("@id", OleDbType.VarWChar).Value = id;

                        using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            if (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                relatedLines.Add(new DataAmbre
                                {
                                    ID = reader["ID"]?.ToString(),
                                    Account_ID = reader["Account_ID"]?.ToString(),
                                    SignedAmount = Convert.ToDecimal(reader["SignedAmount"])
                                });
                            }
                        }
                    }
                }
            }
            return relatedLines;
        }

        #endregion
    }
}
