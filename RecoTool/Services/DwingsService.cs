using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;

namespace RecoTool.Services
{
    /// <summary>
    /// Encapsulates DWINGS local database access with shared, coalesced in-memory caches.
    /// Extracted from ReconciliationService to reduce responsibility and improve reuse.
    /// </summary>
    public class DwingsService
    {
        private readonly OfflineFirstService _offlineFirstService;

        public DwingsService(OfflineFirstService offlineFirstService)
        {
            _offlineFirstService = offlineFirstService;
        }

        private List<DwingsInvoiceDto> _invoicesCache;
        private List<DwingsGuaranteeDto> _guaranteesCache;
        private string _dwPath;
        private volatile bool _invalidated;

        // Shared per-DW path cache across all service instances
        private static readonly ConcurrentDictionary<string, Lazy<Task<(List<DwingsInvoiceDto> invoices, List<DwingsGuaranteeDto> guarantees)>>> _sharedDwCache
            = new ConcurrentDictionary<string, Lazy<Task<(List<DwingsInvoiceDto>, List<DwingsGuaranteeDto>)>>>();

        public static void InvalidateSharedCacheForPath(string dwPath)
        {
            if (string.IsNullOrWhiteSpace(dwPath)) return;
            try { _sharedDwCache.TryRemove(dwPath, out _); } catch { }
        }

        public async Task PrimeCachesAsync()
        {
            var dwPath = _offlineFirstService?.GetLocalDWDatabasePath();
            if (string.IsNullOrWhiteSpace(dwPath) || !File.Exists(dwPath))
            {
                _invoicesCache = new List<DwingsInvoiceDto>();
                _guaranteesCache = new List<DwingsGuaranteeDto>();
                _dwPath = null;
                _invalidated = false;
                return;
            }

            bool needReload = _invalidated
                              || _invoicesCache == null || _guaranteesCache == null
                              || !string.Equals(_dwPath, dwPath, StringComparison.OrdinalIgnoreCase);
            if (!needReload) return;

            if (_invalidated && !string.IsNullOrWhiteSpace(_dwPath))
            {
                _sharedDwCache.TryRemove(_dwPath, out _);
            }

            var loader = _sharedDwCache.GetOrAdd(dwPath,
                new Lazy<Task<(List<DwingsInvoiceDto>, List<DwingsGuaranteeDto>)>>(
                    () => LoadDwingsAsync(dwPath),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));

            var tuple = await loader.Value.ConfigureAwait(false);
            _invoicesCache = tuple.Item1;
            _guaranteesCache = tuple.Item2;
            _dwPath = dwPath;
            _invalidated = false;
        }

        public void InvalidateCaches()
        {
            _invalidated = true;
            if (!string.IsNullOrWhiteSpace(_dwPath))
            {
                _sharedDwCache.TryRemove(_dwPath, out _);
            }
        }

        public async Task<IReadOnlyList<DwingsInvoiceDto>> GetInvoicesAsync()
        {
            await PrimeCachesAsync().ConfigureAwait(false);
            return _invoicesCache ?? new List<DwingsInvoiceDto>();
        }

        public async Task<IReadOnlyList<DwingsGuaranteeDto>> GetGuaranteesAsync()
        {
            await PrimeCachesAsync().ConfigureAwait(false);
            return _guaranteesCache ?? new List<DwingsGuaranteeDto>();
        }

        /// <summary>
        /// Loads DWINGS data from an arbitrary file path (used by the AMBRE-import simulator so
        /// the user can what-if a fresh DWINGS snapshot without touching the country's DW cache).
        /// Accepts either a `.accdb` or a `.zip` containing exactly one `.accdb`; in the zip case
        /// the database is extracted to a temp file, loaded, then the temp file is deleted.
        /// </summary>
        /// <param name="path">Absolute path to a DWINGS `.accdb` or `.zip`.</param>
        public static async Task<(List<DwingsInvoiceDto> invoices, List<DwingsGuaranteeDto> guarantees)> LoadFromPathAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path is required", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("DWINGS file not found", path);

            bool isZip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
            if (!isZip)
                return await LoadDwingsAsync(path).ConfigureAwait(false);

            // Extract the single .accdb from the zip into a temp file.
            string tempAccdb = Path.Combine(Path.GetTempPath(), $"dwings_sim_{Guid.NewGuid():N}.accdb");
            try
            {
                using (var archive = System.IO.Compression.ZipFile.OpenRead(path))
                {
                    var entry = archive.Entries.FirstOrDefault(en =>
                        !string.IsNullOrEmpty(en.Name) &&
                        string.Equals(Path.GetExtension(en.Name), ".accdb", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        throw new InvalidDataException("The DWINGS zip does not contain a .accdb file.");
                    // Stream-copy instead of ExtractToFile — avoids the extra System.IO.Compression.FileSystem assembly reference.
                    using (var entryStream = entry.Open())
                    using (var fs = new FileStream(tempAccdb, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await entryStream.CopyToAsync(fs).ConfigureAwait(false);
                    }
                }
                return await LoadDwingsAsync(tempAccdb).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tempAccdb)) File.Delete(tempAccdb); } catch { /* best-effort cleanup */ }
            }
        }

        private static async Task<(List<DwingsInvoiceDto> invoices, List<DwingsGuaranteeDto> guarantees)> LoadDwingsAsync(string dwPath)
        {
            var dwCs = DbConn.AceConn(dwPath);
            var invoices = new List<DwingsInvoiceDto>();
            var guarantees = new List<DwingsGuaranteeDto>();

            using (var connection = new OleDbConnection(dwCs))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (var cmd = new OleDbCommand(@"SELECT * FROM T_DW_Data ORDER BY INVOICE_ID, BGPMT", connection))
                using (var rd = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rd.ReadAsync().ConfigureAwait(false))
                    {
                        invoices.Add(new DwingsInvoiceDto
                        {
                            INVOICE_ID = TryToStringSafe(rd, "INVOICE_ID"),
                            T_INVOICE_STATUS = TryToStringSafe(rd, "T_INVOICE_STATUS"),
                            BILLING_AMOUNT = TryToDecimal(TryGet(rd, "BILLING_AMOUNT")),
                            REQUESTED_AMOUNT = TryToDecimal(TryGet(rd, "REQUESTED_AMOUNT")),
                            FINAL_AMOUNT = TryToDecimal(TryGet(rd, "FINAL_AMOUNT")),
                            BILLING_CURRENCY = TryToStringSafe(rd, "BILLING_CURRENCY"),
                            BGPMT = TryToStringSafe(rd, "BGPMT"),
                            PAYMENT_METHOD = TryToStringSafe(rd, "PAYMENT_METHOD"),
                            T_PAYMENT_REQUEST_STATUS = TryToStringSafe(rd, "T_PAYMENT_REQUEST_STATUS"),
                            SENDER_REFERENCE = TryToStringSafe(rd, "SENDER_REFERENCE"),
                            RECEIVER_REFERENCE = TryToStringSafe(rd, "RECEIVER_REFERENCE"),
                            SENDER_NAME = TryToStringSafe(rd, "SENDER_NAME"),
                            RECEIVER_NAME = TryToStringSafe(rd, "RECEIVER_NAME"),
                            BUSINESS_CASE_REFERENCE = TryToStringSafe(rd, "BUSINESS_CASE_REFERENCE"),
                            BUSINESS_CASE_ID = TryToStringSafe(rd, "BUSINESS_CASE_ID"),
                            SENDER_ACCOUNT_NUMBER = TryToStringSafe(rd, "SENDER_ACCOUNT_NUMBER"),
                            SENDER_ACCOUNT_BIC = TryToStringSafe(rd, "SENDER_ACCOUNT_BIC"),
                            REQUESTED_EXECUTION_DATE = TryToDate(TryGet(rd, "REQUESTED_EXECUTION_DATE")),
                            START_DATE = TryToDate(TryGet(rd, "START_DATE")),
                            END_DATE = TryToDate(TryGet(rd, "END_DATE")),
                            DEBTOR_PARTY_NAME = TryToStringSafe(rd, "DEBTOR_PARTY_NAME"),
                            CREDITOR_PARTY_NAME = TryToStringSafe(rd, "CREDITOR_PARTY_NAME"),
                            MT_STATUS = TryToStringSafe(rd, "MT_STATUS"),
                            ERROR_MESSAGE = TryToStringSafe(rd, "ERROR_MESSAGE"),
                            COMM_ID_EMAIL = TryToBool(TryGet(rd, "COMM_ID_EMAIL")),
                            // MT_DATE is the date the wire transfer was booked. Used to seed
                            // FirstClaimDate on ACK'd reconciliations (FirstClaimDate stays user-editable
                            // afterwards — we never overwrite a non-empty value).
                            MT_DATE = TryToDate(TryGet(rd, "MT_DATE")),
                        });
                    }
                }

                using (var cmdG = new OleDbCommand(@"SELECT * FROM T_DW_Guarantee", connection))
                using (var rdG = await cmdG.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await rdG.ReadAsync().ConfigureAwait(false))
                    {
                        guarantees.Add(new DwingsGuaranteeDto
                        {
                            GUARANTEE_ID = TryToStringSafe(rdG, "GUARANTEE_ID"),
                            GUARANTEE_STATUS = TryToStringSafe(rdG, "GUARANTEE_STATUS"),
                            OUTSTANDING_AMOUNT = TryToDecimal(TryGet(rdG, "OUTSTANDING_AMOUNT")),
                            CURRENCYNAME = TryToStringSafe(rdG, "CURRENCYNAME"),
                            NAME1 = TryToStringSafe(rdG, "NAME1"),
                            NAME2 = TryToStringSafe(rdG, "NAME2"),
                            GUARANTEE_TYPE = TryToStringSafe(rdG, "GUARANTEE_TYPE"),
                            NATURE = TryToStringSafe(rdG, "NATURE"),
                            EVENT_STATUS = TryToStringSafe(rdG, "EVENT_STATUS"),
                            EVENT_EFFECTIVEDATE = TryToDate(TryGet(rdG, "EVENT_EFFECTIVEDATE")),
                            ISSUEDATE = TryToDate(TryGet(rdG, "ISSUEDATE")),
                            OFFICIALREF = TryToStringSafe2(rdG, "OFFICIALREF"),
                            LEGACYREF = TryToStringSafe(rdG, "LEGACYREF"),
                            UNDERTAKINGEVENT = TryToStringSafe(rdG, "UNDERTAKINGEVENT"),
                            PROCESS = TryToStringSafe(rdG, "PROCESS"),
                            EXPIRYDATETYPE = TryToStringSafe(rdG, "EXPIRYDATETYPE"),
                            EXPIRYDATE = TryToDate(TryGet(rdG, "EXPIRYDATE")),
                            PARTY_ID = TryToStringSafe(rdG, "PARTY_ID"),
                            PARTY_REF = TryToStringSafe(rdG, "PARTY_REF"),
                            SECONDARY_OBLIGOR = TryToStringSafe(rdG, "SECONDARY_OBLIGOR"),
                            SECONDARY_OBLIGOR_NATURE = TryToStringSafe(rdG, "SECONDARY_OBLIGOR_NATURE"),
                            ROLE = TryToStringSafe(rdG, "ROLE"),
                            COUNTRY = TryToStringSafe(rdG, "COUNTRY"),
                            //CENTRAL_PARTY_CODE = TryToStringSafe(rdG, "BIC_CODE"),
                            GROUPE = TryToStringSafe(rdG, "GROUPE"),
                            PREMIUM = TryToStringSafe(rdG, "PREMIUM"),
                            BRANCH_CODE = TryToStringSafe(rdG, "BRANCH_CODE"),
                            BRANCH_NAME = TryToStringSafe(rdG, "BRANCH_NAME"),
                            OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY = TryToStringSafe(rdG, "OUTSTANDING_AMOUNT_IN_BOOKING_CURRENCY"),
                            CANCELLATIONDATE = TryToDate(TryGet(rdG, "CANCELLATIONDATE")),
                            CONTROLER = TryToStringSafe(rdG, "CONTROLER"),
                            AUTOMATICBOOKOFF = TryToStringSafe(rdG, "AUTOMATICBOOKOFF"),
                            NATUREOFDEAL = TryToStringSafe(rdG, "NATUREOFDEAL"),
                        }); ;
                    }
                }
            }

            return (invoices, guarantees);
        }

        private static decimal? TryToDecimal(object o)
        {
            if (o == null || o == DBNull.Value) return null;
            try { return Convert.ToDecimal(o, CultureInfo.InvariantCulture); } catch { return null; }
        }

        /// <summary>
        /// Coerces a DWINGS column value to <see cref="DateTime"/>. Handles both:
        /// <list type="bullet">
        /// <item>True DATE columns — the OleDb provider already gives us a <see cref="DateTime"/>.</item>
        /// <item>TEXT columns that store the date as a string. <c>MT_DATE</c> in <c>T_DW_Data</c>
        /// is the prime example: column type is TEXT in the source extract, with values such as
        /// <c>"23-APR-26"</c>. The default <see cref="Convert.ToDateTime(object, IFormatProvider)"/>
        /// rejects those formats under InvariantCulture and silently returns null, which broke
        /// the FirstClaimDate seed. We delegate string parsing to <see cref="DwingsDateHelper.TryParseDwingsDate"/>
        /// (already used for the lazy-loaded I_*/G_* date properties) so all DWINGS date formats
        /// — <c>dd-MMM-yy</c>, <c>dd/MM/yyyy</c>, ISO, etc. — are recognised consistently.</item>
        /// </list>
        /// </summary>
        private static DateTime? TryToDate(object o)
        {
            if (o == null || o == DBNull.Value) return null;
            if (o is DateTime dt) return dt;

            // Stringified value: DWINGS extracts frequently store dates in TEXT columns.
            var s = Convert.ToString(o, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return null;

            if (DwingsDateHelper.TryParseDwingsDate(s, out var parsed))
                return parsed;

            // Last-resort generic parse — keeps backward compatibility for any pre-existing
            // exotic format that the helper would not match (kept inside try/catch on purpose).
            try { return Convert.ToDateTime(s, CultureInfo.InvariantCulture); } catch { return null; }
        }

        private static bool? TryToBool(object o)
        {
            if (o == null || o == DBNull.Value) return null;
            try
            {
                if (o is bool b) return b;
                // Access can store YESNO as -1 (true) / 0 (false)
                if (o is sbyte sb) return sb != 0;
                if (o is short s) return s != 0;
                if (o is int i) return i != 0;
                if (o is long l) return l != 0;
                var sVal = Convert.ToString(o)?.Trim();
                if (string.IsNullOrEmpty(sVal)) return null;
                if (string.Equals(sVal, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(sVal, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (int.TryParse(sVal, out var iv)) return iv != 0;
                return null;
            }
            catch { return null; }
        }

        private static string TryToStringSafe(DbDataReader rd, string column)
        {
            try
            {
                int ord = rd.GetOrdinal(column);
                if (ord < 0) return null;
                return rd.IsDBNull(ord) ? null : rd.GetValue(ord).ToString();
            }
            catch { return null; }
        }

        private static string TryToStringSafe2(DbDataReader rd, string column)
        {
            try
            {
                int ord = rd.GetOrdinal(column);
                if (ord < 0) return null;
                return rd.IsDBNull(ord) ? null : rd.GetString(ord);
            }
            catch { return null; }
        }

        private static object TryGet(DbDataReader rd, string column)
        {
            try
            {
                int ord = rd.GetOrdinal(column);
                if (ord < 0) return null;
                return rd.IsDBNull(ord) ? null : rd.GetValue(ord);
            }
            catch { return null; }
        }
    }
}
