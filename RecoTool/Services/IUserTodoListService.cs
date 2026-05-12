using System.Collections.Generic;
using System.Threading.Tasks;
using RecoTool.Models;

namespace RecoTool.Services
{
    /// <summary>
    /// Abstraction over <see cref="UserTodoListService"/> so that ViewModels can be
    /// mocked in unit tests without touching OleDb.
    /// </summary>
    public interface IUserTodoListService
    {
        /// <summary>Ensures the underlying T_Ref_TodoList table exists and has all required columns.</summary>
        Task<bool> EnsureTableAsync();

        /// <summary>Lists all active TodoList items shared across countries.</summary>
        Task<List<TodoListItem>> ListAsync(string countryId);

        /// <summary>Inserts or updates a TodoList item. Returns the row identifier.</summary>
        Task<int> UpsertAsync(TodoListItem item);

        /// <summary>Deletes a TodoList item by primary key.</summary>
        Task<bool> DeleteAsync(int id);
    }
}
