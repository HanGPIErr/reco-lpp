using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Domain.Repositories
{
    /// <summary>
    /// Tests for <see cref="InMemoryUserFieldsRepository"/> — verifies the test double honours
    /// the <c>IUserFieldsRepository</c> contract: empty / blank inputs return empty lists
    /// (never null), category lookup is case-insensitive, GetById returns null for misses.
    /// </summary>
    public class InMemoryUserFieldsRepositoryTests
    {
        private static UserField Field(int id, string category, string name, string description = null)
            => new UserField
            {
                USR_ID = id,
                USR_Category = category,
                USR_FieldName = name,
                USR_FieldDescription = description ?? name,
                USR_Pivot = true,
                USR_Receivable = true,
                USR_Color = "#000000"
            };

        [Fact]
        public async Task GetAllAsync_returns_empty_when_unseeded()
        {
            var repo = new InMemoryUserFieldsRepository();

            var result = await repo.GetAllAsync();

            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task GetAllAsync_orders_by_category_then_field_name()
        {
            var repo = new InMemoryUserFieldsRepository();
            repo.Seed(new[]
            {
                Field(3, "KPI",    "Paid"),
                Field(1, "Action", "Trigger"),
                Field(2, "Action", "Claim"),
                Field(4, "KPI",    "Open"),
            });

            var result = await repo.GetAllAsync();

            result.Should().HaveCount(4);
            result[0].USR_FieldName.Should().Be("Claim");
            result[1].USR_FieldName.Should().Be("Trigger");
            result[2].USR_FieldName.Should().Be("Open");
            result[3].USR_FieldName.Should().Be("Paid");
        }

        [Fact]
        public async Task GetByIdAsync_returns_match_or_null()
        {
            var repo = new InMemoryUserFieldsRepository();
            repo.Seed(new[] { Field(1, "Action", "Trigger"), Field(2, "KPI", "Open") });

            var hit = await repo.GetByIdAsync(1);
            hit.Should().NotBeNull();
            hit.USR_FieldName.Should().Be("Trigger");

            var miss = await repo.GetByIdAsync(999);
            miss.Should().BeNull();
        }

        [Fact]
        public async Task GetByCategoryAsync_filters_case_insensitively_and_orders_by_name()
        {
            var repo = new InMemoryUserFieldsRepository();
            repo.Seed(new[]
            {
                Field(1, "Action", "Trigger"),
                Field(2, "Action", "Claim"),
                Field(3, "KPI",    "Paid"),
            });

            var result = await repo.GetByCategoryAsync("aCtIoN");

            result.Should().HaveCount(2);
            result[0].USR_FieldName.Should().Be("Claim");
            result[1].USR_FieldName.Should().Be("Trigger");
        }

        [Fact]
        public async Task GetByCategoryAsync_returns_empty_for_blank_or_unknown_category()
        {
            var repo = new InMemoryUserFieldsRepository();
            repo.Seed(new[] { Field(1, "Action", "Trigger") });

            (await repo.GetByCategoryAsync(null)).Should().NotBeNull().And.BeEmpty();
            (await repo.GetByCategoryAsync("")).Should().NotBeNull().And.BeEmpty();
            (await repo.GetByCategoryAsync("   ")).Should().NotBeNull().And.BeEmpty();
            (await repo.GetByCategoryAsync("NonExisting")).Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public async Task Seed_with_null_clears_repository()
        {
            var repo = new InMemoryUserFieldsRepository();
            repo.Seed(new[] { Field(1, "Action", "Trigger") });
            repo.Seed(null);

            var result = await repo.GetAllAsync();
            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Fact]
        public async Task Cancellation_token_is_honoured()
        {
            var repo = new InMemoryUserFieldsRepository();
            repo.Seed(new[] { Field(1, "Action", "Trigger") });
            var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = async () => await repo.GetAllAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
