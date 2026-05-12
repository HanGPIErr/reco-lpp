using System;
using System.Globalization;
using FluentAssertions;
using RecoTool.Domain.Filters;
using Xunit;

namespace RecoTool.Tests.Domain.Filters
{
    /// <summary>
    /// Tests pour <see cref="FilterBuilder"/> — vérifie la génération de la clause WHERE
    /// (Access-compatible) à partir d'un <see cref="FilterState"/>. Couvre les filtres
    /// simples, l'échappement des apostrophes, les plages de dates, le filtre de montant
    /// avec/sans tolérance, le mappage UI→DB des types de garantie et les modes Status.
    /// </summary>
    public class FilterBuilderTests
    {
        private static CultureInfo _origCulture;

        public FilterBuilderTests()
        {
            // Le builder utilise InvariantCulture pour formater les nombres ; on force
            // une culture locale exotique pour s'assurer que la formation reste invariante.
            _origCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        }

        // ----- cas simples -----

        [Fact]
        public void BuildWhere_NullFilter_ReturnsEmpty()
        {
            FilterBuilder.BuildWhere(null).Should().Be(string.Empty);
        }

        [Fact]
        public void BuildWhere_EmptyFilter_ReturnsEmpty()
        {
            FilterBuilder.BuildWhere(new FilterState()).Should().Be(string.Empty);
        }

        [Fact]
        public void BuildWhere_AccountId_AddsEqualityClause()
        {
            var f = new FilterState { AccountId = "ACC1" };
            FilterBuilder.BuildWhere(f).Should().Be("WHERE Account_ID = 'ACC1'");
        }

        [Fact]
        public void BuildWhere_EscapesSingleQuotes()
        {
            var f = new FilterState { AccountId = "O'Reilly" };
            FilterBuilder.BuildWhere(f).Should().Be("WHERE Account_ID = 'O''Reilly'");
        }

        [Fact]
        public void BuildWhere_Currency_AddsEqualityClause()
        {
            var f = new FilterState { Currency = "EUR" };
            FilterBuilder.BuildWhere(f).Should().Contain("CCY = 'EUR'");
        }

        // ----- filtre montant -----

        [Fact]
        public void BuildWhere_Amount_NoTolerance_UsesEquality()
        {
            var f = new FilterState { Amount = 1234.56m, AmountWithTolerance = false };
            FilterBuilder.BuildWhere(f).Should().Be("WHERE SignedAmount = 1234.56");
        }

        [Fact]
        public void BuildWhere_Amount_WithTolerance_UsesRange()
        {
            var f = new FilterState { Amount = 100m, AmountWithTolerance = true };
            FilterBuilder.BuildWhere(f).Should()
                .Be("WHERE SignedAmount >= 99 AND SignedAmount <= 101");
        }

        [Fact]
        public void BuildWhere_Amount_UsesInvariantCulture_ForDecimalPoint()
        {
            // Sous fr-FR, le séparateur décimal est ',' — la requête doit néanmoins utiliser '.'
            var f = new FilterState { Amount = 12.34m };
            FilterBuilder.BuildWhere(f).Should().Contain("SignedAmount = 12.34");
        }

        // ----- dates -----

        [Fact]
        public void BuildWhere_OperationDate_BuildsDayRange_AndIgnoresFromTo()
        {
            var f = new FilterState
            {
                OperationDate = new DateTime(2024, 5, 1, 14, 30, 0),
                FromDate = new DateTime(2024, 1, 1),
                ToDate = new DateTime(2024, 12, 31),
            };
            var got = FilterBuilder.BuildWhere(f);
            got.Should().Contain("Operation_Date >= #2024-05-01#");
            got.Should().Contain("Operation_Date < #2024-05-02#");
            got.Should().NotContain("#2024-01-01#"); // From ignored
            got.Should().NotContain("#2024-12-31#"); // To ignored
        }

        [Fact]
        public void BuildWhere_FromAndToDate_BuildsRangeClauses()
        {
            var f = new FilterState
            {
                FromDate = new DateTime(2024, 1, 1),
                ToDate = new DateTime(2024, 1, 31)
            };
            var got = FilterBuilder.BuildWhere(f);
            got.Should().Contain("Operation_Date >= #2024-01-01#");
            got.Should().Contain("Operation_Date <= #2024-01-31#");
        }

        [Fact]
        public void BuildWhere_DeletedDate_BuildsDayRange()
        {
            var f = new FilterState { DeletedDate = new DateTime(2024, 6, 15) };
            FilterBuilder.BuildWhere(f).Should()
                .Contain("a.DeleteDate >= #2024-06-15# AND a.DeleteDate < #2024-06-16#");
        }

        [Fact]
        public void BuildWhere_ActionDate_BuildsDayRange()
        {
            var f = new FilterState { ActionDate = new DateTime(2024, 6, 1) };
            FilterBuilder.BuildWhere(f).Should()
                .Contain("r.ActionDate >= #2024-06-01# AND r.ActionDate < #2024-06-02#");
        }

        [Fact]
        public void BuildWhere_RemindDate_BuildsDayRange()
        {
            var f = new FilterState { RemindDate = new DateTime(2024, 6, 20) };
            FilterBuilder.BuildWhere(f).Should()
                .Contain("r.ToRemindDate >= #2024-06-20# AND r.ToRemindDate < #2024-06-21#");
        }

        // ----- LIKE patterns -----

        [Fact]
        public void BuildWhere_ReconciliationNum_UsesLikeWildcards()
        {
            var f = new FilterState { ReconciliationNum = "ABC" };
            FilterBuilder.BuildWhere(f).Should().Contain("a.Reconciliation_Num LIKE '%ABC%'");
        }

        [Fact]
        public void BuildWhere_RawLabel_UsesLike()
        {
            var f = new FilterState { RawLabel = "Foo" };
            FilterBuilder.BuildWhere(f).Should().Contain("RawLabel LIKE '%Foo%'");
        }

        [Fact]
        public void BuildWhere_DwRef_BuildsConsolidatedOr()
        {
            var f = new FilterState { DwRef = "X" };
            var got = FilterBuilder.BuildWhere(f);
            got.Should().Contain("r.DWINGS_InvoiceID LIKE '%X%'");
            got.Should().Contain("r.DWINGS_GuaranteeID LIKE '%X%'");
            got.Should().Contain("r.DWINGS_BGPMT LIKE '%X%'");
        }

        // ----- mappage UI→DB -----

        [Theory]
        [InlineData("REISSUANCE", "REISSU")]
        [InlineData("ISSUANCE", "ISSU")]
        [InlineData("ADVISING", "NOTIF")]
        [InlineData("OTHER_VALUE", "OTHER_VALUE")] // pas de mapping → conservé
        public void BuildWhere_GuaranteeType_AppliesUiToDbMapping(string ui, string db)
        {
            var f = new FilterState { GuaranteeType = ui };
            FilterBuilder.BuildWhere(f).Should().Contain($"GUARANTEE_TYPE = '{db}'");
        }

        [Fact]
        public void BuildWhere_GuaranteeStatus_UsesLike()
        {
            var f = new FilterState { GuaranteeStatus = "ACTIVE" };
            FilterBuilder.BuildWhere(f).Should().Contain("GUARANTEE_STATUS LIKE '%ACTIVE%'");
        }

        // ----- Status enum -----

        [Fact]
        public void BuildWhere_Status_Matched()
        {
            var f = new FilterState { Status = "Matched" };
            FilterBuilder.BuildWhere(f).Should().Contain("DWINGS_GuaranteeID Is Not Null");
        }

        [Fact]
        public void BuildWhere_Status_Unmatched()
        {
            var f = new FilterState { Status = "Unmatched" };
            FilterBuilder.BuildWhere(f).Should().Contain("DWINGS_GuaranteeID Is Null");
        }

        [Fact]
        public void BuildWhere_Status_Live()
        {
            var f = new FilterState { Status = "Live" };
            FilterBuilder.BuildWhere(f).Should().Contain("a.DeleteDate IS NULL");
        }

        [Fact]
        public void BuildWhere_Status_Archived()
        {
            var f = new FilterState { Status = "Archived" };
            FilterBuilder.BuildWhere(f).Should().Contain("a.DeleteDate IS NOT NULL");
        }

        [Fact]
        public void BuildWhere_Status_Unknown_IsIgnored()
        {
            var f = new FilterState { Status = "GIBBERISH" };
            FilterBuilder.BuildWhere(f).Should().Be(string.Empty);
        }

        // ----- IDs entiers -----

        [Fact]
        public void BuildWhere_ActionId_AddsEqualityClause()
        {
            var f = new FilterState { ActionId = 7 };
            FilterBuilder.BuildWhere(f).Should().Contain("r.Action = 7");
        }

        [Fact]
        public void BuildWhere_KpiId_AddsEqualityClause()
        {
            var f = new FilterState { KpiId = 12 };
            FilterBuilder.BuildWhere(f).Should().Contain("r.KPI = 12");
        }

        [Fact]
        public void BuildWhere_IncidentTypeId_AddsEqualityClause()
        {
            var f = new FilterState { IncidentTypeId = 3 };
            FilterBuilder.BuildWhere(f).Should().Contain("r.IncidentType = 3");
        }

        // ----- ActionDone / ToRemind -----

        [Fact]
        public void BuildWhere_ActionDone_True_AddsEqTrue()
        {
            FilterBuilder.BuildWhere(new FilterState { ActionDone = true })
                .Should().Contain("r.ActionStatus = TRUE");
        }

        [Fact]
        public void BuildWhere_ActionDone_False_AllowsFalseOrNull()
        {
            FilterBuilder.BuildWhere(new FilterState { ActionDone = false })
                .Should().Contain("(r.ActionStatus = FALSE OR r.ActionStatus IS NULL)");
        }

        [Fact]
        public void BuildWhere_ToRemind_True()
        {
            FilterBuilder.BuildWhere(new FilterState { ToRemind = true })
                .Should().Contain("r.ToRemind = TRUE");
        }

        [Fact]
        public void BuildWhere_ToRemind_False()
        {
            FilterBuilder.BuildWhere(new FilterState { ToRemind = false })
                .Should().Contain("(r.ToRemind = FALSE OR r.ToRemind IS NULL)");
        }

        // ----- Combinaison -----

        [Fact]
        public void BuildWhere_MultipleCriteria_JoinedByAnd()
        {
            var f = new FilterState
            {
                AccountId = "A",
                Currency = "EUR",
                Status = "Live"
            };
            var got = FilterBuilder.BuildWhere(f);
            got.Should().StartWith("WHERE ");
            got.Should().Contain(" AND ");
            got.Should().Contain("Account_ID = 'A'");
            got.Should().Contain("CCY = 'EUR'");
            got.Should().Contain("a.DeleteDate IS NULL");
        }
    }
}
