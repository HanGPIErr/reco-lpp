using OfflineFirstAccess.ChangeTracking;
using OfflineFirstAccess.Helpers;
using RecoTool.Models;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RecoTool.Helpers;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Services.DTOs;
using RecoTool.Domain.Filters;
using RecoTool.Services.Queries;
using RecoTool.Services.Rules;
using RecoTool.Services.Helpers;
using RecoTool.Infrastructure.Logging;
using RecoTool.Services.Cache;

namespace RecoTool.Services
{
    /// <summary>
    /// Service principal de réconciliation
    /// Gère les opérations de réconciliation, règles automatiques, Actions/KPI
    /// </summary>
    public partial class ReconciliationService
    {
        private readonly string _connectionString;
        private readonly string _currentUser;
        private readonly Dictionary<string, Country> _countries;
        private readonly OfflineFirstService _offlineFirstService;
        private readonly Infrastructure.DataAccess.OleDbQueryExecutor _queryExecutor;
        private DwingsService _dwingsService;
        private RulesEngine _rulesEngine;
        private Rules.RuleProposalRepository _proposalRepository;

        /// <summary>
        /// Lazy-initialized repository for T_RuleProposals. Safe to call repeatedly.
        /// Creates the table on first access if it does not exist yet.
        /// </summary>
        public Rules.RuleProposalRepository ProposalRepository
        {
            get
            {
                if (_proposalRepository == null && !string.IsNullOrWhiteSpace(_connectionString))
                {
                    try
                    {
                        _proposalRepository = new Rules.RuleProposalRepository(_connectionString);
                        // Fire-and-forget table creation
                        _ = _proposalRepository.EnsureTableAsync();
                    }
                    catch { _proposalRepository = null; }
                }
                return _proposalRepository;
            }
        }

        private (string dwEsc, string ambreEsc) GetEscapedPaths(string countryId)
        {
            string dwPath = _offlineFirstService?.GetLocalDWDatabasePath();
            string ambrePath = _offlineFirstService?.GetLocalAmbreDatabasePath(countryId);
            string dwEsc = string.IsNullOrEmpty(dwPath) ? null : dwPath.Replace("'", "''");
            string ambreEsc = string.IsNullOrEmpty(ambrePath) ? null : ambrePath.Replace("'", "''");
            return (dwEsc, ambreEsc);
        }

        // Events (RuleApplied + RuleAppliedEventArgs + RaiseRuleApplied) live in ReconciliationService.Events.cs.
        // GetCurrencySumsAsync / CurrencySumRow remain in this file for now; they belong with Data Retrieval
        // and will move when that region is extracted.

        /// <summary>
        /// Returns total absolute amounts by currency for Live rows matching the provided backend filter.
        /// Uses the same base query as GetReconciliationCountAsync and groups by CCY.
        /// OPTIMIZED: Cached based on countryId + filterSql (AMBRE data rarely changes)
        /// </summary>
        public async Task<Dictionary<string, double>> GetCurrencySumsAsync(string countryId, string filterSql = null)
        {
            var cacheKey = $"CurrencySums_{countryId}_{NormalizeFilterForCache(filterSql)}";
            return await CacheService.Instance.GetOrLoadAsync(cacheKey, async () =>
            {
                try
                {
                    var (dwEsc, ambreEsc) = GetEscapedPaths(countryId);
                    string query = ReconciliationViewQueryBuilder.Build(dwEsc, ambreEsc, filterSql);
                    query += " AND a.DeleteDate IS NULL AND (r.DeleteDate IS NULL)";

                    var predicate = FilterSqlHelper.ExtractNormalizedPredicate(filterSql);
                    if (!string.IsNullOrEmpty(predicate))
                        query += $" AND ({predicate})";

                    var sumsSql = $"SELECT CCY, SUM(ABS(SignedAmount)) AS Amount FROM ({query}) AS q GROUP BY CCY";
                    var rows = await _queryExecutor.QueryAsync<CurrencySumRow>(sumsSql).ConfigureAwait(false);

                    var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in rows)
                    {
                        if (string.IsNullOrWhiteSpace(row.CCY)) continue;
                        if (result.ContainsKey(row.CCY)) result[row.CCY] += row.Amount; else result[row.CCY] = row.Amount;
                    }
                    return result;
                }
                catch
                {
                    return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                }
            }, TimeSpan.FromHours(24)).ConfigureAwait(false);
        }

        private class CurrencySumRow { public string CCY { get; set; } public double Amount { get; set; } }

        public ReconciliationService(string connectionString, string currentUser, IEnumerable<Country> countries)
        {
            _connectionString = connectionString;
            _currentUser = currentUser;
            _countries = countries?.ToDictionary(c => c.CNT_Id, c => c) ?? new Dictionary<string, Country>();
            if (!string.IsNullOrWhiteSpace(connectionString))
                _queryExecutor = new Infrastructure.DataAccess.OleDbQueryExecutor(connectionString);
        }

        // Expose for infrastructure wiring (e.g., exports). Keep read-only.
        public string MainConnectionString => _connectionString;

        

        /// <summary>
        /// Returns the last known AMBRE operation date in the current dataset for the country.
        /// Used as the snapshot date for the pre-import KPI snapshot.
        /// </summary>
        public async Task<DateTime?> GetLastAmbreOperationDateAsync(string countryId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(countryId)) return null;

            // Use the AMBRE database for this country
            var ambrePath = _offlineFirstService?.GetLocalAmbreDatabasePath(countryId);
            if (string.IsNullOrWhiteSpace(ambrePath) || !File.Exists(ambrePath)) return null;
            var ambreCs = _offlineFirstService?.GetAmbreConnectionString(countryId);

            using (var connection = new OleDbConnection(ambreCs))
            {
                await connection.OpenAsync(cancellationToken);
                var cmd = new OleDbCommand("SELECT MAX(Operation_Date) FROM T_Data_Ambre", connection);
                var obj = await cmd.ExecuteScalarAsync(cancellationToken);
                if (obj != null && obj != DBNull.Value)
                {
                    try { return Convert.ToDateTime(obj).Date; } catch { return null; }
                }
            }
            return null;
        }

        public ReconciliationService(string connectionString, string currentUser, IEnumerable<Country> countries, OfflineFirstService offlineFirstService)
            : this(connectionString, currentUser, countries)
        {
            _offlineFirstService = offlineFirstService;
            try { _rulesEngine = new RulesEngine(_offlineFirstService); } catch { _rulesEngine = null; }
        }

        public string CurrentUser => _currentUser;

        #region Data Retrieval

        // Simple in-memory DWINGS caches for UI assistance searches (DTOs moved to Services/DTOs)

        // DWINGS access delegated to DwingsService

        // Cache for reconciliation view queries (task coalescing)
        private static readonly ConcurrentDictionary<string, Lazy<Task<List<ReconciliationViewData>>>> _recoViewCache
            = new ConcurrentDictionary<string, Lazy<Task<List<ReconciliationViewData>>>>();

        // Materialized data cache to allow incremental updates after saves without reloading
        private static readonly ConcurrentDictionary<string, List<ReconciliationViewData>> _recoViewDataCache
            = new ConcurrentDictionary<string, List<ReconciliationViewData>>();

        /// <summary>
        /// Clears all reconciliation view caches (both task and materialized data caches).
        /// Call after external mutations (e.g., pull from network) to force a reload on next request.
        /// OPTIMIZATION: Also clears CacheService entries for StatusCounts, RecoCount, and CurrencySums
        /// </summary>
        public static void InvalidateReconciliationViewCache()
        {
            try
            {
                _recoViewDataCache.Clear();
            }
            catch { }
            try
            {
                foreach (var key in _recoViewCache.Keys)
                {
                    _recoViewCache.TryRemove(key, out _);
                }
            }
            catch { }
            
            // OPTIMIZATION: Invalidate CacheService entries for counts and sums
            try
            {
                CacheService.Instance.InvalidateByPrefix("StatusCounts_");
                CacheService.Instance.InvalidateByPrefix("RecoCount_");
                CacheService.Instance.InvalidateByPrefix("CurrencySums_");
            }
            catch { }
        }

        /// <summary>
        /// Clears reconciliation view caches for a specific country by prefix match on the cache key.
        /// Key format is "{countryId}|{dashboardOnly}|{normalizedFilter}".
        /// OPTIMIZATION: Also clears CacheService entries for StatusCounts, RecoCount, and CurrencySums for this country
        /// </summary>
        public static void InvalidateReconciliationViewCache(string countryId)
        {
            if (string.IsNullOrWhiteSpace(countryId)) { InvalidateReconciliationViewCache(); return; }
            try
            {
                var prefix = countryId + "|";
                foreach (var kv in _recoViewDataCache.ToArray())
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        _recoViewDataCache.TryRemove(kv.Key, out _);
                }
            }
            catch { }
            try
            {
                var prefix = countryId + "|";
                foreach (var kv in _recoViewCache.ToArray())
                {
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        _recoViewCache.TryRemove(kv.Key, out _);
                }
            }
            catch { }
            
            // OPTIMIZATION: Invalidate CacheService entries for this country
            try
            {
                CacheService.Instance.InvalidateByPrefix($"StatusCounts_{countryId}_");
                CacheService.Instance.InvalidateByPrefix($"RecoCount_{countryId}_");
                CacheService.Instance.InvalidateByPrefix($"CurrencySums_{countryId}_");

                // CRITICAL: Also invalidate DWINGS data caches for this country
                CacheService.Instance.Invalidate($"DWINGS_Invoices_{countryId}");
                CacheService.Instance.Invalidate($"DWINGS_Guarantees_{countryId}");
            }
            catch { }
        }

        

        // Shared loader used by the shared cache
      
        

        // ──────────────────────────────────────────────────────────────────────────────────────
        // DWINGS simulation override. When set (via ApplyDwingsOverride), both getters short-circuit
        // the country cache and return the override lists. AsyncLocal flows through awaits and is
        // per-logical-call, so concurrent simulations on different threads remain isolated and the
        // permanent CacheService.Instance is never polluted. Always scoped via `using { }`.
        // ──────────────────────────────────────────────────────────────────────────────────────
        public sealed class DwingsOverride
        {
            public IReadOnlyList<DwingsInvoiceDto> Invoices { get; set; }
            public IReadOnlyList<DwingsGuaranteeDto> Guarantees { get; set; }
        }

        private static readonly System.Threading.AsyncLocal<DwingsOverride> _dwingsOverride
            = new System.Threading.AsyncLocal<DwingsOverride>();

        private sealed class DwingsOverrideScope : IDisposable
        {
            private readonly DwingsOverride _previous;
            public DwingsOverrideScope(DwingsOverride replacement)
            {
                _previous = _dwingsOverride.Value;
                _dwingsOverride.Value = replacement;
            }
            public void Dispose() { _dwingsOverride.Value = _previous; }
        }

        /// <summary>
        /// Installs a DWINGS override for the current async logical call (simulation what-if).
        /// Returned scope restores the previous override on <see cref="IDisposable.Dispose"/>;
        /// the caller MUST wrap in <c>using</c> to guarantee the restore.
        /// </summary>
        public IDisposable ApplyDwingsOverride(DwingsOverride overrideData)
        {
            return new DwingsOverrideScope(overrideData);
        }

        public async Task<IReadOnlyList<DwingsInvoiceDto>> GetDwingsInvoicesAsync()
        {
            // Simulation bypass: user-provided snapshot, NOT cached anywhere.
            var ovr = _dwingsOverride.Value;
            if (ovr?.Invoices != null) return ovr.Invoices;

            // OPTIMIZATION: Cache DWINGS invoices permanently per country (never expires)
            var cacheKey = $"DWINGS_Invoices_{_offlineFirstService?.CurrentCountryId}";
            return await CacheService.Instance.GetOrLoadAsync(cacheKey, async () =>
            {
                if (_dwingsService == null) _dwingsService = new DwingsService(_offlineFirstService);
                return await _dwingsService.GetInvoicesAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<DwingsGuaranteeDto>> GetDwingsGuaranteesAsync()
        {
            // Simulation bypass: user-provided snapshot, NOT cached anywhere.
            var ovr = _dwingsOverride.Value;
            if (ovr?.Guarantees != null) return ovr.Guarantees;

            // OPTIMIZATION: Cache DWINGS guarantees permanently per country (never expires)
            var cacheKey = $"DWINGS_Guarantees_{_offlineFirstService?.CurrentCountryId}";
            return await CacheService.Instance.GetOrLoadAsync(cacheKey, async () =>
            {
                if (_dwingsService == null) _dwingsService = new DwingsService(_offlineFirstService);
                return await _dwingsService.GetGuaranteesAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Récupère toutes les données Ambre pour un pays
        /// </summary>
        public async Task<List<DataAmbre>> GetAmbreDataAsync(string countryId, bool includeDeleted = false)
        {
            // Ambre est désormais dans une base séparée par pays
            var ambrePath = _offlineFirstService?.GetLocalAmbreDatabasePath(countryId);
            if (string.IsNullOrWhiteSpace(ambrePath))
                throw new InvalidOperationException("Chemin de la base AMBRE introuvable pour le pays courant.");
            var ambreCs = _offlineFirstService?.GetAmbreConnectionString(countryId);

            var query = @"SELECT * FROM T_Data_Ambre";
            if (!includeDeleted)
                query += " WHERE DeleteDate IS NULL";
            query += " ORDER BY Operation_Date ASC";

            return await _queryExecutor.QueryAsync<DataAmbre>(query, ambreCs);
        }

        /// <summary>
        /// Récupère uniquement les réconciliations dont l'action est TRIGGER (non supprimées)
        /// </summary>
        public async Task<List<Reconciliation>> GetTriggerReconciliationsAsync(string countryId)
        {
            // Jointure sur AMBRE identique pour respecter la portée pays, mais seules les colonnes r.* sont nécessaires
            string ambrePath = _offlineFirstService?.GetLocalAmbreDatabasePath(countryId);
            string ambreEsc = string.IsNullOrEmpty(ambrePath) ? null : ambrePath.Replace("'", "''");
            string ambreJoin = string.IsNullOrEmpty(ambreEsc) ? "T_Data_Ambre AS a" : $"(SELECT * FROM [{ambreEsc}].T_Data_Ambre) AS a";

            // Filter by Action = Trigger and only RECEIVABLE account side
            var country = _countries.ContainsKey(countryId) ? _countries[countryId] : null;
            var receivableId = country?.CNT_AmbreReceivable;

            var query = $@"SELECT r.* FROM (T_Reconciliation AS r
                             INNER JOIN {ambreJoin} ON r.ID = a.ID)
                           WHERE r.DeleteDate IS NULL AND r.Action = ? AND a.Account_ID = ?
                           ORDER BY r.LastModified DESC";

            return await _queryExecutor.QueryAsync<Reconciliation>(query, _connectionString, (int)ActionType.Trigger, receivableId);
        }

        // JSON filter preset is defined in Domain/Filters/FilterPreset

        /// <summary>
        /// Clears the reconciliation view cache to force fresh data reload
        /// </summary>
        public void ClearViewCache()
        {
            try
            {
                // CRITICAL: Clear BOTH cache layers
                _recoViewDataCache.Clear();  // materialized data
                _recoViewCache.Clear();      // Lazy<Task> coalescing cache
                
                // Also clear CacheService entries for counts/sums
                var countryId = _offlineFirstService?.CurrentCountryId;
                if (!string.IsNullOrWhiteSpace(countryId))
                {
                    Cache.CacheService.Instance.InvalidateByPrefix($"StatusCounts_{countryId}");
                    Cache.CacheService.Instance.InvalidateByPrefix($"RecoCount_{countryId}");
                    Cache.CacheService.Instance.InvalidateByPrefix($"CurrencySums_{countryId}");
                }

                System.Diagnostics.Debug.WriteLine("ReconciliationService: View cache cleared (both layers)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReconciliationService: Error clearing cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Récupère les données jointes Ambre + Réconciliation (Live only - DeleteDate IS NULL)
        /// </summary>
        public async Task<List<ReconciliationViewData>> GetReconciliationViewAsync(string countryId, string filterSql = null)
        {
            return await GetReconciliationViewAsync(countryId, filterSql, includeDeleted: false).ConfigureAwait(false);
        }
        
        public List<ReconciliationViewData> TryGetCachedReconciliationView(string countryId, string filterSql, bool includeDeleted = false)
        {
            var key = $"{countryId ?? string.Empty}|{includeDeleted}|{NormalizeFilterForCache(filterSql)}";
            if (_recoViewDataCache.TryGetValue(key, out var list)) return list;
            return null;
        }
        
        /// <summary>
        /// Récupère les données jointes Ambre + Réconciliation avec option d'inclure les lignes supprimées
        /// Used by HomePage for historical charts (Deletion Delay, New vs Deleted Daily)
        /// </summary>
        public async Task<List<ReconciliationViewData>> GetReconciliationViewAsync(string countryId, string filterSql, bool includeDeleted)
        {
            // CRITICAL: Always ensure DWINGS caches are initialized for lazy loading
            await EnsureDwingsCachesInitializedAsync().ConfigureAwait(false);
            
            var key = $"{countryId ?? string.Empty}|{includeDeleted}|{NormalizeFilterForCache(filterSql)}";
            if (_recoViewCache.TryGetValue(key, out var existing))
            {
                var cached = await existing.Value.ConfigureAwait(false);
                
                // CRITICAL: Re-apply enrichments for cached data (linking may have been lost)
                // This ensures DWINGS_InvoiceID, MissingAmount, etc. are always calculated
                await ReapplyEnrichmentsAsync(cached, countryId).ConfigureAwait(false);
                
                return cached;
            }
            var lazy = new Lazy<Task<List<ReconciliationViewData>>>(() => BuildReconciliationViewAsyncCore(countryId, filterSql, includeDeleted, key));
            var entry = _recoViewCache.GetOrAdd(key, lazy);
            var result = await entry.Value.ConfigureAwait(false);
            return result;
        }
        
        /// <summary>
        /// Re-applies critical enrichments to a list of ReconciliationViewData
        /// Needed because DWINGS caches may have been cleared between cache creation and retrieval
        /// Also used for preloaded data to ensure enrichments are always applied
        /// </summary>
        public async Task ReapplyEnrichmentsToListAsync(List<ReconciliationViewData> list, string countryId)
        {
            await ReapplyEnrichmentsAsync(list, countryId).ConfigureAwait(false);
        }
        
        /// <summary>
        /// Re-applies critical enrichments to cached data (internal implementation)
        /// Full DWINGS property enrichment is skipped if already done (sampling first row).
        /// Unlinked Receivable BGI retry and MissingAmount recalculation ALWAYS run,
        /// because DWINGS data may have become available since last enrichment.
        /// </summary>
        private async Task ReapplyEnrichmentsAsync(List<ReconciliationViewData> list, string countryId)
        {
            if (list == null || list.Count == 0) return;

            await EnsureDwingsCachesInitializedAsync().ConfigureAwait(false);

            bool alreadyEnriched = list.Count > 0 && !string.IsNullOrWhiteSpace(list[0].I_RECEIVER_NAME);

            try
            {
                var invoices = await GetDwingsInvoicesAsync().ConfigureAwait(false);

                if (!alreadyEnriched)
                {
                    var guarantees = await GetDwingsGuaranteesAsync().ConfigureAwait(false);
                    ReconciliationViewEnricher.EnrichWithDwingsInvoices(list, invoices);
                    EnrichRowsWithDwingsProperties(list, invoices, guarantees);
                }

                // ALWAYS retry linking for Receivable rows whose BGI was not found previously
                // (DWINGS file may have been updated since last enrichment)
                ReconciliationViewEnricher.RetryUnlinkedReceivableBgi(list, invoices);

                // ALWAYS recalculate MissingAmounts (grouping may have changed due to new links or basket linking)
                var country = _countries?.TryGetValue(countryId, out var c) == true ? c : null;
                if (country != null)
                    ReconciliationViewEnricher.CalculateMissingAmounts(list, country.CNT_AmbreReceivable, country.CNT_AmbrePivot);
            }
            catch { /* best-effort */ }
        }
        
        /// <summary>
        /// Builds DWINGS lookup dictionaries once and populates I_* and G_* properties on each row.
        /// Shared between BuildReconciliationViewAsyncCore and ReapplyEnrichmentsAsync to avoid duplication.
        /// </summary>
        private static void EnrichRowsWithDwingsProperties(
            List<ReconciliationViewData> list,
            IReadOnlyList<DwingsInvoiceDto> invoices,
            IReadOnlyList<DwingsGuaranteeDto> guarantees)
        {
            if (list == null || list.Count == 0) return;

            var invoiceDict = invoices?
                .Where(i => !string.IsNullOrWhiteSpace(i.INVOICE_ID))
                .GroupBy(i => i.INVOICE_ID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, DwingsInvoiceDto>();

            var guaranteeDict = guarantees?
                .Where(g => !string.IsNullOrWhiteSpace(g.GUARANTEE_ID))
                .GroupBy(g => g.GUARANTEE_ID, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, DwingsGuaranteeDto>();

            foreach (var row in list)
            {
                if (!string.IsNullOrWhiteSpace(row.DWINGS_InvoiceID) && invoiceDict.TryGetValue(row.DWINGS_InvoiceID, out var invoice))
                    row.PopulateInvoiceProperties(invoice);

                if (!string.IsNullOrWhiteSpace(row.DWINGS_GuaranteeID) && guaranteeDict.TryGetValue(row.DWINGS_GuaranteeID, out var guarantee))
                    row.PopulateGuaranteeProperties(guarantee);
            }
        }

        private bool _dwingsCachesInitialized = false;
        private readonly object _dwingsCacheLock = new object();
        
        /// <summary>
        /// Ensures DWINGS caches are initialized once per country session
        /// Called before returning cached data to guarantee lazy loading works
        /// Public to allow ReconciliationView to initialize caches for preloaded data
        /// </summary>
        public async Task EnsureDwingsCachesInitializedAsync()
        {
            if (_dwingsCachesInitialized) return;
            
            lock (_dwingsCacheLock)
            {
                if (_dwingsCachesInitialized) return;
                _dwingsCachesInitialized = true;
            }
            
            try
            {
                var invoices = await GetDwingsInvoicesAsync().ConfigureAwait(false);
                var guarantees = await GetDwingsGuaranteesAsync().ConfigureAwait(false);
                ReconciliationViewData.InitializeDwingsCaches(invoices, guarantees);
            }
            catch { /* best-effort */ }
        }

        private async Task<List<ReconciliationViewData>> BuildReconciliationViewAsyncCore(string countryId, string filterSql, bool includeDeleted, string cacheKey)
        {
            var swBuild = Stopwatch.StartNew();
            var (dwEsc, ambreEsc) = GetEscapedPaths(countryId);

            // Detect PotentialDuplicates flag from optional JSON comment prefix (centralized helper)
            bool dupOnly = FilterSqlHelper.TryExtractPotentialDuplicatesFlag(filterSql);

            // Build the base query via centralized builder
            string query = ReconciliationViewQueryBuilder.Build(dwEsc, ambreEsc, filterSql);

            // CRITICAL: Filter out deleted records (unless includeDeleted=true for historical charts)
            if (!includeDeleted)
            {
                query += " AND a.DeleteDate IS NULL AND (r.DeleteDate IS NULL)";
            }

            // Apply Potential Duplicates predicate if requested via JSON
            if (dupOnly)
            {
                query += " AND (dup.DupCount) > 1";
            }

            var predicate = FilterSqlHelper.ExtractNormalizedPredicate(filterSql);
            if (!string.IsNullOrEmpty(predicate))
            {
                query += $" AND ({predicate})";
            }

            query += " ORDER BY a.Operation_Date ASC";

            swBuild.Stop();
            var swExec = Stopwatch.StartNew();
            var list = await _queryExecutor.QueryAsync<ReconciliationViewData>(query);

            // Pre-calculate all DWINGS properties once during load
            try
            {
                await EnsureDwingsCachesInitializedAsync().ConfigureAwait(false);

                var invoices = await GetDwingsInvoicesAsync().ConfigureAwait(false);
                var guarantees = await GetDwingsGuaranteesAsync().ConfigureAwait(false);

                // Link rows to DWINGS invoices (sets DWINGS_InvoiceID via heuristic matching)
                ReconciliationViewEnricher.EnrichWithDwingsInvoices(list, invoices);

                // Build lookup dicts ONCE and populate all DWINGS properties per row
                EnrichRowsWithDwingsProperties(list, invoices, guarantees);
            }
            catch { /* best-effort enrichment */ }
            
            // Calculate missing amounts for grouped lines (Receivable vs Pivot)
            try
            {
                var country = _countries?.TryGetValue(countryId, out var c) == true ? c : null;
                if (country != null)
                {
                    ReconciliationViewEnricher.CalculateMissingAmounts(list, country.CNT_AmbreReceivable, country.CNT_AmbrePivot);
                }
            }
            catch { /* best-effort calculation */ }

            // Compute transient UI flag: IsNewlyAdded (Ambre import date is today)
            try
            {
                var today = DateTime.Today;
                foreach (var row in list)
                {
                    if (row.CreationDate.HasValue && row.CreationDate.Value.Date == today)
                        row.IsNewlyAdded = true;
                }
            }
            catch { }
            
            // Compute AccountSide and Matched-across-accounts flag
            try
            {
                var currentCountry = _offlineFirstService?.CurrentCountry;
                var pivotId = currentCountry?.CNT_AmbrePivot;
                var recvId = currentCountry?.CNT_AmbreReceivable;

                AccountSideCalculator.AssignAccountSides(list, pivotId, recvId,
                    r => r.Account_ID, (r, side) => r.AccountSide = side);

                AccountSideCalculator.ComputeMatchedAcrossAccounts(list,
                    r => r.AccountSide,
                    r => r.DWINGS_InvoiceID,
                    r => r.InternalInvoiceReference,
                    (r, matched) => r.IsMatchedAcrossAccounts = matched,
                    r => AccountSideCalculator.ExtractFallbackBgiKey(
                        r.DWINGS_InvoiceID, r.Receivable_InvoiceFromAmbre,
                        r.Reconciliation_Num, r.Comments, r.RawLabel,
                        r.Receivable_DWRefFromAmbre, r.InternalInvoiceReference));
            }
            catch { }

            swExec.Stop();

            // Store materialized list for incremental updates
            _recoViewDataCache[cacheKey] = list;
            return list;
        }

        private static string NormalizeFilterForCache(string filterSql)
        {
            if (string.IsNullOrWhiteSpace(filterSql)) return string.Empty;
            var cond = filterSql.Trim();
            // Strip optional JSON header
            var m = Regex.Match(cond, @"^/\*JSON:(.*?)\*/\s*(.*)$", RegexOptions.Singleline);
            if (m.Success) cond = m.Groups[2].Value?.Trim();
            // Strip leading WHERE
            if (cond.StartsWith("WHERE ", StringComparison.OrdinalIgnoreCase)) cond = cond.Substring(6).Trim();
            // Collapse whitespace
            cond = Regex.Replace(cond, @"\s+", " ").Trim();
            return cond;
        }

        /// <summary>
        /// Lightweight DTO for status count calculation (loads only essential columns)
        /// </summary>
        private class StatusCountRow
        {
            public string ID { get; set; }
            public string Account_ID { get; set; }
            public double SignedAmount { get; set; }
            public string DWINGS_InvoiceID { get; set; }
            public string InternalInvoiceReference { get; set; }
            public DateTime? CreationDate { get; set; }
            public DateTime? Reco_CreationDate { get; set; }
            public DateTime? Reco_LastModified { get; set; }
            public string Reco_ModifiedBy { get; set; }
            public string AccountSide { get; set; }
            public bool IsNewlyAdded { get; set; }
            public bool IsUpdated { get; set; }
            public bool IsMatchedAcrossAccounts { get; set; }
            public double? MissingAmount { get; set; }
        }

        /// <summary>
        /// Returns status indicator counts for a filter (optimized with minimal data loading)
        /// OPTIMIZATION: Loads only essential columns instead of full ReconciliationViewData
        /// </summary>
        public async Task<StatusCountsDto> GetStatusCountsAsync(string countryId, string filterSql = null)
        {
            // Cache status counts
            var cacheKey = $"StatusCounts_{countryId}_{NormalizeFilterForCache(filterSql)}";
            return await CacheService.Instance.GetOrLoadAsync(cacheKey, async () =>
            {
                try
                {
                    var (dwEscStatus, ambreEscStatus) = GetEscapedPaths(countryId);

                    // Build minimal query with only columns needed for status calculation
                    string ambreJoin = string.IsNullOrEmpty(ambreEscStatus) ? "T_Data_Ambre AS a" : $"(SELECT * FROM [{ambreEscStatus}].T_Data_Ambre) AS a";
                    
                    string query = $@"
                        SELECT 
                            a.ID,
                            a.Account_ID,
                            a.SignedAmount,
                            a.CreationDate,
                            r.DWINGS_InvoiceID,
                            r.InternalInvoiceReference,
                            r.CreationDate AS Reco_CreationDate,
                            r.LastModified AS Reco_LastModified,
                            r.ModifiedBy AS Reco_ModifiedBy
                        FROM {ambreJoin}
                        LEFT JOIN T_Reconciliation AS r ON a.ID = r.ID
                        WHERE a.DeleteDate IS NULL AND (r.DeleteDate IS NULL)";

                    var predicate = FilterSqlHelper.ExtractNormalizedPredicate(filterSql);
                    if (!string.IsNullOrEmpty(predicate))
                    {
                        query += $" AND ({predicate})";
                    }

                    var rows = await _queryExecutor.QueryAsync<StatusCountRow>(query).ConfigureAwait(false);
                    if (rows == null || rows.Count == 0)
                        return new StatusCountsDto();

                    // Enrich rows with computed flags
                    var today = DateTime.Today;
                    var country = _countries?.TryGetValue(countryId, out var c) == true ? c : null;
                    string pivotId = country?.CNT_AmbrePivot?.Trim();
                    string receivableId = country?.CNT_AmbreReceivable?.Trim();

                    // Set IsNewlyAdded and IsUpdated flags
                    foreach (var row in rows)
                    {
                        // New if Ambre import date is today
                        if (row.CreationDate.HasValue && row.CreationDate.Value.Date == today)
                            row.IsNewlyAdded = true;
                        
                        // Updated if LastModified is today AND differs from CreationDate
                        // AND ModifiedBy is empty/null (automatic update, not user edit)
                        if (row.Reco_LastModified.HasValue && row.Reco_LastModified.Value.Date == today)
                        {
                            if (!row.Reco_CreationDate.HasValue || row.Reco_LastModified.Value > row.Reco_CreationDate.Value)
                            {
                                // Only mark as Updated if ModifiedBy is empty/null (import/rule)
                                // User manual edits should NOT trigger the "U" indicator
                                if (string.IsNullOrWhiteSpace(row.Reco_ModifiedBy))
                                    row.IsUpdated = true;
                            }
                        }
                    }

                    // Calculate IsMatchedAcrossAccounts using centralized helper
                    AccountSideCalculator.AssignAccountSides(rows, pivotId, receivableId,
                        r => r.Account_ID, (r, side) => r.AccountSide = side);

                    AccountSideCalculator.ComputeMatchedAcrossAccounts(rows,
                        r => r.AccountSide,
                        r => r.DWINGS_InvoiceID,
                        r => r.InternalInvoiceReference,
                        (r, matched) => r.IsMatchedAcrossAccounts = matched);

                    // Calculate MissingAmount for matched groups
                    AccountSideCalculator.ComputeMissingAmounts(rows,
                        r => r.IsMatchedAcrossAccounts,
                        r => r.DWINGS_InvoiceID,
                        r => r.InternalInvoiceReference,
                        r => r.AccountSide,
                        r => r.SignedAmount,
                        (r, amount) => r.MissingAmount = amount);

                    // Calculate status counts based on status color logic
                    var result = new StatusCountsDto
                    {
                        NewCount = rows.Count(r => r.IsNewlyAdded),
                        UpdatedCount = rows.Count(r => r.IsUpdated),
                        NotLinkedCount = rows.Count(r => 
                            string.IsNullOrWhiteSpace(r.DWINGS_InvoiceID) && 
                            string.IsNullOrWhiteSpace(r.InternalInvoiceReference)), // Red: No DWINGS link
                        NotGroupedCount = rows.Count(r => 
                            (!string.IsNullOrWhiteSpace(r.DWINGS_InvoiceID) || !string.IsNullOrWhiteSpace(r.InternalInvoiceReference)) &&
                            !r.IsMatchedAcrossAccounts), // Orange: Has link but not grouped
                        DiscrepancyCount = rows.Count(r => 
                            r.IsMatchedAcrossAccounts && 
                            r.MissingAmount.HasValue && 
                            r.MissingAmount.Value != 0), // Yellow/Amber: Grouped but has discrepancy
                        BalancedCount = rows.Count(r => 
                            r.IsMatchedAcrossAccounts && 
                            (!r.MissingAmount.HasValue || r.MissingAmount.Value == 0)) // Green: Balanced
                    };

                    return result;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"GetStatusCountsAsync error: {ex.Message}");
                    return new StatusCountsDto();
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the number of Live reconciliation rows matching the provided backend filter for the given country.
        /// Live means a.DeleteDate IS NULL and r.DeleteDate IS NULL.
        /// The filterSql may optionally include a JSON preset header; only the predicate is applied.
        /// OPTIMIZED: Cached based on countryId + filterSql (AMBRE data rarely changes)
        /// </summary>
        public async Task<int> GetReconciliationCountAsync(string countryId, string filterSql = null)
        {
            var cacheKey = $"RecoCount_{countryId}_{NormalizeFilterForCache(filterSql)}";
            return await CacheService.Instance.GetOrLoadAsync(cacheKey, async () =>
            {
                try
                {
                    var (dwEscCount, ambreEscCount) = GetEscapedPaths(countryId);
                    bool dupOnly = FilterSqlHelper.TryExtractPotentialDuplicatesFlag(filterSql);
                    string query = ReconciliationViewQueryBuilder.Build(dwEscCount, ambreEscCount, filterSql);
                    query += " AND a.DeleteDate IS NULL AND (r.DeleteDate IS NULL)";

                    var predicate = FilterSqlHelper.ExtractNormalizedPredicate(filterSql);
                    if (!string.IsNullOrEmpty(predicate))
                        query += $" AND ({predicate})";
                    if (dupOnly)
                        query += " AND (dup.DupCount) > 1";

                    var countSql = $"SELECT COUNT(*) FROM ({query}) AS q";
                    var obj = await _queryExecutor.ScalarAsync(countSql).ConfigureAwait(false);
                    if (obj == null || obj == DBNull.Value) return 0;
                    return int.TryParse(Convert.ToString(obj), out var n) ? n : 0;
                }
                catch { return 0; }
            }, TimeSpan.FromHours(2)).ConfigureAwait(false);
        }

        /// <summary>
        /// Preview rules for a single reconciliation ID (Edit scope) without applying them.
        /// Used by UI to show what rules would apply before saving.
        /// </summary>
        public async Task<RuleEvaluationResult> PreviewRulesForEditAsync(string id, string editedField = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(id)) return null;

                var currentCountryId = _offlineFirstService?.CurrentCountryId;
                if (string.IsNullOrWhiteSpace(currentCountryId) || _rulesEngine == null) return null;

                Country country = null;
                if (_countries != null && !_countries.TryGetValue(currentCountryId, out country)) return null;
                if (country == null) return null;

                var amb = await GetAmbreRowByIdAsync(currentCountryId, id).ConfigureAwait(false);
                if (amb == null) return null;

                var reconciliation = await GetOrCreateReconciliationAsync(id).ConfigureAwait(false);
                if (reconciliation == null) return null;

                bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);
                var ctx = await BuildRuleContextAsync(amb, reconciliation, country, currentCountryId, isPivot).ConfigureAwait(false);
                ctx.EditedField = editedField;
                var res = await _rulesEngine.EvaluateAsync(ctx, RuleScope.Edit).ConfigureAwait(false);

                return res;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Exécute immédiatement les règles (scope Edit) pour les IDs donnés.
        /// N'applique que les règles en Auto-apply; les autres peuvent ajouter un message.
        /// </summary>
        public async Task<int> ApplyRulesNowAsync(IEnumerable<string> ids, string editedField = null)
        {
            try
            {
                if (ids == null) return 0;
                // Ensure latest rules are loaded now
                try { _rulesEngine?.InvalidateCache(); } catch { }
                var distinct = ids.Where(id => !string.IsNullOrWhiteSpace(id))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .ToList();
                if (distinct.Count == 0) return 0;

                var recos = new List<Reconciliation>(distinct.Count);
                var currentCountryId = _offlineFirstService?.CurrentCountryId;
                Country country = null;
                if (!string.IsNullOrWhiteSpace(currentCountryId) && _countries != null)
                    _countries.TryGetValue(currentCountryId, out country);

                foreach (var id in distinct)
                {
                    // Skip archived rows (IsDeleted == true on Ambre row)
                    DataAmbre amb = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(currentCountryId))
                        {
                            amb = await GetAmbreRowByIdAsync(currentCountryId, id).ConfigureAwait(false);
                            if (amb == null || amb.IsDeleted) continue;
                        }
                    }
                    catch { }

                    var r = await GetOrCreateReconciliationAsync(id).ConfigureAwait(false);
                    if (r == null) continue;

                    // Evaluate and apply outputs in EDIT scope unconditionally when running now
                    try
                    {
                        if (country != null && _rulesEngine != null && amb != null)
                        {
                            bool isPivot = amb.IsPivotAccount(country.CNT_AmbrePivot);
                            var ctx = await BuildRuleContextAsync(amb, r, country, currentCountryId, isPivot).ConfigureAwait(false);
                            ctx.EditedField = editedField;
                            var res = await _rulesEngine.EvaluateAsync(ctx, RuleScope.Edit).ConfigureAwait(false);
                            RuleApplicationHelper.ApplyAndLog(res, r, _currentUser, "run-now", currentCountryId, RaiseRuleApplied, ProposalRepository);
                            if (res?.NewActionIdSelf.HasValue == true) EnsureActionDefaults(r);
                        }
                    }
                    catch { }

                    recos.Add(r);
                }
                if (recos.Count == 0) return 0;

                await SaveReconciliationsAsync(recos, applyRulesOnEdit: false).ConfigureAwait(false);
                return recos.Count;
            }
            catch { return 0; }
        }

        private void EnsureActionDefaults(Reconciliation r)
        {
            try
            {
                if (r == null) return;
                var all = _offlineFirstService?.UserFields;
                bool isNa = !r.Action.HasValue || UserFieldUpdateService.IsActionNA(r.Action, all);
                if (isNa)
                {
                    // FIX: N/A action should be marked as DONE, not null
                    r.ActionStatus = true;
                    r.ActionDate = DateTime.Now;
                }
                else
                {
                    if (!r.ActionStatus.HasValue) r.ActionStatus = false; // PENDING
                    // FIX: ALWAYS update ActionDate when Action changes
                    r.ActionDate = DateTime.Now;
                }
            }
            catch { }
        }
        #endregion

        // The remaining surface of ReconciliationService lives in sibling partial files:
        //   • ReconciliationService.Events.cs    — RuleApplied event + RuleAppliedEventArgs + RaiseRuleApplied
        //   • ReconciliationService.Delegates.cs — ReconciliationMatchingService + RuleContextBuilder delegations + GetAmbreRowByIdAsync
        //   • ReconciliationService.Crud.cs      — Get/Save/Simulate/RuleDebug + SaveSingleReconciliationAsync + AddReconciliationParameters
        //   • ReconciliationService.AmbreSim.cs  — AMBRE import simulation helpers
    }
}
