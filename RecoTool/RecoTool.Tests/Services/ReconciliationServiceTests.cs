using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Tests.Domain.Repositories;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests for <see cref="ReconciliationService"/> — focused on the repository-pattern
    /// migration of <see cref="ReconciliationService.GetAmbreDataAsync"/>.
    ///
    /// <para>
    /// When an <see cref="RecoTool.Domain.Repositories.IDataAmbreRepository"/> is injected,
    /// the service must delegate Ambre row loading to the repo — bypassing the legacy OleDb
    /// query path entirely. This is the highest-leverage win in the migration: all consumers
    /// that still call <c>ReconciliationService.GetAmbreDataAsync</c> as a fallback
    /// (<c>HomePageViewModel</c>, <c>ReconciliationMatchingService</c>,
    /// <c>DashboardExportService</c>) transparently route through the repo too.
    /// </para>
    /// </summary>
    public class ReconciliationServiceTests
    {
        private static DataAmbre Row(string id, string account, DateTime opDate, DateTime? deleted = null)
            => new DataAmbre
            {
                ID = id,
                Account_ID = account,
                CCY = "EUR",
                Country = "FR",
                SignedAmount = 100m,
                LocalSignedAmount = 100m,
                Operation_Date = opDate,
                DeleteDate = deleted
            };

        [Fact]
        public async Task GetAmbreData_UsesRepository_WhenInjected()
        {
            // Arrange — seed the repo for "FR" only.
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                Row("R1", "100", new DateTime(2024, 2, 1)),
                Row("R2", "100", new DateTime(2024, 1, 1)),
                Row("R3", "200", new DateTime(2024, 3, 1)),
            });

            // Construct the service WITHOUT a connection string and WITHOUT an OfflineFirstService —
            // the repo path must work end-to-end without touching OleDb. We use the base ctor
            // (5-arg variant) with ambreRepo as the LAST optional param.
            var svc = new ReconciliationService(
                connectionString: null,
                currentUser: "tester",
                countries: new List<Country>(),
                clock: null,
                logger: null,
                ambreRepo: repo);

            // Act
            var rows = await svc.GetAmbreDataAsync("FR", includeDeleted: false);

            // Assert — all 3 seeded rows come back, ordered by Operation_Date ASC.
            rows.Should().NotBeNull();
            rows.Should().HaveCount(3);
            rows.Select(r => r.ID).Should().ContainInOrder("R2", "R1", "R3");
        }

        [Fact]
        public async Task GetAmbreData_RepositoryPath_ExcludesDeletedByDefault()
        {
            // Arrange
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[]
            {
                Row("live", "100", new DateTime(2024, 1, 1)),
                Row("dead", "100", new DateTime(2024, 2, 1), deleted: new DateTime(2024, 5, 1)),
            });

            var svc = new ReconciliationService(
                connectionString: null,
                currentUser: "tester",
                countries: new List<Country>(),
                ambreRepo: repo);

            // Act — default includeDeleted=false must filter out the soft-deleted row.
            var liveOnly = await svc.GetAmbreDataAsync("FR");
            var withDeleted = await svc.GetAmbreDataAsync("FR", includeDeleted: true);

            // Assert
            liveOnly.Should().HaveCount(1);
            liveOnly[0].ID.Should().Be("live");

            withDeleted.Should().HaveCount(2);
        }

        [Fact]
        public async Task GetAmbreData_RepositoryPath_ReturnsEmpty_ForUnknownCountry()
        {
            // Arrange — repo only knows about "FR".
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed("FR", new[] { Row("R1", "100", new DateTime(2024, 1, 1)) });

            var svc = new ReconciliationService(
                connectionString: null,
                currentUser: "tester",
                countries: new List<Country>(),
                ambreRepo: repo);

            // Act
            var rows = await svc.GetAmbreDataAsync("DE");

            // Assert — InMemory repo contract: empty list (never null) for an unseeded country.
            rows.Should().NotBeNull();
            rows.Should().BeEmpty();
        }

        [Fact]
        public void Ctor_AmbreRepoIsOptional_DoesNotChangeExistingApi()
        {
            // Both ctors must remain callable WITHOUT the repo — existing call sites
            // (App.xaml.cs factory, direct constructions in tests) must keep compiling.
            var withoutRepo1 = new ReconciliationService(
                connectionString: null,
                currentUser: "tester",
                countries: new List<Country>());
            withoutRepo1.Should().NotBeNull();

            // Reflection-level sanity: the new ambreRepo parameter must be the LAST optional
            // parameter on BOTH constructors and must have a default value of null.
            var ctors = typeof(ReconciliationService).GetConstructors()
                .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(RecoTool.Domain.Repositories.IDataAmbreRepository)))
                .ToList();
            ctors.Should().HaveCount(2, "both ctors must accept the optional IDataAmbreRepository");
            foreach (var ctor in ctors)
            {
                var parms = ctor.GetParameters();
                parms[parms.Length - 1].ParameterType.Should().Be(typeof(RecoTool.Domain.Repositories.IDataAmbreRepository));
                parms[parms.Length - 1].HasDefaultValue.Should().BeTrue();
                parms[parms.Length - 1].DefaultValue.Should().BeNull();
            }
        }
    }
}
