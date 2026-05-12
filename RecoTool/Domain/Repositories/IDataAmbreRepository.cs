using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Models;

namespace RecoTool.Domain.Repositories
{
    /// <summary>
    /// Read-only abstraction over the per-country <c>T_Data_Ambre</c> table.
    ///
    /// <para>
    /// Each country has its own Ambre database file; concrete implementations are
    /// responsible for resolving the connection string from the country id (typically
    /// via a <c>Func&lt;string, string&gt;</c> factory). Callers should NOT cache the
    /// resulting lists across country switches — switching country changes both the
    /// underlying file and the row set.
    /// </para>
    ///
    /// <para>
    /// All methods MUST honour the soft-delete convention used elsewhere in the
    /// codebase: rows with non-null <c>DeleteDate</c> are excluded unless explicitly
    /// requested via <paramref name="includeDeleted"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Returning empty</b> — implementations should never return <c>null</c>.
    /// When no row matches, an empty <c>IReadOnlyList</c> is returned. When the
    /// underlying file is unreadable, implementations may either throw the original
    /// <c>OleDbException</c> or return empty; this contract intentionally leaves the
    /// choice to the implementor (the in-memory test double never throws).
    /// </para>
    /// </summary>
    public interface IDataAmbreRepository
    {
        /// <summary>
        /// Returns every live Ambre row for the given country, ordered by Operation_Date ASC.
        /// </summary>
        /// <param name="countryId">Country identifier (e.g. <c>"FR"</c>). Must not be null/empty.</param>
        /// <param name="includeDeleted">When <c>true</c>, soft-deleted rows are included.</param>
        /// <param name="ct">Cancellation token. Checked before the OleDb call starts.</param>
        Task<IReadOnlyList<DataAmbre>> GetAllAsync(string countryId, bool includeDeleted = false, CancellationToken ct = default);

        /// <summary>
        /// Returns a single row by its <c>ID</c>, or <c>null</c> when not found.
        /// Soft-deleted rows are excluded.
        /// </summary>
        /// <param name="countryId">Country identifier. Must not be null/empty.</param>
        /// <param name="id">Primary key (Ambre row id).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<DataAmbre> GetByIdAsync(string countryId, string id, CancellationToken ct = default);

        /// <summary>
        /// Returns the rows for a specific <c>Account_ID</c>, ordered by Operation_Date ASC.
        /// Useful to load all rows on the Pivot or Receivable side of a country.
        /// Soft-deleted rows are excluded.
        /// </summary>
        /// <param name="countryId">Country identifier.</param>
        /// <param name="accountId">Account id (e.g. country.CNT_AmbrePivot or CNT_AmbreReceivable).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<IReadOnlyList<DataAmbre>> GetByAccountAsync(string countryId, string accountId, CancellationToken ct = default);
    }
}
