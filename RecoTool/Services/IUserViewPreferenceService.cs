using System.Collections.Generic;
using System.Threading.Tasks;
using RecoTool.Models;

namespace RecoTool.Services
{
    /// <summary>
    /// Abstraction over <see cref="UserViewPreferenceService"/> so consumers
    /// can be unit-tested without OleDb. Mirrors the public surface of the
    /// concrete service.
    /// </summary>
    public interface IUserViewPreferenceService
    {
        /// <summary>Returns all saved views (id, name, user, SQL, column widths).</summary>
        Task<List<UserFieldsPreference>> GetAllAsync();

        /// <summary>Returns a single saved view by name, or null when not found.</summary>
        Task<UserFieldsPreference> GetByNameAsync(string name);

        /// <summary>Inserts a new saved view and returns its generated identifier.</summary>
        Task<int> InsertAsync(string name, string sql, string columnsJson);

        /// <summary>Updates an existing saved view by id. Returns true when at least one row changed.</summary>
        Task<bool> UpdateAsync(int id, string name, string sql, string columnsJson);

        /// <summary>Inserts or updates a saved view, keyed by (name, current user). Returns the row id.</summary>
        Task<int> UpsertAsync(string name, string sql, string columnsJson);

        /// <summary>Lists saved view names for the current user, optionally filtered by substring.</summary>
        Task<List<string>> ListNamesAsync(string contains = null);

        /// <summary>Lists (name, creator) tuples for the current user, optionally filtered.</summary>
        Task<List<(string Name, string Creator)>> ListDetailedAsync(string contains = null);

        /// <summary>Deletes a saved view by name for the current user. Returns true when removed.</summary>
        Task<bool> DeleteByNameAsync(string name);
    }
}
