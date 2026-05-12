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
    /// List-backed test double for <see cref="IDataAmbreRepository"/>. Use it in unit tests
    /// of consumers that depend on the repository — it requires no Access file and never
    /// touches I/O. Rows are partitioned by country id (case-insensitive).
    /// </summary>
    public sealed class InMemoryDataAmbreRepository : IDataAmbreRepository
    {
        private readonly Dictionary<string, List<DataAmbre>> _byCountry =
            new Dictionary<string, List<DataAmbre>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Seed the repository with rows for a given country. Replaces any previously seeded data.
        /// </summary>
        public void Seed(string countryId, IEnumerable<DataAmbre> rows)
        {
            if (string.IsNullOrWhiteSpace(countryId)) throw new ArgumentException("countryId is required", nameof(countryId));
            _byCountry[countryId] = (rows ?? Enumerable.Empty<DataAmbre>()).ToList();
        }

        public Task<IReadOnlyList<DataAmbre>> GetAllAsync(string countryId, bool includeDeleted = false, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(countryId) || !_byCountry.TryGetValue(countryId, out var rows))
                return Task.FromResult<IReadOnlyList<DataAmbre>>(Array.Empty<DataAmbre>());

            var query = includeDeleted ? rows : rows.Where(r => r.DeleteDate == null);
            var ordered = query
                .OrderBy(r => r.Operation_Date ?? DateTime.MinValue)
                .ToList();
            return Task.FromResult<IReadOnlyList<DataAmbre>>(ordered);
        }

        public Task<DataAmbre> GetByIdAsync(string countryId, string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(countryId) || string.IsNullOrWhiteSpace(id))
                return Task.FromResult<DataAmbre>(null);
            if (!_byCountry.TryGetValue(countryId, out var rows))
                return Task.FromResult<DataAmbre>(null);

            var found = rows.FirstOrDefault(r =>
                r.DeleteDate == null &&
                string.Equals(r.ID, id, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<DataAmbre>> GetByAccountAsync(string countryId, string accountId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(countryId) || string.IsNullOrWhiteSpace(accountId))
                return Task.FromResult<IReadOnlyList<DataAmbre>>(Array.Empty<DataAmbre>());
            if (!_byCountry.TryGetValue(countryId, out var rows))
                return Task.FromResult<IReadOnlyList<DataAmbre>>(Array.Empty<DataAmbre>());

            var filtered = rows
                .Where(r => r.DeleteDate == null
                         && string.Equals(r.Account_ID, accountId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Operation_Date ?? DateTime.MinValue)
                .ToList();
            return Task.FromResult<IReadOnlyList<DataAmbre>>(filtered);
        }
    }
}
