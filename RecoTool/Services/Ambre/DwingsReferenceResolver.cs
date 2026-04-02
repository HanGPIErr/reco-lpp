using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using OfflineFirstAccess.Helpers;
using RecoTool.Helpers;
using RecoTool.Models;
using RecoTool.Services.Ambre;
using RecoTool.Services.DTOs;
using System.Threading;

namespace RecoTool.Services.Ambre
{
    public static class GuaranteeCache
    {
        // 0 = pas encore initialisé, 1 = initialisé
        private static int _state;                     // utilisé avec Interlocked pour être lock‑free
        private static volatile Dictionary<string, string>? _lookup; // OfficialRef (MAJ) → GuaranteeId

        /// <summary>
        /// Initialise le cache à partir d'une collection de garanties.
        /// L'appel est idempotent : la première invocation charge le dictionnaire,
        /// les appels suivants sont ignorés.
        /// Thread-safe : peut être appelé après Clear() pour rafraîchir le cache.
        /// </summary>
        public static void Initialise(IReadOnlyList<DwingsGuaranteeDto> guarantees)
        {
            // Si l'état était déjà 1, on sort immédiatement (déjà initialisé)
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 1)
                return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (guarantees != null)
            {
                foreach (var g in guarantees)
                {
                    var official = g.OFFICIALREF?.Trim();
                    var gid = g.GUARANTEE_ID?.Trim();

                    if (string.IsNullOrEmpty(official) || string.IsNullOrEmpty(gid))
                        continue;

                    var cleanOfficial = Regex.Replace(official, "[^a-zA-Z0-9]", string.Empty,
                                                  RegexOptions.CultureInvariant);

                    map[cleanOfficial.ToUpperInvariant()] = gid;
                }
            }

            // Publication atomique du dictionnaire (volatile field → visible immédiatement par les autres threads)
            _lookup = map;
        }

        /// <summary>
        /// Retourne le <c>GuaranteeId</c> dont la <c>OfficialRef</c> apparaît dans le payload.
        /// Retourne <c>null</c> si le cache n'est pas encore initialisé (pas d'exception).
        /// 
        /// Priority order:
        ///   1. Structured G/N-ref regex (e.g. G2603AT000651373) — highest confidence
        ///   2. Brute-force substring match on cleaned payload — only for refs that are
        ///      long enough and contain letters (to avoid false positives from timestamps,
        ///      amounts, IBANs, etc.)
        /// </summary>
        public static string? FindGuaranteeId(string payload)
        {
            var snapshot = _lookup;
            if (snapshot == null || string.IsNullOrWhiteSpace(payload))
                return null;

            // ── STRATEGY 1: Structured regex extraction (most reliable) ──
            // Extract all G/N-refs from the raw payload and check against known guarantees.
            // This avoids any cleaning/concatenation that destroys token boundaries.
            var gMatches = Regex.Matches(payload, @"[GN]\d{4}[A-Za-z]{2}\d{9}");
            foreach (System.Text.RegularExpressions.Match gm in gMatches)
            {
                var candidate = gm.Value.ToUpperInvariant();
                // Check if this guarantee ID is directly in the cache values
                if (snapshot.Values.Any(v => string.Equals(v, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }

            // ── STRATEGY 2: Brute-force substring match (fallback, with safeguards) ──
            // Strip noise sources before alphanumeric cleaning:
            //   - pacs message-type identifiers (pacs.009.001.08)
            //   - ISO timestamps (2026-03-23T09:00:28.898+00:00)
            //   - IBAN numbers (AT301810018388...)
            string sanitized = payload;
            sanitized = Regex.Replace(sanitized, @"pacs(?:\.[0-9]+)+", " ",
                                      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            sanitized = Regex.Replace(sanitized, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[.\d]*(?:[+-]\d{2}:\d{2})?", " ",
                                      RegexOptions.CultureInvariant);
            sanitized = Regex.Replace(sanitized, @"(?:IBAN>?)[A-Z]{2}\d{10,30}", " ",
                                      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

            string cleaned = Regex.Replace(sanitized, "[^a-zA-Z0-9]", string.Empty,
                                      RegexOptions.CultureInvariant);
            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            var upperPayload = cleaned.ToUpperInvariant();

            foreach (var kvp in snapshot)
            {
                // Skip OFFICIALREFs that are too short (< 8 chars) — high false-positive risk
                if (kvp.Key.Length < 8)
                    continue;

                // Skip purely numeric OFFICIALREFs — too ambiguous (match amounts, dates, etc.)
                if (Regex.IsMatch(kvp.Key, @"^\d+$"))
                    continue;

                if (upperPayload.Contains(kvp.Key))
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// Indique si le cache a déjà été initialisé.
        /// </summary>
        public static bool IsInitialised => Volatile.Read(ref _state) == 1;

        /// <summary>
        /// Réinitialise le cache. Thread-safe : les lecteurs en cours verront
        /// soit l'ancienne map, soit null (et retourneront null sans crash).
        /// Un appel ultérieur à Initialise() rechargera les données.
        /// </summary>
        public static void Clear()
        {
            // Ordre important : d'abord remettre _state à 0 pour permettre un futur Initialise(),
            // puis nullifier _lookup. Les lecteurs font un snapshot local donc pas de crash.
            Volatile.Write(ref _state, 0);
            _lookup = null;
        }
    }



    /// <summary>
    /// Résolveur de références DWINGS pour l'import Ambre
    /// </summary>
    public class DwingsReferenceResolver
    {
        // ── PERF: Compiled Regex (avoid pattern re-parse per call — called per row × 20K rows) ──
        private static readonly Regex RxNonAlnum = new Regex(@"[^A-Za-z0-9]", RegexOptions.Compiled);
        private static readonly Regex RxPacs = new Regex(@"pacs(?:\.[0-9]+)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ReconciliationService _reconciliationService;
        private IReadOnlyList<DwingsGuaranteeDto> _dwingsGuarantees;

        // ── PERF: Pre-built O(1) lookup (set once via SetInvoiceLookup, thread-safe for concurrent reads) ──
        private DwingsInvoiceLookup _invoiceLookup;

        // ── PERF: Pre-computed alphanumeric lookup caches (built once, avoid Regex.Replace per row) ──
        private Dictionary<string, string> _senderRefToBusinessCaseId; // senderRefAlnum → BUSINESS_CASE_ID
        private Dictionary<string, string> _officialRefToGuaranteeId;  // officialRefAlnum → GUARANTEE_ID (latest)
        private bool _invoiceSenderLookupBuilt;
        private bool _guaranteeLookupBuilt;

        public DwingsReferenceResolver(ReconciliationService reconciliationService)
        {
            _reconciliationService = reconciliationService;
        }

        /// <summary>
        /// Set the pre-built O(1) invoice lookup. Call once before bulk resolution.
        /// Thread-safe: the lookup is immutable after construction.
        /// </summary>
        public void SetInvoiceLookup(DwingsInvoiceLookup lookup) => _invoiceLookup = lookup;

        /// <summary>
        /// Eagerly build all lazy lookup dictionaries so they are safe for concurrent reads
        /// in Parallel.ForEach. Call once before bulk resolution.
        /// </summary>
        public void PreBuildLookups(IReadOnlyList<DwingsInvoiceDto> dwInvoices, IReadOnlyList<DwingsGuaranteeDto> dwGuarantees)
        {
            _dwingsGuarantees = dwGuarantees;
            // Force sender-reference lookup build
            if (!_invoiceSenderLookupBuilt && dwInvoices != null)
            {
                _senderRefToBusinessCaseId = new Dictionary<string, string>(dwInvoices.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var i in dwInvoices)
                {
                    if (string.IsNullOrWhiteSpace(i?.SENDER_REFERENCE) || string.IsNullOrWhiteSpace(i?.BUSINESS_CASE_ID)) continue;
                    var cleaned = RxNonAlnum.Replace(i.SENDER_REFERENCE, "");
                    if (!string.IsNullOrWhiteSpace(cleaned) && !_senderRefToBusinessCaseId.ContainsKey(cleaned))
                        _senderRefToBusinessCaseId[cleaned] = i.BUSINESS_CASE_ID;
                }
                _invoiceSenderLookupBuilt = true;
            }
            // Force guarantee officialref lookup build
            if (!_guaranteeLookupBuilt && dwGuarantees != null)
            {
                _officialRefToGuaranteeId = new Dictionary<string, string>(dwGuarantees.Count * 2, StringComparer.OrdinalIgnoreCase);
                var sorted = dwGuarantees
                    .Where(g => !string.IsNullOrWhiteSpace(g?.GUARANTEE_ID))
                    .OrderBy(g => g.GUARANTEE_ID, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var g in sorted)
                {
                    if (!string.IsNullOrWhiteSpace(g.OFFICIALREF))
                    {
                        var cleaned = RxNonAlnum.Replace(g.OFFICIALREF, "");
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            _officialRefToGuaranteeId[cleaned] = g.GUARANTEE_ID;
                    }
                    if (!string.IsNullOrWhiteSpace(g.PARTY_REF))
                    {
                        var cleaned = RxNonAlnum.Replace(g.PARTY_REF, "");
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            _officialRefToGuaranteeId[cleaned] = g.GUARANTEE_ID;
                    }
                }
                _guaranteeLookupBuilt = true;
            }
        }

        /// <summary>
        /// Résout les références DWINGS pour une ligne Ambre
        /// Thread-safe: no mutable instance state is touched during resolution.
        /// All lookups use pre-built immutable dictionaries.
        /// </summary>
        public Ambre.DwingsTokens ResolveReferences(
            DataAmbre dataAmbre,
            bool isPivot,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            IReadOnlyList<DwingsGuaranteeDto> dwGuarantees = null)
        {
            var references = new Ambre.DwingsTokens();
            // Local variable instead of instance state → thread-safe for parallel execution
            string resolvedGuaranteeId = null;

            try
            {
                var lookup = _invoiceLookup; // snapshot for thread safety

                // Extract tokens from various fields
                var tokens = ExtractTokens(dataAmbre);

                // Build primary BGI candidate depending on side
                string bgiCandidate;
                if (!isPivot)
                {
                    bgiCandidate = dataAmbre.Receivable_InvoiceFromAmbre?.Trim() 
                                   ?? DwingsLinkingHelper.ExtractBgiToken(dataAmbre.Reconciliation_Num);
                }
                else
                {
                    bgiCandidate = tokens.Bgi;
                }

                // Try to resolve an actual DW invoice via O(1) lookup
                DwingsInvoiceDto hit = null;
                if (!string.IsNullOrWhiteSpace(bgiCandidate))
                {
                    hit = lookup != null
                        ? DwingsLinkingHelper.ResolveInvoiceByBgi(lookup, bgiCandidate)
                        : DwingsLinkingHelper.ResolveInvoiceByBgi(dwInvoices, bgiCandidate);
                    if (hit != null && !VerifyAmountMatch(hit, dataAmbre.SignedAmount))
                        hit = null;
                }

                // BGPMT path
                if (hit == null && !string.IsNullOrWhiteSpace(tokens.Bgpmt))
                {
                    hit = lookup != null
                        ? DwingsLinkingHelper.ResolveInvoiceByBgpmt(lookup, tokens.Bgpmt)
                        : DwingsLinkingHelper.ResolveInvoiceByBgpmt(dwInvoices, tokens.Bgpmt);
                    if (hit != null && !VerifyAmountMatch(hit, dataAmbre.SignedAmount))
                        hit = null;
                }

                // Guarantee path
                if (hit == null && !string.IsNullOrWhiteSpace(tokens.GuaranteeId))
                {
                    var hits = lookup != null
                        ? DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                            lookup, tokens.GuaranteeId,
                            dataAmbre.Operation_Date ?? dataAmbre.Value_Date,
                            dataAmbre.SignedAmount, take: 1)
                        : DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                            dwInvoices, tokens.GuaranteeId,
                            dataAmbre.Operation_Date ?? dataAmbre.Value_Date,
                            dataAmbre.SignedAmount, take: 1);
                    hit = hits?.FirstOrDefault();
                }

                // Suggestions
                if (hit == null)
                {
                    var suggested = GetSuggestedInvoice(dataAmbre, tokens, dwInvoices, isPivot, lookup);
                    if (!string.IsNullOrWhiteSpace(suggested))
                        hit = lookup?.FindByInvoiceId(suggested)
                              ?? dwInvoices?.FirstOrDefault(i => string.Equals(i?.INVOICE_ID, suggested, StringComparison.OrdinalIgnoreCase));
                }

                // OfficialRef path – last resort (returns both invoiceId and guaranteeId)
                if (hit == null)
                {
                    var (officialInvoiceId, officialGuaranteeId) = ResolveByOfficialRef(dataAmbre, dwInvoices, lookup);
                    resolvedGuaranteeId = officialGuaranteeId;
                    if (!string.IsNullOrWhiteSpace(officialInvoiceId))
                    {
                        hit = lookup?.FindByInvoiceId(officialInvoiceId)
                              ?? dwInvoices?.FirstOrDefault(i => string.Equals(i?.INVOICE_ID, officialInvoiceId, StringComparison.OrdinalIgnoreCase));
                    }
                }

                // Fill references
                references.InvoiceId = hit?.INVOICE_ID;
                references.CommissionId = !string.IsNullOrWhiteSpace(tokens.Bgpmt) ? tokens.Bgpmt : hit?.BGPMT;
                references.GuaranteeId = resolvedGuaranteeId
                                        ?? tokens.GuaranteeId
                                        ?? hit?.BUSINESS_CASE_REFERENCE
                                        ?? hit?.BUSINESS_CASE_ID;
            }
            catch (Exception ex)
            {
                LogManager.Warning($"DWINGS resolution failed for {dataAmbre?.ID}: {ex.Message}");
            }

            return references;
        }

        /// <summary>Verify amount matches (tolerance 0.01) — extracted to avoid duplication.</summary>
        private static bool VerifyAmountMatch(DwingsInvoiceDto hit, decimal ambreAmt)
        {
            var absAmbre = Math.Abs(ambreAmt);
            bool reqMatch = hit.REQUESTED_AMOUNT.HasValue && DwingsLinkingHelper.AmountMatches(absAmbre, Math.Abs(hit.REQUESTED_AMOUNT.Value), tolerance: 0.01m);
            bool billMatch = hit.BILLING_AMOUNT.HasValue && DwingsLinkingHelper.AmountMatches(absAmbre, Math.Abs(hit.BILLING_AMOUNT.Value), tolerance: 0.01m);
            return reqMatch || billMatch;
        }

        /// <summary>
        /// Obtient la méthode de paiement pour une référence DWINGS
        /// </summary>
        public async Task<string> GetPaymentMethodAsync(DataAmbre dataAmbre, string countryId)
        {
            var dwRef = ExtractDwingsReference(dataAmbre);
            if (dwRef == null) return null;

            return await GetPaymentMethodFromDwingsAsync(countryId, dwRef.Type, dwRef.Code);
        }

        private DwingsTokens ExtractTokens(DataAmbre dataAmbre)
        {
            // EXTENDED: Match ReconciliationViewEnricher heuristics for consistency
            return new DwingsTokens
            {
                // BGPMT: check Reconciliation_Num, ReconciliationOrigin_Num, RawLabel
                Bgpmt = DwingsLinkingHelper.ExtractBgpmtToken(dataAmbre.Reconciliation_Num)
                        ?? DwingsLinkingHelper.ExtractBgpmtToken(dataAmbre.ReconciliationOrigin_Num)
                        ?? DwingsLinkingHelper.ExtractBgpmtToken(dataAmbre.RawLabel),
                        
                // GuaranteeId: check Reconciliation_Num, RawLabel, Receivable_DWRefFromAmbre
                GuaranteeId = DwingsLinkingHelper.ExtractGuaranteeId(dataAmbre.Reconciliation_Num)
                              ?? DwingsLinkingHelper.ExtractGuaranteeId(dataAmbre.RawLabel)
                              ?? DwingsLinkingHelper.ExtractGuaranteeId(dataAmbre.Receivable_DWRefFromAmbre),
                              
                // BGI: check RawLabel, Reconciliation_Num, ReconciliationOrigin_Num, Receivable_DWRefFromAmbre
                Bgi = DwingsLinkingHelper.ExtractBgiToken(dataAmbre.RawLabel)
                      ?? DwingsLinkingHelper.ExtractBgiToken(dataAmbre.Reconciliation_Num)
                      ?? DwingsLinkingHelper.ExtractBgiToken(dataAmbre.ReconciliationOrigin_Num)
                      ?? DwingsLinkingHelper.ExtractBgiToken(dataAmbre.Receivable_DWRefFromAmbre)
            };
        }

        /// <summary>
        /// STEP 1: Find GUARANTEE_ID via OFFICIALREF/SENDER_REFERENCE/GUARANTEE_ID pattern matching
        /// STEP 2: Find best invoice linked to this guarantee (with amount matching)
        /// </summary>
        private (string invoiceId, string guaranteeId) ResolveByOfficialRef(
            DataAmbre dataAmbre,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            DwingsInvoiceLookup lookup)
        {
            if ((dwInvoices == null || dwInvoices.Count == 0) && lookup == null) return (null, null);
            if (dataAmbre == null) return (null, null);

            var guaranteeId = FindGuaranteeIdFromReferences(dataAmbre, dwInvoices);
            if (string.IsNullOrWhiteSpace(guaranteeId)) return (null, null);

            var invoices = lookup != null
                ? DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                    lookup, guaranteeId,
                    dataAmbre.Operation_Date ?? dataAmbre.Value_Date,
                    dataAmbre.SignedAmount, take: 5)
                : DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                    dwInvoices, guaranteeId,
                    dataAmbre.Operation_Date ?? dataAmbre.Value_Date,
                    dataAmbre.SignedAmount, take: 5);

            var bestInvoiceId = invoices?.FirstOrDefault()?.INVOICE_ID;
            return (bestInvoiceId, guaranteeId);
        }

        /// <summary>
        /// STEP 1: Find GUARANTEE_ID via multiple matching strategies
        /// Tries in order: SENDER_REFERENCE → OFFICIALREF → PARTY_REF
        /// All use same alphanumeric cleaning logic for consistent matching
        /// </summary>
        private string FindGuaranteeIdFromReferences(DataAmbre dataAmbre, IReadOnlyList<DwingsInvoiceDto> dwInvoices)
        {
            // Extract alphanumeric token from Reconciliation_Num
            var token = ExtractAlphanumericToken(dataAmbre.Reconciliation_Num);
            if (string.IsNullOrWhiteSpace(token))
            {
                // Fallback to ReconciliationOrigin_Num
                token = ExtractAlphanumericToken(dataAmbre.ReconciliationOrigin_Num);
            }
            if (string.IsNullOrWhiteSpace(token)) return null;

            var ambreAmt = dataAmbre.SignedAmount;

            // Strategy 1: Match via invoice SENDER_REFERENCE (must have BUSINESS_CASE_ID + amount match)
            var guaranteeFromSender = FindGuaranteeViaSenderReference(token, ambreAmt, dwInvoices);
            if (!string.IsNullOrWhiteSpace(guaranteeFromSender))
                return guaranteeFromSender;

            // Strategy 2: Match via guarantee OFFICIALREF or PARTY_REF
            var guaranteeFromOfficial = FindGuaranteeViaOfficialRef(token);
            if (!string.IsNullOrWhiteSpace(guaranteeFromOfficial))
                return guaranteeFromOfficial;

            return null;
        }

        /// <summary>
        /// Extract single alphanumeric token from string (removes all non-alphanumeric chars)
        /// Example: "ABC-123-456" => "ABC123456"
        /// </summary>
        private string ExtractAlphanumericToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Remove pacs message-type identifiers (e.g. pacs.009.001.08, pacs.008.001.08)
            // to avoid their numeric fragments being mistaken for an OfficialRef.
            var noPacs = RxPacs.Replace(s, " ");
            var cleaned = RxNonAlnum.Replace(noPacs, "");
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 3 || cleaned.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;
            return cleaned;
        }

        /// <summary>
        /// Find GUARANTEE_ID via invoice SENDER_REFERENCE matching
        /// Requirements: SENDER_REFERENCE matches token + BUSINESS_CASE_ID exists
        /// PERF: Uses pre-built dictionary (O(1) instead of O(n) linear scan with Regex per invoice)
        /// </summary>
        private string FindGuaranteeViaSenderReference(string token, decimal ambreAmt, IReadOnlyList<DwingsInvoiceDto> dwInvoices)
        {
            // Build lookup on first use (once per import)
            if (!_invoiceSenderLookupBuilt && dwInvoices != null)
            {
                _senderRefToBusinessCaseId = new Dictionary<string, string>(dwInvoices.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var i in dwInvoices)
                {
                    if (string.IsNullOrWhiteSpace(i?.SENDER_REFERENCE) || string.IsNullOrWhiteSpace(i?.BUSINESS_CASE_ID)) continue;
                    var cleaned = RxNonAlnum.Replace(i.SENDER_REFERENCE, "");
                    if (!string.IsNullOrWhiteSpace(cleaned) && !_senderRefToBusinessCaseId.ContainsKey(cleaned))
                        _senderRefToBusinessCaseId[cleaned] = i.BUSINESS_CASE_ID;
                }
                _invoiceSenderLookupBuilt = true;
            }

            if (_senderRefToBusinessCaseId == null) return null;
            // token is already alphanumeric (from ExtractAlphanumericToken), no need to clean again
            _senderRefToBusinessCaseId.TryGetValue(token, out var result);
            return result;
        }

        /// <summary>
        /// Find GUARANTEE_ID via guarantee OFFICIALREF or PARTY_REF matching
        /// Handles duplicates (recreations) by taking latest GUARANTEE_ID (descending sort)
        /// PERF: Uses pre-built dictionary (O(1) instead of O(n) linear scan with Regex per guarantee)
        /// </summary>
        private string FindGuaranteeViaOfficialRef(string token)
        {
            if (_officialRefToGuaranteeId == null) return null;
            // token is already alphanumeric (from ExtractAlphanumericToken), no need to clean again
            _officialRefToGuaranteeId.TryGetValue(token, out var result);
            return result;
        }

        private string GetSuggestedInvoice(
            DataAmbre dataAmbre,
            DwingsTokens tokens,
            IReadOnlyList<DwingsInvoiceDto> dwInvoices,
            bool isPivot,
            DwingsInvoiceLookup lookup = null)
        {
            // For receivable: ONLY use Reconciliation_Num
            // For pivot: use fallback chain (Rec_Num -> RecOrigin_Num -> RawLabel)
            string bgiOrdered;
            if (!isPivot)
            {
                bgiOrdered = DwingsLinkingHelper.ExtractBgiToken(dataAmbre.Reconciliation_Num);
            }
            else
            {
                bgiOrdered = DwingsLinkingHelper.ExtractBgiToken(dataAmbre.Reconciliation_Num)
                            ?? DwingsLinkingHelper.ExtractBgiToken(dataAmbre.ReconciliationOrigin_Num)
                            ?? DwingsLinkingHelper.ExtractBgiToken(dataAmbre.RawLabel);
            }

            // Use O(1) lookup overload when available
            var suggestions = lookup != null
                ? DwingsLinkingHelper.SuggestInvoicesForAmbre(
                    lookup,
                    rawLabel: dataAmbre.RawLabel,
                    reconciliationNum: dataAmbre.Reconciliation_Num,
                    reconciliationOriginNum: dataAmbre.ReconciliationOrigin_Num,
                    explicitBgi: dataAmbre.Receivable_InvoiceFromAmbre?.Trim() ?? bgiOrdered,
                    guaranteeId: tokens.GuaranteeId,
                    ambreDate: dataAmbre.Operation_Date ?? dataAmbre.Value_Date,
                    ambreAmount: dataAmbre.SignedAmount,
                    take: 1)
                : DwingsLinkingHelper.SuggestInvoicesForAmbre(
                    dwInvoices,
                    rawLabel: dataAmbre.RawLabel,
                    reconciliationNum: dataAmbre.Reconciliation_Num,
                    reconciliationOriginNum: dataAmbre.ReconciliationOrigin_Num,
                    explicitBgi: dataAmbre.Receivable_InvoiceFromAmbre?.Trim() ?? bgiOrdered,
                    guaranteeId: tokens.GuaranteeId,
                    ambreDate: dataAmbre.Operation_Date ?? dataAmbre.Value_Date,
                    ambreAmount: dataAmbre.SignedAmount,
                    take: 1);
                
            return suggestions?.FirstOrDefault()?.INVOICE_ID;
        }

        private DwingsRef ExtractDwingsReference(DataAmbre ambre)
        {
            if (ambre == null) return null;
            
            // Try BGPMT first
            string bgpmt = DwingsLinkingHelper.ExtractBgpmtToken(ambre.RawLabel) 
                          ?? DwingsLinkingHelper.ExtractBgpmtToken(ambre.Reconciliation_Num);
            if (!string.IsNullOrWhiteSpace(bgpmt))
                return new DwingsRef { Type = "BGPMT", Code = bgpmt };

            // Try BGI
            string bgi = DwingsLinkingHelper.ExtractBgiToken(ambre.RawLabel) 
                        ?? DwingsLinkingHelper.ExtractBgiToken(ambre.Reconciliation_Num);
            if (!string.IsNullOrWhiteSpace(bgi))
                return new DwingsRef { Type = "BGI", Code = bgi };

            return null;
        }

        private async Task<string> GetPaymentMethodFromDwingsAsync(
            string countryId,
            string refType,
            string refCode)
        {
            if (string.IsNullOrWhiteSpace(refType) || string.IsNullOrWhiteSpace(refCode))
                return null;

            var invoices = await _reconciliationService?.GetDwingsInvoicesAsync();
            if (invoices == null || invoices.Count == 0) 
                return null;

            var code = refCode?.Trim();
            if (string.IsNullOrEmpty(code)) 
                return null;

            DwingsInvoiceDto hit = null;
            
            if (string.Equals(refType, "BGPMT", StringComparison.OrdinalIgnoreCase))
            {
                hit = invoices.FirstOrDefault(i => 
                    !string.IsNullOrWhiteSpace(i?.BGPMT) &&
                    string.Equals(i.BGPMT, code, StringComparison.OrdinalIgnoreCase));
            }
            else // BGI
            {
                hit = FindInvoiceByBgi(invoices, code);
            }

            return hit?.PAYMENT_METHOD;
        }

        private DwingsInvoiceDto FindInvoiceByBgi(IReadOnlyList<DwingsInvoiceDto> invoices, string code)
        {
            return invoices.FirstOrDefault(i =>
                MatchesField(i.INVOICE_ID, code) ||
                MatchesField(i.SENDER_REFERENCE, code) ||
                MatchesField(i.RECEIVER_REFERENCE, code) ||
                MatchesField(i.BUSINESS_CASE_REFERENCE, code));
        }

        private bool MatchesField(string field, string value)
        {
            return field != null && 
                   string.Equals(field, value, StringComparison.OrdinalIgnoreCase);
        }

        private class DwingsRef
        {
            public string Type { get; set; } // "BGPMT" or "BGI"
            public string Code { get; set; }
        }

        private class DwingsTokens
        {
            public string Bgpmt { get; set; }
            public string GuaranteeId { get; set; }
            public string Bgi { get; set; }
        }
    }

    /// <summary>
    /// Références DWINGS résolues
    /// </summary>
    public class DwingsTokens
    {
        public string InvoiceId { get; set; }
        public string CommissionId { get; set; }
        public string GuaranteeId { get; set; }
    }
}