using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Models;

namespace RecoTool.Domain.Repositories
{
    /// <summary>
    /// Read-only abstraction over <c>T_Ref_User_Fields</c> (the referential lookup table
    /// that backs every combobox in the app: Actions, KPIs, Incident Types, Reason Non-Risky, …).
    ///
    /// <para>
    /// The table lives in the shared referential database, so a concrete implementation
    /// only needs a single fixed connection string (no per-country dispatch).
    /// </para>
    ///
    /// <para>
    /// <b>Returning empty</b> — implementations should never return <c>null</c>.
    /// </para>
    /// </summary>
    public interface IUserFieldsRepository
    {
        /// <summary>
        /// Returns every user field, ordered by category then field name (the canonical
        /// presentation order used by <c>OfflineFirstService.LoadUserFieldsAsync</c>).
        /// </summary>
        /// <param name="ct">Cancellation token. Checked before the OleDb call starts.</param>
        Task<IReadOnlyList<UserField>> GetAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns a single user field by its <c>USR_ID</c>, or <c>null</c> when not found.
        /// </summary>
        /// <param name="id">Primary key (<c>USR_ID</c>).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<UserField> GetByIdAsync(int id, CancellationToken ct = default);

        /// <summary>
        /// Returns every user field for a given category (e.g. <c>"Action"</c>, <c>"KPI"</c>),
        /// ordered by field name. Case-insensitive match on <c>USR_Category</c>.
        /// Returns an empty list when the category is null/empty or has no entries.
        /// </summary>
        /// <param name="category">Category name (e.g. <c>"Action"</c>).</param>
        /// <param name="ct">Cancellation token.</param>
        Task<IReadOnlyList<UserField>> GetByCategoryAsync(string category, CancellationToken ct = default);
    }
}
