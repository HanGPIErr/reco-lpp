using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Models;

namespace RecoTool.Services
{
    /// <summary>
    /// Minimal seam over <see cref="OfflineFirstService"/>, exposing only the members
    /// consumed by lower-layer services that need to be testable without a full OFS.
    ///
    /// This interface is intentionally narrow:
    /// — anything that requires the full sync/lock/path machinery should still depend
    ///   on the concrete <see cref="OfflineFirstService"/>;
    /// — anything that only reads paths, the referential connection string, or the
    ///   in-memory user fields should depend on this interface so it can be mocked.
    ///
    /// Implementations: production = <see cref="OfflineFirstService"/>; tests = Moq stub
    /// or a hand-rolled fake.
    /// </summary>
    public interface IOfflineFirstService
    {
        /// <summary>
        /// Local file path to the AMBRE Access database for the given country
        /// (or current country if <paramref name="countryId"/> is null/empty).
        /// Returns <c>null</c> when no path can be resolved (no current country, etc.).
        /// </summary>
        string GetLocalAmbreDatabasePath(string countryId = null);

        /// <summary>
        /// Local file path to the DWINGS Access database for the given country
        /// (or current country if <paramref name="countryId"/> is null/empty).
        /// Returns <c>null</c> when no path can be resolved.
        /// </summary>
        string GetLocalDWDatabasePath(string countryId = null);

        /// <summary>
        /// OLE DB connection string to the AMBRE database for the given country
        /// (or current country if <paramref name="countryId"/> is null/empty).
        /// </summary>
        string GetAmbreConnectionString(string countryId = null);

        /// <summary>
        /// OLE DB connection string to the country's main reconciliation database
        /// (T_Reconciliation lives there).
        /// </summary>
        string GetCountryConnectionString(string countryId);

        /// <summary>
        /// OLE DB connection string for the referential database (T_Param, T_User, T_Ref_*).
        /// </summary>
        string ReferentialConnectionString { get; }

        /// <summary>
        /// Snapshot copy of the in-memory user-fields list (Action / KPI / Incident referential).
        /// Returns a defensive copy — modifying the returned list does not affect OFS state.
        /// </summary>
        List<UserField> UserFields { get; }

        /// <summary>
        /// Identifier of the active country (the one currently selected in the UI).
        /// May be <c>null</c> when no country has been selected yet.
        /// </summary>
        string CurrentCountryId { get; }

        /// <summary>
        /// Active country POCO. May be <c>null</c> when no country has been selected yet.
        /// </summary>
        Country CurrentCountry { get; }

        /// <summary>
        /// Updates a transient sync status string surfaced in the UI status bar
        /// (e.g. "Importing", "Syncing", "Idle"). Best-effort, may no-op when the
        /// network DB is offline.
        /// </summary>
        Task SetSyncStatusAsync(string status, CancellationToken token = default);

        // ─────────────────────────────────────────────────────────────────────
        // Referential lookups consumed by the AMBRE import pipeline.
        // All return the in-memory snapshot loaded at startup; refreshed by
        // OfflineFirstService.RefreshReferentialsAsync().
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>List of countries available for the user (T_Ref_Country).</summary>
        Task<List<Country>> GetCountries();

        /// <summary>Looks up a country by its identifier. Returns null if not found.</summary>
        Task<Country> GetCountryByIdAsync(string countryId);

        /// <summary>
        /// Mapping of source columns to AMBRE destination fields (T_Ref_Ambre_ImportFields).
        /// </summary>
        List<AmbreImportField> GetAmbreImportFields();

        /// <summary>
        /// Functional transformations to apply on each imported AMBRE row
        /// (T_Ref_Ambre_Transform).
        /// </summary>
        List<AmbreTransform> GetAmbreTransforms();

        /// <summary>
        /// Mapping ATC code → transaction type tag (T_Ref_Ambre_TransactionCodes).
        /// </summary>
        List<AmbreTransactionCode> GetAmbreTransactionCodes();
    }
}
