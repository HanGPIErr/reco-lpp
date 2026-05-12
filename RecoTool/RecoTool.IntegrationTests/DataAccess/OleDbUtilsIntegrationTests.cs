using System;
using System.Data.OleDb;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.IntegrationTests.Fixtures;
using Xunit;

namespace RecoTool.IntegrationTests.DataAccess
{
    /// <summary>
    /// Fixture spécifique : crée la BDD + le schéma + les fixtures UNE seule fois,
    /// pour que les tests de la classe partagent un état stable. Évite l'erreur
    /// "T_Sample existe déjà" causée par un seeding répété dans le constructeur de test.
    /// </summary>
    public sealed class OleDbUtilsAccessFixture : TempAccessDbFixture
    {
        public bool Ready { get; }

        public OleDbUtilsAccessFixture()
        {
            Ready = AccessAvailable.AnyAce && Created;
            if (!Ready) return;

            ExecuteNonQuery(@"CREATE TABLE T_Sample
                               (Id COUNTER PRIMARY KEY,
                                Label TEXT(50),
                                LastModified DATETIME,
                                [Version] LONG)");
            ExecuteNonQuery("INSERT INTO T_Sample (Label, LastModified, [Version]) VALUES ('A', NOW(), 1)");
            ExecuteNonQuery("INSERT INTO T_Sample (Label, LastModified, [Version]) VALUES ('B', NOW(), 5)");
            ExecuteNonQuery("INSERT INTO T_Sample (Label, LastModified, [Version]) VALUES ('C', NOW(), 3)");
        }
    }

    /// <summary>
    /// Tests d'intégration pour <see cref="OleDbUtils"/> contre une vraie BDD Access.
    /// </summary>
    public class OleDbUtilsIntegrationTests : IClassFixture<OleDbUtilsAccessFixture>, IDisposable
    {
        private readonly OleDbUtilsAccessFixture _fx;
        private readonly bool _ready;

        public OleDbUtilsIntegrationTests(OleDbUtilsAccessFixture fx)
        {
            _fx = fx;
            _ready = _fx.Ready;
        }

        public void Dispose() { /* fixture-managed */ }

        [SkippableFact]
        public async Task GetMaxVersionAsync_ReturnsLargestVersionAcrossRows()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            using (var c = new OleDbConnection(_fx.ConnectionString))
            {
                await c.OpenAsync();
                var max = await OleDbUtils.GetMaxVersionAsync(c, "T_Sample");
                max.Should().Be(5);
            }
        }

        [SkippableFact]
        public async Task GetMaxLastModifiedAsync_ReturnsRecentDate()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            using (var c = new OleDbConnection(_fx.ConnectionString))
            {
                await c.OpenAsync();
                var dt = await OleDbUtils.GetMaxLastModifiedAsync(c, "T_Sample");
                dt.Should().NotBeNull();
                dt.Value.Should().BeOnOrAfter(DateTime.Now.AddMinutes(-5));
            }
        }

        [SkippableFact]
        public async Task OpenWithTimeoutAsync_HappyPath_OpensConnection()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            using (var c = new OleDbConnection(_fx.ConnectionString))
            {
                await OleDbUtils.OpenWithTimeoutAsync(c, TimeSpan.FromSeconds(5), CancellationToken.None);
                c.State.Should().Be(System.Data.ConnectionState.Open);
            }
        }

        [SkippableFact]
        public async Task OpenConnectionWithTimeoutAsync_BadConnectionString_Throws()
        {
            Skip.IfNot(AccessAvailable.AnyAce, AccessAvailable.SkipReasonOrNull);

            // Provider valide mais fichier inexistant → l'open va échouer avec OleDbException
            var cs = $"Provider={AccessAvailable.PreferredProvider};Data Source=Z:\\does\\not\\exist.accdb;";
            Func<Task> act = async () =>
            {
                using (await OleDbUtils.OpenConnectionWithTimeoutAsync(cs, TimeSpan.FromSeconds(2), CancellationToken.None)) { }
            };
            await act.Should().ThrowAsync<Exception>();
        }
    }
}
