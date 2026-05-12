using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Services.Helpers;
using Xunit;

namespace RecoTool.Tests.Services.Helpers
{
    /// <summary>
    /// Tests pour <see cref="AccountSideCalculator"/>.
    /// Utilise un type POCO local pour éviter la dépendance à ReconciliationViewData.
    /// </summary>
    public class AccountSideCalculatorTests
    {
        private class Row
        {
            public string AccountId { get; set; }
            public string AccountSide { get; set; }
            public string DwingsInvoiceId { get; set; }
            public string InternalRef { get; set; }
            public bool IsMatched { get; set; }
            public double Amount { get; set; }
            public double? Missing { get; set; }
        }

        // ===== AssignAccountSides =====

        [Fact]
        public void AssignAccountSides_SetsPivotAndReceivableSides()
        {
            var rows = new List<Row>
            {
                new Row { AccountId = "P1" },
                new Row { AccountId = "R1" },
                new Row { AccountId = "OTHER" },
                new Row { AccountId = null },
            };
            AccountSideCalculator.AssignAccountSides(
                rows, "P1", "R1",
                r => r.AccountId,
                (r, s) => r.AccountSide = s);

            rows[0].AccountSide.Should().Be("P");
            rows[1].AccountSide.Should().Be("R");
            rows[2].AccountSide.Should().BeNull();
            rows[3].AccountSide.Should().BeNull();
        }

        [Fact]
        public void AssignAccountSides_TrimAndCaseInsensitiveOnAccountId()
        {
            var rows = new List<Row> { new Row { AccountId = "  p1  " } };
            AccountSideCalculator.AssignAccountSides(
                rows, "P1", "R1",
                r => r.AccountId,
                (r, s) => r.AccountSide = s);
            rows[0].AccountSide.Should().Be("P");
        }

        [Fact]
        public void AssignAccountSides_NullOrEmptyRows_NoOp()
        {
            AccountSideCalculator.AssignAccountSides<Row>(
                null, "P", "R", r => null, (r, s) => { });
            AccountSideCalculator.AssignAccountSides<Row>(
                new List<Row>(), "P", "R", r => null, (r, s) => { });
            // pas d'exception
        }

        [Fact]
        public void AssignAccountSides_BothPivotAndRecvIdNullOrEmpty_NoOp()
        {
            var rows = new List<Row> { new Row { AccountId = "P1" } };
            AccountSideCalculator.AssignAccountSides(
                rows, "  ", null,
                r => r.AccountId,
                (r, s) => r.AccountSide = s);
            rows[0].AccountSide.Should().BeNull();
        }

        // ===== ComputeMatchedAcrossAccounts =====

        [Fact]
        public void ComputeMatchedAcrossAccounts_BothSidesShareInvoice_IsMatchedTrue()
        {
            var rows = new List<Row>
            {
                new Row { AccountSide = "P", DwingsInvoiceId = "INV1" },
                new Row { AccountSide = "R", DwingsInvoiceId = "INV1" }
            };
            AccountSideCalculator.ComputeMatchedAcrossAccounts(
                rows,
                r => r.AccountSide,
                r => r.DwingsInvoiceId,
                r => r.InternalRef,
                (r, m) => r.IsMatched = m);

            rows[0].IsMatched.Should().BeTrue();
            rows[1].IsMatched.Should().BeTrue();
        }

        [Fact]
        public void ComputeMatchedAcrossAccounts_OnlyOneSide_NotMatched()
        {
            var rows = new List<Row>
            {
                new Row { AccountSide = "P", DwingsInvoiceId = "INV1" },
                new Row { AccountSide = "P", DwingsInvoiceId = "INV1" } // 2 pivots, pas de receivable
            };
            AccountSideCalculator.ComputeMatchedAcrossAccounts(
                rows,
                r => r.AccountSide,
                r => r.DwingsInvoiceId,
                r => r.InternalRef,
                (r, m) => r.IsMatched = m);

            rows.Should().AllSatisfy(r => r.IsMatched.Should().BeFalse());
        }

        [Fact]
        public void ComputeMatchedAcrossAccounts_GroupsByInternalReferenceToo()
        {
            var rows = new List<Row>
            {
                new Row { AccountSide = "P", InternalRef = "REF1" },
                new Row { AccountSide = "R", InternalRef = "REF1" },
            };
            AccountSideCalculator.ComputeMatchedAcrossAccounts(
                rows,
                r => r.AccountSide,
                r => r.DwingsInvoiceId,
                r => r.InternalRef,
                (r, m) => r.IsMatched = m);

            rows.Should().AllSatisfy(r => r.IsMatched.Should().BeTrue());
        }

        [Fact]
        public void ComputeMatchedAcrossAccounts_FallbackKey_AppliedIfProvided()
        {
            var rows = new List<Row>
            {
                new Row { AccountSide = "P", DwingsInvoiceId = "X" }, // pas de groupe explicite
                new Row { AccountSide = "R", DwingsInvoiceId = "X" },
            };
            AccountSideCalculator.ComputeMatchedAcrossAccounts(
                rows,
                r => r.AccountSide,
                r => null,           // pas de DWINGS_InvoiceID utilisé
                r => null,           // pas d'InternalRef
                (r, m) => r.IsMatched = m,
                getFallbackKey: r => r.DwingsInvoiceId); // fallback sur DwingsInvoiceId

            rows.Should().AllSatisfy(r => r.IsMatched.Should().BeTrue());
        }

        // ===== ComputeMissingAmounts =====

        [Fact]
        public void ComputeMissingAmounts_SumsAcrossSides()
        {
            var rows = new List<Row>
            {
                new Row { AccountSide = "R", DwingsInvoiceId = "INV1", Amount = -100 },
                new Row { AccountSide = "P", DwingsInvoiceId = "INV1", Amount = 90 },
                new Row { AccountSide = "P", DwingsInvoiceId = "INV1", Amount = 5 },
            };
            // Pré-condition: tous matched=true
            foreach (var r in rows) r.IsMatched = true;

            AccountSideCalculator.ComputeMissingAmounts(
                rows,
                isMatched: r => r.IsMatched,
                getDwingsInvoiceId: r => r.DwingsInvoiceId,
                getInternalInvoiceRef: r => r.InternalRef,
                getAccountSide: r => r.AccountSide,
                getSignedAmount: r => r.Amount,
                setMissingAmount: (r, v) => r.Missing = v);

            // Total = -100 + 90 + 5 = -5
            rows.Should().AllSatisfy(r => r.Missing.Should().Be(-5d));
        }

        [Fact]
        public void ComputeMissingAmounts_OnlyMatchedRows_AreConsidered()
        {
            var rows = new List<Row>
            {
                new Row { AccountSide = "R", DwingsInvoiceId = "X", Amount = -100, IsMatched = false },
                new Row { AccountSide = "P", DwingsInvoiceId = "X", Amount = 100, IsMatched = false },
            };
            AccountSideCalculator.ComputeMissingAmounts(
                rows,
                r => r.IsMatched,
                r => r.DwingsInvoiceId,
                r => r.InternalRef,
                r => r.AccountSide,
                r => r.Amount,
                (r, v) => r.Missing = v);

            rows.Should().AllSatisfy(r => r.Missing.Should().BeNull());
        }

        // ===== ExtractFallbackBgiKey =====

        [Fact]
        public void ExtractFallbackBgiKey_PrefersDwingsInvoiceId()
        {
            AccountSideCalculator.ExtractFallbackBgiKey(
                dwingsInvoiceId: " bgi1 ",
                receivableInvoiceFromAmbre: "RECV",
                reconciliationNum: null,
                comments: null,
                rawLabel: null,
                receivableDwRef: null,
                internalInvoiceRef: "INTERNAL")
                .Should().Be("BGI1");
        }

        [Fact]
        public void ExtractFallbackBgiKey_FallsBackToReceivableInvoiceFromAmbre()
        {
            AccountSideCalculator.ExtractFallbackBgiKey(
                null, "rcv1", null, null, null, null, null)
                .Should().Be("RCV1");
        }

        [Fact]
        public void ExtractFallbackBgiKey_ExtractsBgiTokenFromTextFields()
        {
            var key = AccountSideCalculator.ExtractFallbackBgiKey(
                dwingsInvoiceId: null,
                receivableInvoiceFromAmbre: null,
                reconciliationNum: "ref BGI20231024E1F84 end",
                comments: null,
                rawLabel: null,
                receivableDwRef: null,
                internalInvoiceRef: null);
            key.Should().Be("BGI20231024E1F84");
        }

        [Fact]
        public void ExtractFallbackBgiKey_FallsBackToInternalRef()
        {
            AccountSideCalculator.ExtractFallbackBgiKey(
                null, null, null, null, null, null, "  internal-x  ")
                .Should().Be("INTERNAL-X");
        }

        [Fact]
        public void ExtractFallbackBgiKey_AllNull_ReturnsNull()
        {
            AccountSideCalculator.ExtractFallbackBgiKey(
                null, null, null, null, null, null, null).Should().BeNull();
        }
    }
}
