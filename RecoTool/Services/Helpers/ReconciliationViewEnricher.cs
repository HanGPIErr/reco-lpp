using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RecoTool.Helpers;
using RecoTool.Services.DTOs;

namespace RecoTool.Services.Helpers
{
    /// <summary>
    /// Enriches ReconciliationViewData rows with DWINGS invoice fields using an in-memory list
    /// of DwingsInvoiceDto records and lightweight heuristics.
    /// </summary>
    public static class ReconciliationViewEnricher
    {
        /// <summary>
        /// Links reconciliation rows to DWINGS invoices by setting DWINGS_InvoiceID
        /// OPTIMIZED: No longer enriches all I_* properties (now lazy-loaded on demand)
        /// NOTE: Assumes DWINGS caches are already initialized by ReconciliationService
        /// </summary>
        public static void EnrichWithDwingsInvoices(List<ReconciliationViewData> rows, IEnumerable<DwingsInvoiceDto> invoices)
        {
            if (rows == null || invoices == null) return;

            // NOTE: Do NOT reinitialize caches here - they are managed by ReconciliationService
            // ReconciliationViewData.InitializeDwingsCaches(invoices, null);

            // Build quick lookups (handle duplicates gracefully by picking first)
            var invoiceList = invoices as IList<DwingsInvoiceDto> ?? invoices.ToList();
            var byInvoiceId = invoiceList.Where(i => !string.IsNullOrWhiteSpace(i.INVOICE_ID))
                                         .GroupBy(i => i.INVOICE_ID, StringComparer.OrdinalIgnoreCase)
                                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byBgpmt = invoiceList.Where(i => !string.IsNullOrWhiteSpace(i.BGPMT))
                                      .GroupBy(i => i.BGPMT, StringComparer.OrdinalIgnoreCase)
                                      .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                DwingsInvoiceDto inv = null;
                // Receivable rule: if Receivable_InvoiceFromAmbre (BGI) is present, use ONLY this to link to DWINGS invoice.
                // Do not fall back to other heuristics in this case.
                if (!string.IsNullOrWhiteSpace(row.Receivable_InvoiceFromAmbre))
                {
                    if (byInvoiceId.TryGetValue(row.Receivable_InvoiceFromAmbre, out var foundByReceivable))
                    {
                        inv = foundByReceivable;
                        // Strict rule: on receivable, always bind using Receivable_InvoiceFromAmbre
                        row.DWINGS_InvoiceID = inv.INVOICE_ID;
                    }
                }
                // Else apply existing resolution order
                else
                {
                    // 1) Direct by DWINGS_InvoiceID
                    if (!string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID) && byInvoiceId.TryGetValue(row.DWINGS_InvoiceID, out var foundById))
                    {
                        inv = foundById;
                    }
                    // 2) By stored PaymentReference (BGPMT)
                    else if (!string.IsNullOrWhiteSpace(row.PaymentReference) && byBgpmt.TryGetValue(row.PaymentReference, out var foundByPm))
                    {
                        inv = foundByPm;
                    }
                    // 3) By stored DWINGS_BGPMT (BGPMT) when PaymentReference is not set
                    else if (!string.IsNullOrWhiteSpace(row.DWINGS_BGPMT) && byBgpmt.TryGetValue(row.DWINGS_BGPMT, out var foundByCommission))
                    {
                        inv = foundByCommission;
                        if (string.IsNullOrWhiteSpace(row.PaymentReference)) row.PaymentReference = row.DWINGS_BGPMT;
                    }
                    else
                    {
                        // 4) Heuristic: extract BGI or BGPMT from available texts (pivot or when no Receivable_InvoiceFromAmbre available)
                        string TryNonEmpty(params string[] ss)
                        {
                            foreach (var s in ss)
                                if (!string.IsNullOrWhiteSpace(s)) return s;
                            return null;
                        }

                        // Extract tokens from potential sources
                        var bgi = DwingsLinkingHelper.ExtractBgiToken(TryNonEmpty(row.Reconciliation_Num))
                                  ?? DwingsLinkingHelper.ExtractBgiToken(TryNonEmpty(row.Comments))
                                  ?? DwingsLinkingHelper.ExtractBgiToken(TryNonEmpty(row.RawLabel))
                                  ?? DwingsLinkingHelper.ExtractBgiToken(TryNonEmpty(row.Receivable_DWRefFromAmbre));

                        var bgpmt = DwingsLinkingHelper.ExtractBgpmtToken(TryNonEmpty(row.Reconciliation_Num))
                                   ?? DwingsLinkingHelper.ExtractBgpmtToken(TryNonEmpty(row.Comments))
                                   ?? DwingsLinkingHelper.ExtractBgpmtToken(TryNonEmpty(row.RawLabel))
                                   ?? DwingsLinkingHelper.ExtractBgpmtToken(TryNonEmpty(row.PaymentReference));

                        if (!string.IsNullOrWhiteSpace(bgi) && byInvoiceId.TryGetValue(bgi, out var foundByBgi))
                        {
                            inv = foundByBgi;
                            // Backfill missing fields to strengthen link in UI
                            if (string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID)) row.DWINGS_InvoiceID = inv.INVOICE_ID;
                        }
                        else if (!string.IsNullOrWhiteSpace(bgpmt) && byBgpmt.TryGetValue(bgpmt, out var foundByBgpmt))
                        {
                            inv = foundByBgpmt;
                            if (string.IsNullOrWhiteSpace(row.PaymentReference)) row.PaymentReference = bgpmt;
                            if (string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID)) row.DWINGS_InvoiceID = inv.INVOICE_ID;
                        }
                    }
                }

                // ── Pivot fallback: GuaranteeID + amount + INITIATED → unique invoice ──
                // When a pivot row has the guarantee reference (typically pasted from email/Free
                // search) but no BGI/BGPMT yet, try to resolve a unique INITIATED invoice with
                // matching amount. This mirrors DwingsReferenceResolver.ResolveReferences but
                // applies at view-refresh time so the link appears as soon as DWINGS data is in
                // sync (e.g. after a new invoice has been initiated for an existing guarantee).
                if (inv == null
                    && string.Equals(row.AccountSide, "P", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID)
                    && string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID)
                    && string.IsNullOrWhiteSpace(row.DWINGS_BGPMT))
                {
                    var matches = DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                        invoiceList,
                        row.DWINGS_GuaranteeID,
                        row.Operation_Date ?? row.Value_Date,
                        row.SignedAmount,
                        take: 1);
                    var matched = matches?.FirstOrDefault();
                    if (matched != null)
                    {
                        inv = matched;
                        row.DWINGS_InvoiceID = matched.INVOICE_ID;
                        if (string.IsNullOrWhiteSpace(row.DWINGS_BGPMT)) row.DWINGS_BGPMT = matched.BGPMT;
                        if (string.IsNullOrWhiteSpace(row.PaymentReference) && !string.IsNullOrWhiteSpace(matched.BGPMT))
                            row.PaymentReference = matched.BGPMT;
                    }
                }

                if (inv != null)
                {
                    // OPTIMIZED: Only set the linking fields, all I_* properties are now lazy-loaded
                    row.INVOICE_ID = inv.INVOICE_ID;

                    // ── Seed FirstClaimDate from invoice MT_DATE when ACK'd ──
                    // Business rule: once the bank has acknowledged the wire, MT_DATE on the linked
                    // DWINGS invoice is the authoritative "first claim" date. We only fill empty
                    // FirstClaimDate values (never overwrite a user-edited one) and only when ACK is
                    // true — for non-ACK rows the MT_DATE is meaningless as a claim reference.
                    // UI-only seed: persistence happens lazily on the next user save (the value will
                    // round-trip through SaveReconciliation* after the user opens & saves the row).
                    if (row.ACK && !row.FirstClaimDate.HasValue && inv.MT_DATE.HasValue)
                    {
                        row.FirstClaimDate = inv.MT_DATE;
                    }
                }
            }
        }
        
        /// <summary>
        /// Retries DWINGS linking for Receivable rows that have a BGI (Receivable_InvoiceFromAmbre)
        /// but were not linked to a DWINGS invoice (DWINGS_InvoiceID is empty).
        /// This handles the case where DWINGS data was not yet available at import time.
        /// Only targets: Receivable + BGI present + DWINGS_InvoiceID missing.
        /// Returns the number of newly linked rows.
        /// </summary>
        public static int RetryUnlinkedReceivableBgi(List<ReconciliationViewData> rows, IEnumerable<DwingsInvoiceDto> invoices)
        {
            if (rows == null || invoices == null) return 0;

            // Only build lookup if there are unlinked candidates
            List<ReconciliationViewData> unlinked = null;
            foreach (var r in rows)
            {
                if (!string.IsNullOrWhiteSpace(r.Receivable_InvoiceFromAmbre)
                    && string.IsNullOrWhiteSpace(r.DWINGS_InvoiceID))
                {
                    if (unlinked == null) unlinked = new List<ReconciliationViewData>();
                    unlinked.Add(r);
                }
            }
            if (unlinked == null || unlinked.Count == 0) return 0;

            var byInvoiceId = (invoices as IList<DwingsInvoiceDto> ?? invoices.ToList())
                .Where(i => !string.IsNullOrWhiteSpace(i.INVOICE_ID))
                .GroupBy(i => i.INVOICE_ID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            int linked = 0;
            foreach (var row in unlinked)
            {
                if (byInvoiceId.TryGetValue(row.Receivable_InvoiceFromAmbre, out var inv))
                {
                    row.DWINGS_InvoiceID = inv.INVOICE_ID;
                    row.INVOICE_ID = inv.INVOICE_ID;
                    linked++;
                }
            }
            return linked;
        }

        /// <summary>
        /// Phase 2 — Partially-paid BGI tracking (Auto by group balance).
        /// <para>
        /// Computes <see cref="ReconciliationViewData.GroupBalance"/> for every row by summing
        /// <c>SignedAmount</c> across all rows sharing the same group key. The grouping is aligned
        /// with <c>RowActions.SingleProcessDwings_Click</c>: <c>InternalInvoiceReference</c> is
        /// the primary key, with <c>DWINGS_BGPMT</c> as fallback when no internal ref is set.
        /// </para>
        /// <para>
        /// A row that has neither identifier is left untouched (<c>GroupBalance = null</c>) and is
        /// therefore considered standalone — never partially paid by the auto rule.
        /// </para>
        /// <para>
        /// Deleted rows are excluded from the balance because the Trigger flow already ignores
        /// them; including them would skew the sum and cause false "still owed" verdicts.
        /// </para>
        /// </summary>
        /// <remarks>
        /// O(N) over the input. Run once after every full enrichment pass (refresh, basket link,
        /// edit) — the result drives the read-only "Effective remaining" displayed in the detail
        /// dialog and the bulk-Trigger eligibility filter.
        /// </remarks>
        public static void ComputeAndApplyGroupBalances(IList<ReconciliationViewData> rows)
        {
            if (rows == null || rows.Count == 0) return;

            // Local helper: same precedence rule as the Trigger flow uses to assemble groups.
            string KeyOf(ReconciliationViewData r)
            {
                if (r == null || r.IsDeleted) return null;
                var inv = r.InternalInvoiceReference?.Trim();
                if (!string.IsNullOrEmpty(inv)) return "REF:" + inv.ToUpperInvariant();
                var bg = r.DWINGS_BGPMT?.Trim();
                if (!string.IsNullOrEmpty(bg)) return "BGPMT:" + bg.ToUpperInvariant();
                return null;
            }

            // Single-pass aggregation. Using decimal explicitly — SignedAmount on DataAmbre is
            // already decimal, so no float drift creeps in.
            var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var k = KeyOf(r);
                if (k == null) continue;
                if (balances.TryGetValue(k, out var v)) balances[k] = v + r.SignedAmount;
                else balances[k] = r.SignedAmount;
            }

            // Application pass. Rows outside any group get null so the detail UI knows the
            // auto-rule has nothing to say (vs "computed and equal to 0", which is "fully paid").
            foreach (var r in rows)
            {
                if (r == null) continue;
                var k = KeyOf(r);
                r.GroupBalance = (k != null && balances.TryGetValue(k, out var v)) ? v : (decimal?)null;
            }
        }

        /// <summary>
        /// Calculates missing amounts for grouped lines (Receivable vs Pivot)
        /// Groups by InternalInvoiceReference or DWINGS_InvoiceID.
        ///
        /// PERF: rewritten as 2-pass aggregation. The previous LINQ implementation did
        /// GroupBy → ToList → for-each (group → Where(R) ToList + Where(P) ToList +
        /// Sum + Sum), allocating ~5 enumerators per group + intermediate Lists.
        /// On 20k rows we measured ~80-120 ms.
        ///
        /// New approach:
        ///   Pass 1 — accumulate Receivable/Pivot totals + counts per key into a single
        ///            Dictionary&lt;string, GroupAggregate&gt; (struct value, no boxing).
        ///   Pass 2 — apply aggregates back onto each row.
        /// Allocates exactly 1 dictionary. ~10-20 ms on 20k rows in our profiling.
        /// </summary>
        public static void CalculateMissingAmounts(List<ReconciliationViewData> rows, string receivableAccountId, string pivotAccountId)
        {
            if (rows == null || rows.Count == 0
                || string.IsNullOrWhiteSpace(receivableAccountId)
                || string.IsNullOrWhiteSpace(pivotAccountId))
                return;

            // OrdinalIgnoreCase comparer means we don't need ToUpperInvariant on the key
            // (which would allocate a new string per row).
            var aggregates = new Dictionary<string, GroupAggregate>(rows.Count / 4 + 16, StringComparer.OrdinalIgnoreCase);

            // ── Pass 1: aggregate per group key ────────────────────────────────────────
            // Internal ref takes priority over DWINGS id (basket-linking semantics).
            // Skip rows that have neither, or that aren't on receivable/pivot accounts.
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];

                string key;
                var inv = r.InternalInvoiceReference;
                if (!string.IsNullOrWhiteSpace(inv))
                {
                    key = inv.Trim();
                }
                else
                {
                    var dw = r.DWINGS_InvoiceID;
                    if (string.IsNullOrWhiteSpace(dw)) continue;
                    key = dw.Trim();
                }
                if (key.Length == 0) continue;

                bool isReceivable = r.Account_ID == receivableAccountId;
                bool isPivot = r.Account_ID == pivotAccountId;
                if (!isReceivable && !isPivot) continue;

                aggregates.TryGetValue(key, out var agg);
                if (isReceivable)
                {
                    agg.ReceivableTotal += r.SignedAmount;
                    agg.ReceivableCount++;
                }
                else
                {
                    agg.PivotTotal += r.SignedAmount;
                    agg.PivotCount++;
                }
                aggregates[key] = agg;
            }

            // ── Pass 2: apply aggregates back ──────────────────────────────────────────
            // Skip groups that don't have BOTH sides (no missing-amount semantics).
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];

                string key;
                var inv = r.InternalInvoiceReference;
                if (!string.IsNullOrWhiteSpace(inv))
                {
                    key = inv.Trim();
                }
                else
                {
                    var dw = r.DWINGS_InvoiceID;
                    if (string.IsNullOrWhiteSpace(dw)) continue;
                    key = dw.Trim();
                }
                if (key.Length == 0) continue;

                if (!aggregates.TryGetValue(key, out var agg)) continue;
                if (agg.ReceivableCount == 0 || agg.PivotCount == 0) continue;

                // MissingAmount = ReceivableTotal + PivotTotal (Receivable is typically
                // negative, Pivot positive — sums to 0 when balanced). Identical value
                // on both sides; the detail dialog highlights the imbalance regardless
                // of which side the user is looking at.
                decimal missing = agg.ReceivableTotal + agg.PivotTotal;

                if (r.Account_ID == receivableAccountId)
                {
                    r.CounterpartTotalAmount = agg.PivotTotal;
                    r.CounterpartCount = agg.PivotCount;
                    r.MissingAmount = missing;
                }
                else if (r.Account_ID == pivotAccountId)
                {
                    r.CounterpartTotalAmount = agg.ReceivableTotal;
                    r.CounterpartCount = agg.ReceivableCount;
                    r.MissingAmount = missing;
                }
            }
        }

        // Tight per-key aggregate. Stored as a value type in the Dictionary so each
        // upsert is one entry write — no boxing, no extra heap object per group.
        private struct GroupAggregate
        {
            public decimal ReceivableTotal;
            public decimal PivotTotal;
            public int ReceivableCount;
            public int PivotCount;
        }
        
        /// <summary>
        /// Recalcule IsMatchedAcrossAccounts et MissingAmount pour un groupe spécifique seulement
        /// Version optimisée pour édition incrémentale (95% plus rapide que recalcul complet)
        /// </summary>
        /// <param name="allData">Toutes les données (pour trouver le groupe)</param>
        /// <param name="changedInvoiceRef">Référence modifiée (InternalInvoiceReference ou DWINGS_InvoiceID)</param>
        /// <param name="receivableAccountId">Account_ID Receivable</param>
        /// <param name="pivotAccountId">Account_ID Pivot</param>
        public static void RecalculateFlagsForGroup(
            IEnumerable<ReconciliationViewData> allData,
            string changedInvoiceRef,
            string receivableAccountId,
            string pivotAccountId)
        {
            if (allData == null || string.IsNullOrWhiteSpace(changedInvoiceRef))
                return;
            
            // Trouver toutes les lignes du groupe modifié
            var affectedRows = allData
                .Where(r => string.Equals(r.InternalInvoiceReference, changedInvoiceRef, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(r.DWINGS_InvoiceID, changedInvoiceRef, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            if (affectedRows.Count == 0) return;
            
            // Recalculer IsMatchedAcrossAccounts pour ce groupe
            bool hasP = affectedRows.Any(x => string.Equals(x.AccountSide, "P", StringComparison.OrdinalIgnoreCase));
            bool hasR = affectedRows.Any(x => string.Equals(x.AccountSide, "R", StringComparison.OrdinalIgnoreCase));
            bool isMatched = hasP && hasR;
            
            foreach (var row in affectedRows)
            {
                row.IsMatchedAcrossAccounts = isMatched;
            }
            
            // Recalculer MissingAmount pour ce groupe uniquement
            if (isMatched && !string.IsNullOrWhiteSpace(receivableAccountId) && !string.IsNullOrWhiteSpace(pivotAccountId))
            {
                var receivableLines = affectedRows.Where(r => r.Account_ID == receivableAccountId).ToList();
                var pivotLines = affectedRows.Where(r => r.Account_ID == pivotAccountId).ToList();
                
                if (receivableLines.Count > 0 && pivotLines.Count > 0)
                {
                    var receivableTotal = receivableLines.Sum(r => r.SignedAmount);
                    var pivotTotal = pivotLines.Sum(r => r.SignedAmount);
                    var missing = receivableTotal + pivotTotal; // Addition: should be 0 when balanced
                    
                    // Enrich Receivable lines
                    foreach (var r in receivableLines)
                    {
                        r.CounterpartTotalAmount = pivotTotal;
                        r.CounterpartCount = pivotLines.Count;
                        r.MissingAmount = missing;
                    }
                    
                    // Enrich Pivot lines
                    foreach (var p in pivotLines)
                    {
                        p.CounterpartTotalAmount = receivableTotal;
                        p.CounterpartCount = receivableLines.Count;
                        p.MissingAmount = missing; // Same value for both sides (not inverted)
                    }
                }
            }
            else
            {
                // Pas de matching ou pas de country info, reset les valeurs
                foreach (var row in affectedRows)
                {
                    row.MissingAmount = null;
                    row.CounterpartTotalAmount = null;
                    row.CounterpartCount = null;
                }
            }

            // Refresh pre-calculated display caches for all affected rows
            foreach (var row in affectedRows)
                row.PreCalculateDisplayProperties();
        }

        /// <summary>
        /// Assigns alternating InvoiceGroupBrush to rows that share the same InternalInvoiceReference.
        /// Groups with only one row (or no ref) get transparent. Others cycle through the palette.
        /// </summary>
        public static void AssignInvoiceGroupColors(IList<ReconciliationViewData> rows)
        {
            if (rows == null || rows.Count == 0) return;

            var palette = ReconciliationViewData.InvoiceGroupPalette;
            int colorIdx = 0;

            // Group by InternalInvoiceReference (only non-empty)
            var groups = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.InternalInvoiceReference))
                .GroupBy(r => r.InternalInvoiceReference.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1); // only color groups with 2+ rows

            var assignedKeys = new Dictionary<string, System.Windows.Media.SolidColorBrush>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var brush = palette[colorIdx % palette.Length];
                colorIdx++;
                foreach (var row in g)
                    row.InvoiceGroupBrush = brush;
                assignedKeys[g.Key] = brush;
            }

            // Clear brush for rows not in a multi-row group
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.InternalInvoiceReference)
                    || !assignedKeys.ContainsKey(row.InternalInvoiceReference.Trim()))
                {
                    row.InvoiceGroupBrush = null;
                }
            }
        }
    }
}
