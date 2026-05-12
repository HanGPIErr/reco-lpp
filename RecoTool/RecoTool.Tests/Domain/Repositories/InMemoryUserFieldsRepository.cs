using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RecoTool.Domain.Repositories;
using RecoTool.Models;

namespace RecoTool.Tests.Domain.Repositories
{
    /// <summary>
    /// List-backed test double for <see cref="IUserFieldsRepository"/>. The seeded list is
    /// considered the source of truth; lookups are pure LINQ over the in-memory store.
    /// </summary>
    public sealed class InMemoryUserFieldsRepository : IUserFieldsRepository
    {
        private List<UserField> _rows = new List<UserField>();

        /// <summary>
        /// Seed the repository with a new set of user fields. A null collection clears the store.
        /// </summary>
        public void Seed(IEnumerable<UserField> rows)
        {
            _rows = (rows ?? Enumerable.Empty<UserField>()).ToList();
        }

        public Task<IReadOnlyList<UserField>> GetAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var ordered = _rows
                .OrderBy(u => u.USR_Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(u => u.USR_FieldName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<UserField>>(ordered);
        }

        public Task<UserField> GetByIdAsync(int id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var found = _rows.FirstOrDefault(u => u.USR_ID == id);
            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<UserField>> GetByCategoryAsync(string category, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(category))
                return Task.FromResult<IReadOnlyList<UserField>>(Array.Empty<UserField>());

            var filtered = _rows
                .Where(u => string.Equals(u.USR_Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.USR_FieldName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Task.FromResult<IReadOnlyList<UserField>>(filtered);
        }
    }
}
