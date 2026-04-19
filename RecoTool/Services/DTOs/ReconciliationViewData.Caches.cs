using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace RecoTool.Services.DTOs
{
    /// <summary>
    /// Static caches for <see cref="ReconciliationViewData"/>:
    /// <list type="bullet">
    /// <item><b>Brush cache</b> — converts hex color strings to frozen <see cref="SolidColorBrush"/> once per color.
    /// Consumed all over the instance code via <see cref="GetCachedBrush"/>. Avoids hundreds of allocations per
    /// visible row during scroll (pre-refactor this was one of the biggest GC pressure sources in the grid).</item>
    /// <item><b>DWINGS invoice / guarantee lookup caches</b> — populated once per country switch with
    /// <see cref="ReconciliationViewData.InitializeDwingsCaches"/> and consumed by
    /// <c>RefreshDwingsData()</c> when the user edits a DWINGS reference on a row.</item>
    /// </list>
    /// <para>
    /// Extracted into this partial file so the main <c>ReconciliationViewData.cs</c> focuses on the entity shape
    /// (properties + change notifications).
    /// </para>
    /// </summary>
    public partial class ReconciliationViewData
    {
        // ──────────────────────────────────────────────────────────────────────────────────────
        // Frozen brush cache (hex → SolidColorBrush). Reused by display-state properties.
        // ──────────────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, SolidColorBrush> _brushCache
            = new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);

        internal static readonly SolidColorBrush _transparentBrush;
        internal static readonly SolidColorBrush _defaultBorderBrush;

        static ReconciliationViewData()
        {
            _transparentBrush = new SolidColorBrush(Colors.Transparent);
            _transparentBrush.Freeze();
            _defaultBorderBrush = GetCachedBrush("#DDDDDD");
        }

        /// <summary>
        /// Returns a frozen brush for the given hex string (e.g. <c>#E8F5E9</c>), reusing a cached instance
        /// on subsequent calls. <c>null</c>, empty or <c>"Transparent"</c> always resolve to the shared
        /// transparent brush. Parse failures fall back to transparent silently — we don't want grid scroll
        /// to be affected by a bad color value in a rule.
        /// </summary>
        internal static SolidColorBrush GetCachedBrush(string hex)
        {
            if (string.IsNullOrEmpty(hex) || string.Equals(hex, "Transparent", StringComparison.OrdinalIgnoreCase))
                return _transparentBrush;
            if (_brushCache.TryGetValue(hex, out var cached)) return cached;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                _brushCache[hex] = b;
                return b;
            }
            catch
            {
                return _transparentBrush;
            }
        }

        // ──────────────────────────────────────────────────────────────────────────────────────
        // DWINGS lookup caches (Invoice/Guarantee). Shared across all instances; rebuilt per country.
        // Access is synchronised through _dwingsCacheLock because InitializeDwingsCaches runs on
        // a background loader while RefreshDwingsData() can fire from the UI thread.
        // ──────────────────────────────────────────────────────────────────────────────────────
        private static Dictionary<string, DwingsInvoiceDto> _dwingsInvoiceCache;
        private static Dictionary<string, DwingsGuaranteeDto> _dwingsGuaranteeCache;
        private static readonly object _dwingsCacheLock = new object();

        /// <summary>
        /// Populates the per-country DWINGS caches. Called once by the loading pipeline after the
        /// DWINGS repository responds. Safe to call repeatedly — the caches are rebuilt wholesale.
        /// <para>
        /// <b>Note</b>: <c>INVOICE_ID</c> is NOT guaranteed unique in source data; we keep the first
        /// occurrence to remain deterministic. <c>GUARANTEE_ID</c> should be unique, but we defensively
        /// apply the same rule.
        /// </para>
        /// </summary>
        public static void InitializeDwingsCaches(IEnumerable<DwingsInvoiceDto> invoices, IEnumerable<DwingsGuaranteeDto> guarantees)
        {
            lock (_dwingsCacheLock)
            {
                _dwingsInvoiceCache = new Dictionary<string, DwingsInvoiceDto>(StringComparer.OrdinalIgnoreCase);
                _dwingsGuaranteeCache = new Dictionary<string, DwingsGuaranteeDto>(StringComparer.OrdinalIgnoreCase);

                if (invoices != null)
                {
                    foreach (var inv in invoices)
                    {
                        if (string.IsNullOrWhiteSpace(inv?.INVOICE_ID)) continue;
                        if (_dwingsInvoiceCache.ContainsKey(inv.INVOICE_ID)) continue; // keep first
                        _dwingsInvoiceCache[inv.INVOICE_ID] = inv;
                    }
                }

                if (guarantees != null)
                {
                    foreach (var guar in guarantees)
                    {
                        if (string.IsNullOrWhiteSpace(guar?.GUARANTEE_ID)) continue;
                        if (_dwingsGuaranteeCache.ContainsKey(guar.GUARANTEE_ID)) continue; // keep first (defensive)
                        _dwingsGuaranteeCache[guar.GUARANTEE_ID] = guar;
                    }
                }
            }
        }

        /// <summary>
        /// Drops the DWINGS caches (called on country switch so we don't serve stale data for the new scope).
        /// </summary>
        public static void ClearDwingsCaches()
        {
            lock (_dwingsCacheLock)
            {
                _dwingsInvoiceCache?.Clear();
                _dwingsGuaranteeCache?.Clear();
            }
        }

        /// <summary>
        /// Internal accessor used by <c>RefreshDwingsData()</c>. Returns copies of the current cache
        /// references under lock so callers can read without holding the lock for the whole operation.
        /// </summary>
        internal static void GetDwingsCacheSnapshots(
            out Dictionary<string, DwingsInvoiceDto> invoiceCache,
            out Dictionary<string, DwingsGuaranteeDto> guaranteeCache)
        {
            lock (_dwingsCacheLock)
            {
                invoiceCache = _dwingsInvoiceCache;
                guaranteeCache = _dwingsGuaranteeCache;
            }
        }
    }
}
