using System.Collections.Generic;
using System.Threading.Tasks;
using RecoTool.Services.DTOs;

namespace RecoTool.Services
{
    /// <summary>
    /// Minimal seam over <see cref="DwingsService"/>, exposing the lifecycle members
    /// (cache priming + invalidation) and the read-only invoice/guarantee lookups
    /// consumed by other components (currently <see cref="ReconciliationService"/>).
    ///
    /// Static helpers (<see cref="DwingsService.LoadFromPathAsync"/> and
    /// <see cref="DwingsService.InvalidateSharedCacheForPath"/>) stay static — they
    /// don't depend on instance state and shouldn't be on this interface.
    ///
    /// Production implementation: <see cref="DwingsService"/>.
    /// Tests: Moq stub or hand-rolled fake.
    /// </summary>
    public interface IDwingsService
    {
        /// <summary>
        /// Ensures the in-memory invoice/guarantee caches are loaded from the local DW
        /// database. Idempotent — subsequent calls return without I/O when the path is
        /// unchanged and caches are still valid.
        /// </summary>
        Task PrimeCachesAsync();

        /// <summary>
        /// Marks the in-memory caches as stale. The next <see cref="PrimeCachesAsync"/>
        /// call will reload them from disk.
        /// </summary>
        void InvalidateCaches();

        /// <summary>
        /// Returns the in-memory list of DWINGS invoices for the active country.
        /// Triggers a load via <see cref="PrimeCachesAsync"/> if not yet primed.
        /// </summary>
        Task<IReadOnlyList<DwingsInvoiceDto>> GetInvoicesAsync();

        /// <summary>
        /// Returns the in-memory list of DWINGS guarantees for the active country.
        /// Triggers a load via <see cref="PrimeCachesAsync"/> if not yet primed.
        /// </summary>
        Task<IReadOnlyList<DwingsGuaranteeDto>> GetGuaranteesAsync();
    }
}
