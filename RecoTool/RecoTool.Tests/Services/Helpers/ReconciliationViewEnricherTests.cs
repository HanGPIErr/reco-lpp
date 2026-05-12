using System;
using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Services.DTOs;
using RecoTool.Services.Helpers;
using Xunit;

namespace RecoTool.Tests.Services.Helpers
{
    /// <summary>
    /// Tests pour <see cref="ReconciliationViewEnricher"/>. Couvre les liens DWINGS
    /// (par BGI receivable, BGPMT, fallback heuristique BGI/BGPMT), le retry des
    /// receivables non liés, le calcul de GroupBalance et les MissingAmounts.
    /// </summary>
    public class ReconciliationViewEnricherTests
    {
        private static ReconciliationViewData Row(
            string id = null,
            string accountId = null,
            string accountSide = null,
            decimal amount = 0,
            string dwingsInvoiceId = null,
            string dwingsBgpmt = null,
            string dwingsGuaranteeId = null,
            string receivableInvoiceFromAmbre = null,
            string paymentReference = null,
            string internalRef = null,
            string reconciliationNum = null,
            string rawLabel = null)
        {
            return new ReconciliationViewData
            {
                ID = id ?? Guid.NewGuid().ToString("N"),
                Account_ID = accountId,
                AccountSide = accountSide,
                SignedAmount = amount,
                DWINGS_InvoiceID = dwingsInvoiceId,
                DWINGS_BGPMT = dwingsBgpmt,
                DWINGS_GuaranteeID = dwingsGuaranteeId,
                Receivable_InvoiceFromAmbre = receivableInvoiceFromAmbre,
                PaymentReference = paymentReference,
                InternalInvoiceReference = internalRef,
                Reconciliation_Num = reconciliationNum,
                RawLabel = rawLabel
            };
        }

        // ===== EnrichWithDwingsInvoices =====

        [Fact]
        public void Enrich_LinksByReceivableInvoiceFromAmbre_StrictRule()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI1" };
            var rows = new List<ReconciliationViewData>
            {
                Row(receivableInvoiceFromAmbre: "BGI1")
            };

            ReconciliationViewEnricher.EnrichWithDwingsInvoices(rows, new[] { inv });

            rows[0].DWINGS_InvoiceID.Should().Be("BGI1");
        }

        [Fact]
        public void Enrich_LinksByDirectDwingsInvoiceId()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI2" };
            var rows = new List<ReconciliationViewData>
            {
                Row(dwingsInvoiceId: "BGI2")
            };
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(rows, new[] { inv });
            rows[0].INVOICE_ID.Should().Be("BGI2");
        }

        [Fact]
        public void Enrich_LinksByPaymentReferenceBgpmt()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI3", BGPMT = "BGPMTABCD1234" };
            var rows = new List<ReconciliationViewData>
            {
                Row(paymentReference: "BGPMTABCD1234")
            };
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(rows, new[] { inv });
            rows[0].INVOICE_ID.Should().Be("BGI3");
        }

        [Fact]
        public void Enrich_LinksByDwingsBgpmt_AndBackfillsPaymentReference()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI4", BGPMT = "BGPMTABCD9999" };
            var rows = new List<ReconciliationViewData>
            {
                Row(dwingsBgpmt: "BGPMTABCD9999")
            };
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(rows, new[] { inv });
            rows[0].INVOICE_ID.Should().Be("BGI4");
            rows[0].PaymentReference.Should().Be("BGPMTABCD9999");
        }

        [Fact]
        public void Enrich_HeuristicBgiFromReconciliationNum()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI20231024E1F84" };
            var rows = new List<ReconciliationViewData>
            {
                Row(reconciliationNum: "free text BGI20231024E1F84 here")
            };
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(rows, new[] { inv });
            rows[0].INVOICE_ID.Should().Be("BGI20231024E1F84");
            rows[0].DWINGS_InvoiceID.Should().Be("BGI20231024E1F84");
        }

        [Fact]
        public void Enrich_NoMatch_LeavesRowUntouched()
        {
            var rows = new List<ReconciliationViewData>
            {
                Row(rawLabel: "no tokens")
            };
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(rows, new[] { new DwingsInvoiceDto() });
            rows[0].DWINGS_InvoiceID.Should().BeNull();
            rows[0].INVOICE_ID.Should().BeNull();
        }

        [Fact]
        public void Enrich_NullInputs_DoesNothing()
        {
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(null, new[] { new DwingsInvoiceDto() });
            ReconciliationViewEnricher.EnrichWithDwingsInvoices(new List<ReconciliationViewData>(), null);
            // pas d'exception
        }

        // ===== RetryUnlinkedReceivableBgi =====

        [Fact]
        public void RetryUnlinkedReceivableBgi_LinksWhenInvoiceIsAvailable()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI42" };
            var rows = new List<ReconciliationViewData>
            {
                Row(receivableInvoiceFromAmbre: "BGI42") // pas de DWINGS_InvoiceID
            };
            var linked = ReconciliationViewEnricher.RetryUnlinkedReceivableBgi(rows, new[] { inv });
            linked.Should().Be(1);
            rows[0].DWINGS_InvoiceID.Should().Be("BGI42");
            rows[0].INVOICE_ID.Should().Be("BGI42");
        }

        [Fact]
        public void RetryUnlinkedReceivableBgi_NoUnlinked_ReturnsZero()
        {
            var rows = new List<ReconciliationViewData>
            {
                Row(receivableInvoiceFromAmbre: "BGI42", dwingsInvoiceId: "BGI42")
            };
            ReconciliationViewEnricher.RetryUnlinkedReceivableBgi(rows, new[] { new DwingsInvoiceDto { INVOICE_ID = "BGI42" } })
                .Should().Be(0);
        }

        [Fact]
        public void RetryUnlinkedReceivableBgi_NullInputs_ReturnsZero()
        {
            ReconciliationViewEnricher.RetryUnlinkedReceivableBgi(null, new[] { new DwingsInvoiceDto() })
                .Should().Be(0);
            ReconciliationViewEnricher.RetryUnlinkedReceivableBgi(new List<ReconciliationViewData>(), null)
                .Should().Be(0);
        }

        // ===== ComputeAndApplyGroupBalances =====

        [Fact]
        public void ComputeAndApplyGroupBalances_GroupsByInternalRef_FirstThenBgpmt()
        {
            var rows = new List<ReconciliationViewData>
            {
                Row(internalRef: "REF1", amount: 100m),
                Row(internalRef: "REF1", amount: -50m),
                Row(dwingsBgpmt: "BGPMT1", amount: 30m),
                Row(dwingsBgpmt: "BGPMT1", amount: -10m),
                Row(amount: 999m), // ni interne ni BGPMT → balance null
            };
            ReconciliationViewEnricher.ComputeAndApplyGroupBalances(rows);

            rows[0].GroupBalance.Should().Be(50m);
            rows[1].GroupBalance.Should().Be(50m);
            rows[2].GroupBalance.Should().Be(20m);
            rows[3].GroupBalance.Should().Be(20m);
            rows[4].GroupBalance.Should().BeNull();
        }

        [Fact]
        public void ComputeAndApplyGroupBalances_DeletedRowsExcluded()
        {
            var deletedRow = Row(internalRef: "REF1", amount: 1000m);
            deletedRow.DeleteDate = DateTime.Now;

            var rows = new List<ReconciliationViewData>
            {
                deletedRow,
                Row(internalRef: "REF1", amount: 50m),
            };
            ReconciliationViewEnricher.ComputeAndApplyGroupBalances(rows);
            // Le row supprimé n'a pas de groupe → null
            rows[0].GroupBalance.Should().BeNull();
            // Le row vivant ne voit que son propre montant
            rows[1].GroupBalance.Should().Be(50m);
        }

        [Fact]
        public void ComputeAndApplyGroupBalances_NullOrEmpty_NoOp()
        {
            ReconciliationViewEnricher.ComputeAndApplyGroupBalances(null);
            ReconciliationViewEnricher.ComputeAndApplyGroupBalances(new List<ReconciliationViewData>());
        }

        // ===== CalculateMissingAmounts =====

        [Fact]
        public void CalculateMissingAmounts_BothSides_ComputesPerGroup()
        {
            var rows = new List<ReconciliationViewData>
            {
                Row(accountId: "RECV", amount: -100m, dwingsInvoiceId: "INV1"),
                Row(accountId: "PIVOT", amount: 90m, dwingsInvoiceId: "INV1"),
                Row(accountId: "PIVOT", amount: 5m, dwingsInvoiceId: "INV1"),
            };
            ReconciliationViewEnricher.CalculateMissingAmounts(rows, "RECV", "PIVOT");

            rows[0].MissingAmount.Should().Be(-5m); // -100 + 95
            rows[1].MissingAmount.Should().Be(-5m);
            rows[2].MissingAmount.Should().Be(-5m);

            // Counterpart counts
            rows[0].CounterpartCount.Should().Be(2);
            rows[1].CounterpartCount.Should().Be(1);
        }

        [Fact]
        public void CalculateMissingAmounts_GroupWithOnlyOneSide_NotComputed()
        {
            var rows = new List<ReconciliationViewData>
            {
                Row(accountId: "PIVOT", amount: 100m, dwingsInvoiceId: "INV1"),
                Row(accountId: "PIVOT", amount: 50m, dwingsInvoiceId: "INV1"),
            };
            ReconciliationViewEnricher.CalculateMissingAmounts(rows, "RECV", "PIVOT");
            rows.Should().AllSatisfy(r => r.MissingAmount.Should().BeNull());
        }

        [Fact]
        public void CalculateMissingAmounts_MissingArguments_NoOp()
        {
            var rows = new List<ReconciliationViewData> { Row(amount: 1m, dwingsInvoiceId: "X") };
            ReconciliationViewEnricher.CalculateMissingAmounts(rows, "", "");
            rows[0].MissingAmount.Should().BeNull();
        }
    }
}
