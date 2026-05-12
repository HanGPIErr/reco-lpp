using System.Reflection;
using FluentAssertions;
using Xunit;

namespace RecoTool.Tests.Services.Queries
{
    /// <summary>
    /// Tests pour ReconciliationViewQueryBuilder (internal). Utilise reflection
    /// car la classe est marquée 'internal' (et il n'y a pas d'InternalsVisibleTo).
    /// </summary>
    public class ReconciliationViewQueryBuilderTests
    {
        private static MethodInfo BuildMethod()
        {
            var asm = typeof(RecoTool.Services.LookupService).Assembly;
            var t = asm.GetType("RecoTool.Services.Queries.ReconciliationViewQueryBuilder", throwOnError: true);
            return t.GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        private static string Invoke(string dwEsc, string ambreEsc, string filterSql)
        {
            return (string)BuildMethod().Invoke(null, new object[] { dwEsc, ambreEsc, filterSql });
        }

        [Fact]
        public void Build_LocalDb_UsesPlainTableName()
        {
            var sql = Invoke(null, null, null);
            sql.Should().Contain("FROM (T_Data_Ambre AS a");
            sql.Should().Contain("LEFT JOIN T_Reconciliation AS r");
        }

        [Fact]
        public void Build_NetworkAmbreDb_UsesEscapedSubselectAlias()
        {
            var sql = Invoke(null, @"\\srv\share\AMBRE.accdb", null);
            sql.Should().Contain(@"(SELECT * FROM [\\srv\share\AMBRE.accdb].T_Data_Ambre) AS a");
            sql.Should().Contain(@"[\\srv\share\AMBRE.accdb].T_Data_Ambre");
        }

        [Fact]
        public void Build_AlwaysIncludesIsPotentialDuplicateColumn()
        {
            Invoke(null, null, null).Should().Contain("AS IsPotentialDuplicate");
        }

        [Fact]
        public void Build_AlwaysSelectsRecoColumns()
        {
            var sql = Invoke(null, null, null);
            sql.Should().Contain("r.DWINGS_GuaranteeID");
            sql.Should().Contain("r.DWINGS_InvoiceID");
            sql.Should().Contain("r.DWINGS_BGPMT");
            sql.Should().Contain("r.RemainingAmount");
            sql.Should().Contain("r.ModifiedBy AS Reco_ModifiedBy");
        }

        [Fact]
        public void Build_EndsWithWhereOnePlaceholder()
        {
            // L'appelant ajoute la clause WHERE additionnelle
            Invoke(null, null, null).Should().EndWith("WHERE 1=1");
        }
    }
}
