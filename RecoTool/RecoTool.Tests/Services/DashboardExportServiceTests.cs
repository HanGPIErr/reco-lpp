using System;
using System.Reflection;
using System.Runtime.Serialization;
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
    /// Repository-pattern migration POC #3: covers the path in
    /// <see cref="DashboardExportService"/> where the AMBRE snapshot date for the cover
    /// sheet is derived from <c>IDataAmbreRepository</c> when one is injected, instead
    /// of the legacy <c>ReconciliationService.GetLastAmbreOperationDateAsync</c> OleDb
    /// query. The private <c>TryGetAmbreSnapshotDateAsync</c> is reached via reflection
    /// because the full <see cref="DashboardExportService.ExportDashboardAsync"/>
    /// pipeline depends on a live Access database (the export's other phases load
    /// the enriched reconciliation view and write an .xlsx workbook).
    ///
    /// We bypass the constructor entirely (via <see cref="FormatterServices.GetUninitializedObject"/>)
    /// because <c>OfflineFirstService</c> needs a configured Settings.ReferentialDB at
    /// construction time, which a unit-test environment cannot provide. We then assign
    /// the repository field directly through reflection — sufficient to exercise the
    /// new repo-injected snapshot path without booting any other infrastructure.
    /// </summary>
    public class DashboardExportServiceTests
    {
        private const string CountryId = "FR";

        /// <summary>
        /// Builds a <see cref="DashboardExportService"/> with only the
        /// <c>_ambreRepo</c> field set (everything else is left at its CLR default).
        /// The test only invokes the private snapshot-date helper, which exclusively
        /// reads from <c>_ambreRepo</c> when non-null, so the other fields are never
        /// touched.
        /// </summary>
        private static DashboardExportService BuildSutWithRepo(RecoTool.Domain.Repositories.IDataAmbreRepository repo)
        {
            var sut = (DashboardExportService)FormatterServices.GetUninitializedObject(typeof(DashboardExportService));
            var repoField = typeof(DashboardExportService).GetField("_ambreRepo",
                BindingFlags.Instance | BindingFlags.NonPublic);
            repoField.Should().NotBeNull("the migration introduces a private _ambreRepo field");
            repoField.SetValue(sut, repo);
            return sut;
        }

        private static Task<DateTime?> InvokeSnapshotDate(DashboardExportService sut, string countryId)
        {
            var method = typeof(DashboardExportService).GetMethod(
                "TryGetAmbreSnapshotDateAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            method.Should().NotBeNull("the private snapshot-date helper must exist for this POC");
            return (Task<DateTime?>)method.Invoke(sut, new object[] { countryId, CancellationToken.None });
        }

        [Fact]
        public async Task TryGetAmbreSnapshotDate_UsesRepository_WhenInjected()
        {
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed(CountryId, new[]
            {
                new DataAmbre { ID = "a", Account_ID = "ACC", Operation_Date = new DateTime(2024, 3, 15) },
                new DataAmbre { ID = "b", Account_ID = "ACC", Operation_Date = new DateTime(2024, 5, 1) },
                new DataAmbre { ID = "c", Account_ID = "ACC", Operation_Date = new DateTime(2024, 1, 7) }
            });
            var sut = BuildSutWithRepo(repo);

            var result = await InvokeSnapshotDate(sut, CountryId);

            // The repo-injected path computes MAX(Operation_Date) in memory; legacy OleDb
            // path is bypassed entirely (its ReconciliationService field is even null here).
            result.Should().Be(new DateTime(2024, 5, 1));
        }

        [Fact]
        public async Task TryGetAmbreSnapshotDate_RepositoryEmpty_ReturnsNull()
        {
            var repo = new InMemoryDataAmbreRepository();
            // No seed for "FR" — repo returns an empty list, so MAX is undefined.
            var sut = BuildSutWithRepo(repo);

            var result = await InvokeSnapshotDate(sut, CountryId);

            result.Should().BeNull();
        }

        [Fact]
        public async Task TryGetAmbreSnapshotDate_RepositoryIgnoresDeletedRows()
        {
            // The repo MUST filter out soft-deleted rows by default — so the more-recent
            // deleted row should NOT be picked as the snapshot date.
            var repo = new InMemoryDataAmbreRepository();
            repo.Seed(CountryId, new[]
            {
                new DataAmbre { ID = "live", Account_ID = "ACC", Operation_Date = new DateTime(2024, 2, 1) },
                new DataAmbre { ID = "deleted", Account_ID = "ACC", Operation_Date = new DateTime(2024, 12, 31), DeleteDate = new DateTime(2025, 1, 1) }
            });
            var sut = BuildSutWithRepo(repo);

            var result = await InvokeSnapshotDate(sut, CountryId);

            result.Should().Be(new DateTime(2024, 2, 1));
        }

        [Fact]
        public void Ctor_PreservesOriginalSignature_AmbreRepoIsOptional()
        {
            // Lock in the public signature: existing call sites (e.g. MainWindow.xaml.cs,
            // HomePage.xaml.cs) construct the service with the legacy 2-arg form. The
            // new IDataAmbreRepository parameter MUST stay optional so those sites
            // compile unchanged. We don't actually call the ctor (OfflineFirstService
            // would need a configured Settings.ReferentialDB), we just confirm via
            // reflection that the parameter list still matches expectations.
            var ctors = typeof(DashboardExportService).GetConstructors();
            ctors.Should().HaveCount(1, "exactly one public ctor is exposed");
            var parameters = ctors[0].GetParameters();
            parameters.Should().HaveCount(4);
            parameters[0].ParameterType.Should().Be(typeof(ReconciliationService));
            parameters[1].ParameterType.Should().Be(typeof(OfflineFirstService));
            parameters[2].HasDefaultValue.Should().BeTrue("clock has been optional since the IClock rollout");
            parameters[3].ParameterType.Should().Be(typeof(RecoTool.Domain.Repositories.IDataAmbreRepository));
            parameters[3].HasDefaultValue.Should().BeTrue("the new repo parameter must be optional for back-compat");
            parameters[3].DefaultValue.Should().BeNull();
        }
    }
}
