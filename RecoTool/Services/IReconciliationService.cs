using System.Collections.Generic;
using System.Threading.Tasks;
using RecoTool.Models;
using RecoTool.Services.DTOs;

namespace RecoTool.Services
{
    /// <summary>
    /// Minimal seam over <see cref="ReconciliationService"/> exposing only the
    /// members consumed by <see cref="ReconciliationMatchingService"/> and
    /// <see cref="Rules.RuleContextBuilder"/>.
    ///
    /// Extracted to make those services unit-testable without instantiating the
    /// full reconciliation pipeline (which requires an OleDb-bound query
    /// executor and a real Access database).
    ///
    /// Production implementation: <see cref="ReconciliationService"/>.
    /// Tests: Moq stub or hand-rolled fake.
    /// </summary>
    public interface IReconciliationService
    {
        /// <summary>
        /// Identifier of the user whose actions are attributed by this service
        /// (used for audit fields in the persisted reconciliations).
        /// </summary>
        string CurrentUser { get; }

        /// <summary>
        /// Returns AMBRE rows for the given country. By default deleted rows are excluded.
        /// </summary>
        Task<List<DataAmbre>> GetAmbreDataAsync(string countryId, bool includeDeleted = false);

        /// <summary>
        /// Loads the existing reconciliation for the given AMBRE row id, or creates
        /// a fresh one if none exists.
        /// </summary>
        Task<Reconciliation> GetOrCreateReconciliationAsync(string id);

        /// <summary>
        /// Persists the given reconciliations. Returns true on success.
        /// </summary>
        Task<bool> SaveReconciliationsAsync(IEnumerable<Reconciliation> reconciliations, bool applyRulesOnEdit = true);

        /// <summary>
        /// In-memory view of the DWINGS invoices currently cached for the active country.
        /// </summary>
        Task<IReadOnlyList<DwingsInvoiceDto>> GetDwingsInvoicesAsync();

        /// <summary>
        /// In-memory view of the DWINGS guarantees currently cached for the active country.
        /// </summary>
        Task<IReadOnlyList<DwingsGuaranteeDto>> GetDwingsGuaranteesAsync();
    }
}
