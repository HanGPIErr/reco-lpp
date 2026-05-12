using System.Collections.Generic;

namespace RecoTool.Services
{
    /// <summary>
    /// Abstraction over <see cref="UserFilterService"/> so that ViewModels and
    /// other services can be mocked in unit tests without touching OleDb.
    /// </summary>
    public interface IUserFilterService
    {
        /// <summary>Saves (insert or update) a named WHERE clause for the current user.</summary>
        void SaveUserFilter(string name, string whereClause);

        /// <summary>Loads the saved WHERE clause for the given filter name. Null when not found.</summary>
        string LoadUserFilterWhere(string name);

        /// <summary>Returns all known filter names ordered alphabetically.</summary>
        IList<string> ListUserFilterNames();

        /// <summary>Returns filter names matching the optional substring filter.</summary>
        IList<string> ListUserFilterNames(string contains);

        /// <summary>Returns (Name, CreatedBy) tuples optionally filtered by substring on name.</summary>
        IList<(string Name, string CreatedBy)> ListUserFiltersDetailed(string contains = null);

        /// <summary>Deletes a filter by name. Returns true when at least one row was removed.</summary>
        bool DeleteUserFilter(string name);
    }
}
