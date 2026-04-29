using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RecoTool.Services;
using RecoTool.Services.DTOs;

namespace RecoTool.Helpers
{
    /// <summary>
    /// Pre-built O(1) lookup dictionaries for DWINGS invoice resolution.
    /// Build once per import, reuse for all rows. Thread-safe for concurrent reads.
    /// Replaces O(n) FirstOrDefault scans that caused 20+ min imports on 20K rows.
    /// </summary>
    public class DwingsInvoiceLookup
    {
        private readonly Dictionary<string, DwingsInvoiceDto> _byInvoiceId;
        private readonly Dictionary<string, DwingsInvoiceDto> _byBgpmt;
        private readonly Dictionary<string, List<DwingsInvoiceDto>> _byGuarantee;

        public DwingsInvoiceLookup(IReadOnlyList<DwingsInvoiceDto> invoices)
        {
            var count = invoices?.Count ?? 0;
            _byInvoiceId = new Dictionary<string, DwingsInvoiceDto>(count, StringComparer.OrdinalIgnoreCase);
            _byBgpmt = new Dictionary<string, DwingsInvoiceDto>(count, StringComparer.OrdinalIgnoreCase);
            _byGuarantee = new Dictionary<string, List<DwingsInvoiceDto>>(count, StringComparer.OrdinalIgnoreCase);

            if (invoices == null) return;
            foreach (var inv in invoices)
            {
                if (inv == null) continue;

                var invId = inv.INVOICE_ID?.Trim();
                if (!string.IsNullOrEmpty(invId) && !_byInvoiceId.ContainsKey(invId))
                    _byInvoiceId[invId] = inv;

                var bgpmt = inv.BGPMT?.Trim();
                if (!string.IsNullOrEmpty(bgpmt) && !_byBgpmt.ContainsKey(bgpmt))
                    _byBgpmt[bgpmt] = inv;

                IndexGuarantee(inv, inv.BUSINESS_CASE_REFERENCE);
                IndexGuarantee(inv, inv.BUSINESS_CASE_ID);
            }
        }

        private void IndexGuarantee(DwingsInvoiceDto inv, string key)
        {
            var k = key?.Trim();
            if (string.IsNullOrEmpty(k)) return;
            if (!_byGuarantee.TryGetValue(k, out var list))
            {
                list = new List<DwingsInvoiceDto>(4);
                _byGuarantee[k] = list;
            }
            list.Add(inv);
        }

        public DwingsInvoiceDto FindByInvoiceId(string bgi)
        {
            if (string.IsNullOrWhiteSpace(bgi)) return null;
            _byInvoiceId.TryGetValue(bgi.Trim(), out var result);
            return result;
        }

        public DwingsInvoiceDto FindByBgpmt(string bgpmt)
        {
            if (string.IsNullOrWhiteSpace(bgpmt)) return null;
            _byBgpmt.TryGetValue(bgpmt.Trim(), out var result);
            return result;
        }

        /// <summary>Returns invoices linked to a guarantee (by BUSINESS_CASE_REFERENCE or BUSINESS_CASE_ID). O(1) lookup.</summary>
        public List<DwingsInvoiceDto> FindByGuarantee(string guaranteeId)
        {
            if (string.IsNullOrWhiteSpace(guaranteeId)) return null;
            _byGuarantee.TryGetValue(guaranteeId.Trim(), out var result);
            return result;
        }
    }

    public static class DwingsLinkingHelper
    {
        // ── PERF: Compiled Regex instances (avoid recompilation per call — called ~12× per row × 20K rows) ──
        private static readonly Regex RxBgpmt = new Regex(
            @"(?:^|[^A-Za-z0-9])(BGPMT[A-Za-z0-9]{8,20})(?![A-Za-z0-9])",
            RegexOptions.Compiled);
        private static readonly Regex RxBgi = new Regex(
            @"(?:^|[^A-Za-z0-9])(BGI(?:\d{6}[A-F0-9]{7}|\d{4}[A-Za-z]{2}[A-F0-9]{7}))(?![A-Za-z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RxGuarantee = new Regex(
            @"(?:^|[^A-Za-z0-9])([GN]\d{4}[A-Za-z]{2}\d{9})(?![A-Za-z0-9])",
            RegexOptions.Compiled);

        // BGPMT token: e.g., BGPMTxxxxxxxx (8-20 alnum after BGPMT)
        public static string ExtractBgpmtToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = RxBgpmt.Match(s);
            return m.Success ? m.Groups[1].Value : null;
        }

        // BGI invoice id supported formats:
        //  - BGI + YYYYMM (6 digits) + 7 chars (digits or A-F)
        //  - BGI + YYMM (4 digits) + CountryCode (2 letters) + 7 chars (digits or A-F)
        public static string ExtractBgiToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = RxBgi.Match(s);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        // Guarantee ID: G####AA######### or N####AA######### (G or N, 4 digits, 2 letters, 9 digits)
        public static string ExtractGuaranteeId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = RxGuarantee.Match(s);
            return m.Success ? m.Groups[1].Value : null;
        }

        // EndToEndId from MX/pacs payload (any namespace prefix). Examples seen in payloads:
        //   <pacs:EndToEndId>700.678</pacs:EndToEndId>
        //   <EndToEndId>BGI20231024E1F84</EndToEndId>
        // The EndToEndId is the originator's transaction reference and is, in practice,
        // the most reliable token to identify the underlying DWINGS guarantee/invoice.
        // See: ISO 20022 PaymentInstruction.PmtId.EndToEndId.
        private static readonly Regex RxEndToEndId = new Regex(
            @"<(?:[A-Za-z0-9_]+:)?EndToEndId\b[^>]*>\s*([^<\s]+)\s*</(?:[A-Za-z0-9_]+:)?EndToEndId>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string ExtractEndToEndId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = RxEndToEndId.Match(s);
            if (!m.Success) return null;
            var raw = m.Groups[1].Value?.Trim();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        // -------- Resolution helpers --------

        private static string Norm(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();

        public static bool AmountMatches(decimal? ambreAmount, decimal? dwAmount, decimal tolerance = 0.01m)
        {
            if (!ambreAmount.HasValue || !dwAmount.HasValue) return false;
            return Math.Abs(ambreAmount.Value - dwAmount.Value) <= tolerance;
        }

        /// <summary>
        /// Resolve a DWINGS invoice by exact BGI match.
        /// BGI = INVOICE_ID (unique identifier), no matching needed.
        /// Returns the first invoice found with this BGI (even if multiple exist - we take the first one).
        /// </summary>
        public static DwingsInvoiceDto ResolveInvoiceByBgi(
            IEnumerable<DwingsInvoiceDto> invoices,
            string bgi)
        {
            if (invoices == null) return null;
            var key = Norm(bgi);
            if (string.IsNullOrWhiteSpace(key)) return null;

            return invoices.FirstOrDefault(i => Norm(i?.INVOICE_ID) == key);
        }

        /// <summary>
        /// Return invoices that belong to a given GuaranteeId.  
        /// • The amount of the Ambre line **must always** match the invoice (±0.01).  
        /// • If at least one invoice has T_INVOICE_STATUS = “INITIATED”, only those invoices are considered.  
        /// • If more than one INITIATED invoice remains and they have identical scores (date + amount) → no result.  
        /// • If no INITIATED invoice exists and **more than one** invoice matches the amount → no result.  
        /// • Otherwise the best‑scored invoice(s) are returned (sorted by date‑proximity then amount‑proximity).
        /// </summary>
        public static List<DwingsInvoiceDto> ResolveInvoicesByGuarantee(
            IEnumerable<DwingsInvoiceDto> invoices,
            string guaranteeId,
            DateTime? ambreDate,
            decimal? ambreAmount,
            int take = 50)
        {
            try
            {
                // -----------------------------------------------------------------
                // 0️⃣  Normalise input and guard‑clause
                // -----------------------------------------------------------------
                var list = (invoices ?? Enumerable.Empty<DwingsInvoiceDto>()).ToList();
                var key = Norm(guaranteeId);
                if (string.IsNullOrWhiteSpace(key) || list.Count == 0)
                    return new List<DwingsInvoiceDto>();

                // -----------------------------------------------------------------
                // 1️⃣  Exact / partial match on the guarantee id
                // -----------------------------------------------------------------
                bool MatchEq(DwingsInvoiceDto i) =>
                    Norm(i?.BUSINESS_CASE_REFERENCE) == key ||
                    Norm(i?.BUSINESS_CASE_ID) == key;

                bool MatchContains(DwingsInvoiceDto i) =>
                    (!string.IsNullOrEmpty(i?.BUSINESS_CASE_REFERENCE) &&
                     Norm(i.BUSINESS_CASE_REFERENCE)?.Contains(key) == true) ||
                    (!string.IsNullOrEmpty(i?.BUSINESS_CASE_ID) &&
                     Norm(i.BUSINESS_CASE_ID)?.Contains(key) == true);

                var exact = list.Where(MatchEq).ToList();
                var partial = exact.Any() ? new List<DwingsInvoiceDto>() : list.Where(MatchContains).ToList();
                var candidates = exact.Any() ? exact : partial;

                if (!candidates.Any())
                    return new List<DwingsInvoiceDto>();

                // -----------------------------------------------------------------
                // 2️⃣  **Amount filter – ALWAYS applied** (tolerance 0.01)
                // -----------------------------------------------------------------
                if (ambreAmount.HasValue)
                {
                    decimal absAmbre = Math.Abs(ambreAmount.Value);
                    candidates = candidates.Where(i =>
                    {
                        bool reqMatch = i?.REQUESTED_AMOUNT.HasValue == true &&
                            AmountMatches(absAmbre, Math.Abs(i.REQUESTED_AMOUNT.Value), tolerance: 0.01m);
                        bool billMatch = i?.BILLING_AMOUNT.HasValue == true &&
                            AmountMatches(absAmbre, Math.Abs(i.BILLING_AMOUNT.Value), tolerance: 0.01m);
                        return reqMatch || billMatch;
                    }).ToList();

                    if (!candidates.Any())
                        return new List<DwingsInvoiceDto>();
                }

                // -----------------------------------------------------------------
                // 3️⃣  Split into INITIATED / non‑INITIATED
                // -----------------------------------------------------------------
                var initiated = candidates
                    .Where(i => string.Equals(i?.T_INVOICE_STATUS, "INITIATED",
                                             StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // -----------------------------------------------------------------
                // 4️⃣  Decide which set we really work with
                // -----------------------------------------------------------------
                List<DwingsInvoiceDto> workingSet;

                if (initiated.Any())
                {
                    // We have at least one INITIATED → keep only those
                    workingSet = initiated;
                }
                else
                {
                    //No initiated => we don't try
                    return new List<DwingsInvoiceDto>();
                }

                // -----------------------------------------------------------------
                // 5️⃣  Scoring – date proximity then amount proximity
                // -----------------------------------------------------------------
                Func<DwingsInvoiceDto, double> dateScore = i =>
                {
                    if (!ambreDate.HasValue) return double.MaxValue;
                    var best = i?.REQUESTED_EXECUTION_DATE ?? i?.START_DATE ?? i?.END_DATE;
                    return best.HasValue
                        ? Math.Abs((best.Value.Date - ambreDate.Value.Date).TotalDays)
                        : double.MaxValue;
                };

                Func<DwingsInvoiceDto, decimal> amountScore = i =>
                {
                    if (!ambreAmount.HasValue) return decimal.MaxValue;
                    var absAmbre = Math.Abs(ambreAmount.Value);
                    var reqDelta = i?.REQUESTED_AMOUNT.HasValue == true
                        ? Math.Abs(absAmbre - i.REQUESTED_AMOUNT.Value)
                        : decimal.MaxValue;
                    var billDelta = i?.BILLING_AMOUNT.HasValue == true
                        ? Math.Abs(absAmbre - i.BILLING_AMOUNT.Value)
                        : decimal.MaxValue;
                    return Math.Min(reqDelta, billDelta);
                };

                var sorted = workingSet
                    .OrderBy(dateScore)
                    .ThenBy(amountScore)
                    .ToList();

                // -----------------------------------------------------------------
                // 6️⃣  Ambiguity check when the caller asks for a single result (take == 1)
                // -----------------------------------------------------------------
                if (take == 1 && sorted.Count >= 2)
                {
                    var first = sorted[0];
                    var second = sorted[1];

                    // Same date‑proximity *and* same amount‑proximity → ambiguous
                    if (Math.Abs(dateScore(first) - dateScore(second)) < 0.01 &&
                        Math.Abs(amountScore(first) - amountScore(second)) < 0.01m)
                    {
                        return new List<DwingsInvoiceDto>(); // ambiguous – return empty
                    }
                }

                // -----------------------------------------------------------------
                // 7️⃣  Return the requested number of rows (at least one)
                // -----------------------------------------------------------------
                return sorted.Take(Math.Max(1, take)).ToList();
            } catch
            {
                return new List<DwingsInvoiceDto>();  //return empty 
            }
        }

        /// <summary>
        /// Suggest a best invoice for a given AMBRE item using BGI → BGPMT → Guarantee strategies.
        /// Returns a ranked list (best first).
        /// </summary>
        public static List<DwingsInvoiceDto> SuggestInvoicesForAmbre(
            IEnumerable<DwingsInvoiceDto> invoices,
            string rawLabel,
            string reconciliationNum,
            string reconciliationOriginNum,
            string explicitBgi,
            string guaranteeId,
            DateTime? ambreDate,
            decimal? ambreAmount,
            int take = 20)
        {
            var list = new List<DwingsInvoiceDto>();
            // 1) BGI direct
            var bgi = explicitBgi?.Trim()
                      ?? ExtractBgiToken(reconciliationNum)
                      ?? ExtractBgiToken(reconciliationOriginNum)
                      ?? ExtractBgiToken(rawLabel);
            if (!string.IsNullOrWhiteSpace(bgi))
            {
                var hit = ResolveInvoiceByBgi(invoices, bgi);
                if (hit != null) list.Add(hit);
            }
            if (list.Count >= take) return list;

            // 2) BGPMT
            var bgpmt = ExtractBgpmtToken(reconciliationNum)
                        ?? ExtractBgpmtToken(reconciliationOriginNum)
                        ?? ExtractBgpmtToken(rawLabel);
            if (!string.IsNullOrWhiteSpace(bgpmt))
            {
                var hit = ResolveInvoiceByBgpmt(invoices, bgpmt);
                if (hit != null && !list.Any(x => Norm(x.INVOICE_ID) == Norm(hit.INVOICE_ID))) list.Add(hit);
            }
            if (list.Count >= take) return list;

            // 3) Guarantee-based
            var gid = guaranteeId?.Trim() ?? ExtractGuaranteeId(reconciliationNum) ?? ExtractGuaranteeId(rawLabel);
            if (!string.IsNullOrWhiteSpace(gid))
            {
                var more = ResolveInvoicesByGuarantee(invoices, gid, ambreDate, ambreAmount, take: take - list.Count);
                foreach (var m in more)
                {
                    if (!list.Any(x => Norm(x.INVOICE_ID) == Norm(m.INVOICE_ID))) list.Add(m);
                }
            }

            return list.Take(take).ToList();
        }

        /// <summary>
        /// Resolve a DWINGS invoice by exact BGPMT match.
        /// BGPMT = commission identifier, no matching needed.
        /// Returns the first invoice found with this BGPMT (even if multiple exist - we take the first one).
        /// </summary>
        public static DwingsInvoiceDto ResolveInvoiceByBgpmt(
            IEnumerable<DwingsInvoiceDto> invoices,
            string bgpmt)
        {
            if (invoices == null) return null;
            var key = Norm(bgpmt);
            if (string.IsNullOrWhiteSpace(key)) return null;

            return invoices.FirstOrDefault(i => Norm(i?.BGPMT) == key);
        }

        // ────────── O(1) lookup-aware overloads for bulk import ──────────

        public static DwingsInvoiceDto ResolveInvoiceByBgi(DwingsInvoiceLookup lookup, string bgi)
            => lookup?.FindByInvoiceId(bgi);

        public static DwingsInvoiceDto ResolveInvoiceByBgpmt(DwingsInvoiceLookup lookup, string bgpmt)
            => lookup?.FindByBgpmt(bgpmt);

        /// <summary>
        /// O(1) guarantee lookup + same filtering/scoring as the O(n) overload.
        /// </summary>
        public static List<DwingsInvoiceDto> ResolveInvoicesByGuarantee(
            DwingsInvoiceLookup lookup,
            string guaranteeId,
            DateTime? ambreDate,
            decimal? ambreAmount,
            int take = 50)
        {
            try
            {
                var key = Norm(guaranteeId);
                if (string.IsNullOrWhiteSpace(key) || lookup == null)
                    return new List<DwingsInvoiceDto>();

                var candidates = lookup.FindByGuarantee(key);
                if (candidates == null || candidates.Count == 0)
                    return new List<DwingsInvoiceDto>();

                // ── Amount filter ──
                if (ambreAmount.HasValue)
                {
                    decimal absAmbre = Math.Abs(ambreAmount.Value);
                    candidates = candidates.Where(i =>
                    {
                        bool reqMatch = i?.REQUESTED_AMOUNT.HasValue == true &&
                            AmountMatches(absAmbre, Math.Abs(i.REQUESTED_AMOUNT.Value), tolerance: 0.01m);
                        bool billMatch = i?.BILLING_AMOUNT.HasValue == true &&
                            AmountMatches(absAmbre, Math.Abs(i.BILLING_AMOUNT.Value), tolerance: 0.01m);
                        return reqMatch || billMatch;
                    }).ToList();

                    if (candidates.Count == 0)
                        return new List<DwingsInvoiceDto>();
                }

                // ── INITIATED filter ──
                var initiated = candidates
                    .Where(i => string.Equals(i?.T_INVOICE_STATUS, "INITIATED",
                                             StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!initiated.Any())
                    return new List<DwingsInvoiceDto>();

                var workingSet = initiated;

                // ── Scoring ──
                Func<DwingsInvoiceDto, double> dateScore = i =>
                {
                    if (!ambreDate.HasValue) return double.MaxValue;
                    var best = i?.REQUESTED_EXECUTION_DATE ?? i?.START_DATE ?? i?.END_DATE;
                    return best.HasValue
                        ? Math.Abs((best.Value.Date - ambreDate.Value.Date).TotalDays)
                        : double.MaxValue;
                };

                Func<DwingsInvoiceDto, decimal> amountScore = i =>
                {
                    if (!ambreAmount.HasValue) return decimal.MaxValue;
                    var absAmbre = Math.Abs(ambreAmount.Value);
                    var reqDelta = i?.REQUESTED_AMOUNT.HasValue == true
                        ? Math.Abs(absAmbre - i.REQUESTED_AMOUNT.Value)
                        : decimal.MaxValue;
                    var billDelta = i?.BILLING_AMOUNT.HasValue == true
                        ? Math.Abs(absAmbre - i.BILLING_AMOUNT.Value)
                        : decimal.MaxValue;
                    return Math.Min(reqDelta, billDelta);
                };

                var sorted = workingSet.OrderBy(dateScore).ThenBy(amountScore).ToList();

                // ── Ambiguity check ──
                if (take == 1 && sorted.Count >= 2)
                {
                    var first = sorted[0];
                    var second = sorted[1];
                    if (Math.Abs(dateScore(first) - dateScore(second)) < 0.01 &&
                        Math.Abs(amountScore(first) - amountScore(second)) < 0.01m)
                        return new List<DwingsInvoiceDto>();
                }

                return sorted.Take(Math.Max(1, take)).ToList();
            }
            catch
            {
                return new List<DwingsInvoiceDto>();
            }
        }

        /// <summary>
        /// O(1) version of SuggestInvoicesForAmbre using pre-built lookup.
        /// </summary>
        public static List<DwingsInvoiceDto> SuggestInvoicesForAmbre(
            DwingsInvoiceLookup lookup,
            string rawLabel,
            string reconciliationNum,
            string reconciliationOriginNum,
            string explicitBgi,
            string guaranteeId,
            DateTime? ambreDate,
            decimal? ambreAmount,
            int take = 20)
        {
            if (lookup == null) return new List<DwingsInvoiceDto>();
            var list = new List<DwingsInvoiceDto>();

            // 1) BGI direct
            var bgi = explicitBgi?.Trim()
                      ?? ExtractBgiToken(reconciliationNum)
                      ?? ExtractBgiToken(reconciliationOriginNum)
                      ?? ExtractBgiToken(rawLabel);
            if (!string.IsNullOrWhiteSpace(bgi))
            {
                var hit = lookup.FindByInvoiceId(bgi);
                if (hit != null) list.Add(hit);
            }
            if (list.Count >= take) return list;

            // 2) BGPMT
            var bgpmtToken = ExtractBgpmtToken(reconciliationNum)
                        ?? ExtractBgpmtToken(reconciliationOriginNum)
                        ?? ExtractBgpmtToken(rawLabel);
            if (!string.IsNullOrWhiteSpace(bgpmtToken))
            {
                var hit = lookup.FindByBgpmt(bgpmtToken);
                if (hit != null && !list.Any(x => Norm(x.INVOICE_ID) == Norm(hit.INVOICE_ID))) list.Add(hit);
            }
            if (list.Count >= take) return list;

            // 3) Guarantee-based
            var gid = guaranteeId?.Trim() ?? ExtractGuaranteeId(reconciliationNum) ?? ExtractGuaranteeId(rawLabel);
            if (!string.IsNullOrWhiteSpace(gid))
            {
                var more = ResolveInvoicesByGuarantee(lookup, gid, ambreDate, ambreAmount, take: take - list.Count);
                foreach (var m in more)
                {
                    if (!list.Any(x => Norm(x.INVOICE_ID) == Norm(m.INVOICE_ID))) list.Add(m);
                }
            }

            return list.Take(take).ToList();
        }
    }
}
