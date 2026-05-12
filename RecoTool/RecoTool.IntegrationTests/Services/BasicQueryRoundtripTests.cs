using System;
using System.Data.OleDb;
using FluentAssertions;
using RecoTool.IntegrationTests.Fixtures;
using Xunit;

namespace RecoTool.IntegrationTests.Services
{
    /// <summary>
    /// Test de bout en bout très simple : créer une BDD Access, créer une table
    /// avec les colonnes attendues par les services AMBRE, insérer une ligne,
    /// la relire. Sert de "smoke test" pour valider que ACE OLE DB est OK
    /// avant d'enchainer avec les vrais services (qui s'attendent à une
    /// arborescence multi-fichiers — ces tests vivent dans un projet ad hoc).
    /// </summary>
    public class BasicQueryRoundtripTests : IClassFixture<TempAccessDbFixture>
    {
        private readonly TempAccessDbFixture _fx;
        private readonly bool _ready;

        public BasicQueryRoundtripTests(TempAccessDbFixture fx)
        {
            _fx = fx;
            _ready = AccessAvailable.AnyAce && _fx.Created;
        }

        [SkippableFact]
        public void CreateTableInsertSelect_RoundTrip()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            // 1) schéma
            _fx.ExecuteNonQuery(@"CREATE TABLE T_Mini
                                   (ID TEXT(40) PRIMARY KEY,
                                    Account_ID TEXT(50),
                                    SignedAmount CURRENCY,
                                    Operation_Date DATETIME)");

            // 2) insert
            using (var c = new OleDbConnection(_fx.ConnectionString))
            {
                c.Open();
                using (var cmd = new OleDbCommand(
                    "INSERT INTO T_Mini (ID, Account_ID, SignedAmount, Operation_Date) VALUES (?, ?, ?, ?)", c))
                {
                    cmd.Parameters.AddWithValue("@id", "ABC-123");
                    cmd.Parameters.AddWithValue("@acc", "PIVOT_FR");
                    cmd.Parameters.AddWithValue("@amt", 1234.56m);
                    cmd.Parameters.AddWithValue("@dt", new DateTime(2024, 5, 1));
                    cmd.ExecuteNonQuery();
                }
            }

            // 3) select
            using (var c = new OleDbConnection(_fx.ConnectionString))
            {
                c.Open();
                using (var cmd = new OleDbCommand("SELECT COUNT(*) FROM T_Mini WHERE Account_ID = ?", c))
                {
                    cmd.Parameters.AddWithValue("@acc", "PIVOT_FR");
                    var n = (int)cmd.ExecuteScalar();
                    n.Should().Be(1);
                }
            }
        }
    }
}
