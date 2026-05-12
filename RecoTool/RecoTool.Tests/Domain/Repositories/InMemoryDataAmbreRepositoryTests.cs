using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Domain.Repositories
{
    /// <summary>
    /// Tests for <see cref="InMemoryDataAmbreRepository"/> — verifies the test double honours
    /// the <c>IDataAmbreRepository</c> contract: empty inputs return empty lists (never null),
    /// soft-deleted rows are excluded by default, and partitioning by country id is case-insensitive.
    /// </summary>
    public class InMemoryDataAmbreRepositoryTests
    {
        private static DataAmbre Row(string id, string account, decimal amount, DateTime? opDate = null, DateTime? deleted = null)
            => new DataAmbre
            {
                ID = id,
                Account_ID = account,
                CCY = "EUR",
                Country = "FR",
                SignedAmount = amount,
                LocalSignedAmount = amount,
                Operation_Date = opDate ?? new DateTime(2024, 1, 1),
                DeleteDate = deleted
            };

        [Fact]
        public async Task GetAllAsync_returns_seeded_rows_ordered_by_operation_date()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                Row("a", "100", 10m, new DateTime(2024, 3, 1)),
                Row("b", "100", 20m, new DateTime(2024, 1, 1)),
                Row("c", "100", 30m, new DateTime(2024, 2, 1)),
            });

            var result = await repo.GetAllAsync("FR");

            result.Should().NotBeNull();
            result.Should().HaveCount(3);
            result[0].ID.Should().Be("b");
            result[1].ID.Should().Be("c");
            result[2].ID.Should().Be("a");
        }

        [Fact]
        public async Task GetAllAsync_excludes_soft_deleted_rows_by_default()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                Row("a", "100", 10m),
                Row("b", "100", 20m, deleted: new DateTime(2024, 5, 1)),
            });

            var live = await repo.GetAllAsync("FR");
            live.Should().HaveCount(1);
            live[0].ID.Should().Be("a");

            var all = await repo.GetAllAsync("FR", includeDeleted: true);
            all.Should().HaveCount(2);
        }

        [Fact]
        public async Task GetAllAsync_returns_empty_for_unknown_country()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("a", "100", 10m) });

            var result = await repo.GetAllAsync("DE");

            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetAllAsync_returns_empty_when_country_is_null_or_blank()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("a", "100", 10m) });

            (await repo.GetAllAsync(null)).Should().NotBeNull().And.BeEmpty();
            (await repo.GetAllAsync("")).Should().NotBeNull().And.BeEmpty();
            (await repo.GetAllAsync("   ")).Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public async Task GetByIdAsync_finds_row_case_insensitively_on_id()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("ABC", "100", 10m) });

            var hit = await repo.GetByIdAsync("FR", "abc");

            hit.Should().NotBeNull();
            hit.ID.Should().Be("ABC");
        }

        [Fact]
        public async Task GetByIdAsync_returns_null_for_missing_or_deleted_row()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                Row("a", "100", 10m),
                Row("b", "100", 20m, deleted: new DateTime(2024, 5, 1)),
            });

            (await repo.GetByIdAsync("FR", "zzz")).Should().BeNull();
            (await repo.GetByIdAsync("FR", "b")).Should().BeNull();
            (await repo.GetByIdAsync("FR", null)).Should().BeNull();
            (await repo.GetByIdAsync(null, "a")).Should().BeNull();
        }

        [Fact]
        public async Task GetByAccountAsync_filters_by_account_and_excludes_deleted()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                Row("a", "100", 10m, new DateTime(2024, 1, 1)),
                Row("b", "200", 20m, new DateTime(2024, 1, 2)),
                Row("c", "100", 30m, new DateTime(2024, 1, 3)),
                Row("d", "100", 40m, deleted: new DateTime(2024, 5, 1)),
            });

            var result = await repo.GetByAccountAsync("FR", "100");

            result.Should().HaveCount(2);
            result[0].ID.Should().Be("a");
            result[1].ID.Should().Be("c");
        }

        [Fact]
        public async Task GetByAccountAsync_returns_empty_for_blank_inputs()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("a", "100", 10m) });

            (await repo.GetByAccountAsync("FR", null)).Should().NotBeNull().And.BeEmpty();
            (await repo.GetByAccountAsync("FR", "")).Should().NotBeNull().And.BeEmpty();
            (await repo.GetByAccountAsync(null, "100")).Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public async Task Seed_with_null_clears_country()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("a", "100", 10m) });
            repo.Seed("FR", null);

            var result = await repo.GetAllAsync("FR");
            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task Cancellation_token_is_honoured()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("a", "100", 10m) });
            var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = async () => await repo.GetAllAsync("FR", false, cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
