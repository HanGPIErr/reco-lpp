using System;
using System.Collections.Generic;
using System.Linq;
using RecoTool.Helpers;

namespace RecoTool.Services.Helpers
{
    /// <summary>
    /// Centralizes AccountSide assignment and IsMatchedAcrossAccounts computation.
    /// Eliminates duplication between BuildReconciliationViewAsyncCore and GetStatusCountsAsync.
    /// </summary>
    public static class AccountSideCalculator
    {
        /// <summary>
        /// Assigns AccountSide ("P" or "R") to each row based on the country's pivot/receivable account IDs.
        /// </summary>
        public static void AssignAccountSides<T>(IList<T> rows, string pivotAccountId, string receivableAccountId, Func<T, string> getAccountId, Action<T, string> setAccountSide)
        {
            if (rows == null || rows.Count == 0) return;
            var pivotId = pivotAccountId?.Trim();
            var recvId = receivableAccountId?.Trim();
            if (string.IsNullOrWhiteSpace(pivotId) && string.IsNullOrWhiteSpace(recvId)) return;

            foreach (var row in rows)
            {
                var acc = getAccountId(row)?.Trim();
                if (!string.IsNullOrWhiteSpace(pivotId) && string.Equals(acc, pivotId, StringComparison.OrdinalIgnoreCase))
                    setAccountSide(row, "P");
                else if (!string.IsNullOrWhiteSpace(recvId) && string.Equals(acc, recvId, StringComparison.OrdinalIgnoreCase))
                    setAccountSide(row, "R");
                else
                    setAccountSide(row, null);
            }
        }

        /// <summary>
        /// Marks rows as IsMatchedAcrossAccounts when both Pivot and Receivable rows share the same grouping key.
        /// Applies grouping by DWINGS_InvoiceID, InternalInvoiceReference, and optionally a fallback BGI heuristic.
        /// </summary>
        public static void ComputeMatchedAcrossAccounts<T>(
            IList<T> rows,
            Func<T, string> getAccountSide,
            Func<T, string> getDwingsInvoiceId,
            Func<T, string> getInternalInvoiceRef,
            Action<T, bool> setMatched,
            Func<T, string> getFallbackKey = null)
        {
            if (rows == null || rows.Count == 0) return;

            // Group by DWINGS_InvoiceID
            MarkMatchedGroups(rows, getDwingsInvoiceId, getAccountSide, setMatched);

            // Group by InternalInvoiceReference (independently)
            MarkMatchedGroups(rows, getInternalInvoiceRef, getAccountSide, setMatched);

            // Fallback grouping (e.g., heuristic BGI extraction)
            if (getFallbackKey != null)
            {
                MarkMatchedGroups(rows, getFallbackKey, getAccountSide, setMatched);
            }
        }

        private static void MarkMatchedGroups<T>(
            IList<T> rows,
            Func<T, string> getKey,
            Func<T, string> getAccountSide,
            Action<T, bool> setMatched)
        {
            var groups = rows
                .Where(r => !string.IsNullOrWhiteSpace(getKey(r)))
                .GroupBy(r => getKey(r).Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                bool hasP = g.Any(x => string.Equals(getAccountSide(x), "P", StringComparison.OrdinalIgnoreCase));
                bool hasR = g.Any(x => string.Equals(getAccountSide(x), "R", StringComparison.OrdinalIgnoreCase));
                if (hasP && hasR)
                {
                    foreach (var row in g) setMatched(row, true);
                }
            }
        }

        /// <summary>
        /// Computes MissingAmount for matched groups (sum of receivable + pivot amounts per group key).
        /// </summary>
        public static void ComputeMissingAmounts<T>(
            IList<T> rows,
            Func<T, bool> isMatched,
            Func<T, string> getDwingsInvoiceId,
            Func<T, string> getInternalInvoiceRef,
            Func<T, string> getAccountSide,
            Func<T, double> getSignedAmount,
            Action<T, double?> setMissingAmount)
        {
            if (rows == null || rows.Count == 0) return;

            var matchedRows = rows.Where(r => isMatched(r)).ToList();
            if (matchedRows.Count == 0) return;

            var groups = matchedRows
                .Where(r => !string.IsNullOrWhiteSpace(getDwingsInvoiceId(r)) || !string.IsNullOrWhiteSpace(getInternalInvoiceRef(r)))
                .GroupBy(r =>
                {
                    // InternalInvoiceReference (explicit user link) takes priority over
                    // DWINGS_InvoiceID (automatic link) so basket-linked items group correctly.
                    var key = getInternalInvoiceRef(r);
                    if (!string.IsNullOrWhiteSpace(key)) return key.Trim().ToUpperInvariant();
                    return getDwingsInvoiceId(r)?.Trim().ToUpperInvariant();
                })
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));

            foreach (var group in groups)
            {
                var receivableLines = group.Where(r => string.Equals(getAccountSide(r), "R", StringComparison.OrdinalIgnoreCase)).ToList();
                var pivotLines = group.Where(r => string.Equals(getAccountSide(r), "P", StringComparison.OrdinalIgnoreCase)).ToList();

                if (receivableLines.Count > 0 && pivotLines.Count > 0)
                {
                    var missing = receivableLines.Sum(r => getSignedAmount(r)) + pivotLines.Sum(r => getSignedAmount(r));
                    foreach (var r in receivableLines) setMissingAmount(r, missing);
                    foreach (var p in pivotLines) setMissingAmount(p, missing);
                }
            }
        }

        /// <summary>
        /// Builds a fallback BGI key from a ReconciliationViewData row using heuristic extraction.
        /// Used as the getFallbackKey delegate for ComputeMatchedAcrossAccounts.
        /// </summary>
        public static string ExtractFallbackBgiKey(
            string dwingsInvoiceId,
            string receivableInvoiceFromAmbre,
            string reconciliationNum,
            string comments,
            string rawLabel,
            string receivableDwRef,
            string internalInvoiceRef)
        {
            string Norm(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

            var k = Norm(dwingsInvoiceId);
            if (!string.IsNullOrWhiteSpace(k)) return k;

            k = Norm(receivableInvoiceFromAmbre);
            if (!string.IsNullOrWhiteSpace(k)) return k;

            var token = DwingsLinkingHelper.ExtractBgiToken(reconciliationNum)
                       ?? DwingsLinkingHelper.ExtractBgiToken(comments)
                       ?? DwingsLinkingHelper.ExtractBgiToken(rawLabel)
                       ?? DwingsLinkingHelper.ExtractBgiToken(receivableDwRef);
            if (!string.IsNullOrWhiteSpace(token)) return Norm(token);

            return Norm(internalInvoiceRef);
        }
    }
}
