using System;
using System.Data.OleDb;
using FluentAssertions;
using RecoTool.Infrastructure.DataAccess;
using RecoTool.IntegrationTests.Fixtures;
using Xunit;

namespace RecoTool.IntegrationTests.DataAccess
{
    /// <summary>
    /// Tests d'intégration sur ouverture de connexion ACE OLE DB.
    /// Skippés automatiquement si Microsoft Access Database Engine n'est pas installé.
    /// </summary>
    public class OleDbConnectionTests : IClassFixture<TempAccessDbFixture>
    {
        private readonly TempAccessDbFixture _fx;

        public OleDbConnectionTests(TempAccessDbFixture fx)
        {
            _fx = fx;
        }

        [SkippableFact]
        public void Open_NewlyCreatedDb_Succeeds()
        {
            Skip.IfNot(AccessAvailable.AnyAce, AccessAvailable.SkipReasonOrNull);
            Skip.IfNot(_fx.Created, "Access DB fixture could not create a temp database (ADOX missing).");

            using (var c = new OleDbConnection(_fx.ConnectionString))
            {
                Action a = () => c.Open();
                a.Should().NotThrow();
                c.State.Should().Be(System.Data.ConnectionState.Open);
            }
        }

        [SkippableFact]
        public void DbConn_ResolveConnectionString_AcceptsRealAccdbFile()
        {
            Skip.IfNot(AccessAvailable.AnyAce, AccessAvailable.SkipReasonOrNull);
            Skip.IfNot(_fx.Created, "Access DB fixture could not create a temp database (ADOX missing).");

            var cs = DbConn.ResolveConnectionString(_fx.Path);
            cs.Should().Contain("Provider=Microsoft.ACE.OLEDB.12.0");

            using (var c = new OleDbConnection(cs))
            {
                Action a = () => c.Open();
                a.Should().NotThrow();
            }
        }
    }
}
