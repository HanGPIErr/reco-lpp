using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services.Helpers;
using Xunit;

namespace RecoTool.Tests.Services.Helpers
{
    /// <summary>
    /// Tests pour <see cref="ReconciliationKpiCalculator.CalculateKpis"/>.
    /// Vérifie le regroupement par DWINGS_InvoiceID / InternalInvoiceReference,
    /// le calcul du MissingAmount et l'enrichissement des deux côtés.
    /// </summary>
    public class ReconciliationKpiCalculatorTests
    {
        private static ReconciliationKpiCalculator.ReconciliationStaging Item(
            string id, decimal amount, bool isPivot,
            string dwingsId = null, string internalRef = null)
        {
            return new ReconciliationKpiCalculator.ReconciliationStaging
            {
                DataAmbre = new DataAmbre { ID = id, SignedAmount = amount },
                Reconciliation = new Reconciliation
                {
                    ID = id,
                    DWINGS_InvoiceID = dwingsId,
                    InternalInvoiceReference = internalRef
                },
                IsPivot = isPivot
            };
        }

        [Fact]
        public void CalculateKpis_NullOrEmpty_NoOp()
        {
            ReconciliationKpiCalculator.CalculateKpis(null);
            ReconciliationKpiCalculator.CalculateKpis(new List<ReconciliationKpiCalculator.ReconciliationStaging>());
            // Pas d'exception
        }

        [Fact]
        public void CalculateKpis_BothSidesShareDwingsId_IsGroupedTrueAndMissingComputed()
        {
            var items = new List<ReconciliationKpiCalculator.ReconciliationStaging>
            {
                Item("R", -100, isPivot: false, dwingsId: "INV1"),
                Item("P", 90, isPivot: true, dwingsId: "INV1"),
            };
            ReconciliationKpiCalculator.CalculateKpis(items);

            items.Should().AllSatisfy(it => it.IsGrouped.Should().BeTrue());
            items.Should().AllSatisfy(it => it.MissingAmount.Should().Be(-10m)); // -100 + 90
        }

        [Fact]
        public void CalculateKpis_OnlyOneSide_NotGrouped()
        {
            var items = new List<ReconciliationKpiCalculator.ReconciliationStaging>
            {
                Item("P1", 50, isPivot: true, dwingsId: "INV1"),
                Item("P2", 50, isPivot: true, dwingsId: "INV1"),
            };
            ReconciliationKpiCalculator.CalculateKpis(items);

            items.Should().AllSatisfy(it => it.IsGrouped.Should().BeFalse());
            items.Should().AllSatisfy(it => it.MissingAmount.Should().BeNull());
        }

        [Fact]
        public void CalculateKpis_InternalRefTakesPriorityOverDwingsId()
        {
            // Même DWINGS_InvoiceID mais InternalRef différent → 2 groupes distincts
            var items = new List<ReconciliationKpiCalculator.ReconciliationStaging>
            {
                Item("R1", -100, isPivot: false, dwingsId: "INV1", internalRef: "RefA"),
                Item("P1", 100, isPivot: true, dwingsId: "INV1", internalRef: "RefA"),
                Item("R2", -200, isPivot: false, dwingsId: "INV1", internalRef: "RefB"),
                Item("P2", 200, isPivot: true, dwingsId: "INV1", internalRef: "RefB"),
            };
            ReconciliationKpiCalculator.CalculateKpis(items);

            // Group A = balanced
            items[0].MissingAmount.Should().Be(0m);
            items[1].MissingAmount.Should().Be(0m);
            // Group B = balanced
            items[2].MissingAmount.Should().Be(0m);
            items[3].MissingAmount.Should().Be(0m);
        }

        [Fact]
        public void CalculateKpis_NoInvoiceRef_NotGrouped()
        {
            var items = new List<ReconciliationKpiCalculator.ReconciliationStaging>
            {
                Item("X", 10, isPivot: true), // ni dwingsId ni internalRef
                Item("Y", -10, isPivot: false)
            };
            ReconciliationKpiCalculator.CalculateKpis(items);
            items.Should().AllSatisfy(it => it.IsGrouped.Should().BeFalse());
            items.Should().AllSatisfy(it => it.MissingAmount.Should().BeNull());
        }

        [Fact]
        public void CalculateKpis_CounterpartCountAndTotal_CorrectlyComputed()
        {
            var items = new List<ReconciliationKpiCalculator.ReconciliationStaging>
            {
                Item("R", -100, isPivot: false, dwingsId: "INV1"),
                Item("P1", 60, isPivot: true, dwingsId: "INV1"),
                Item("P2", 30, isPivot: true, dwingsId: "INV1"),
            };
            ReconciliationKpiCalculator.CalculateKpis(items);

            items[0].CounterpartCount.Should().Be(2);   // 2 pivots vu du receivable
            items[0].CounterpartTotalAmount.Should().Be(90m);

            items[1].CounterpartCount.Should().Be(1);   // 1 receivable vu d'un pivot
            items[1].CounterpartTotalAmount.Should().Be(-100m);
        }

        [Fact]
        public void CalculateKpis_CaseInsensitiveOnInvoiceId()
        {
            var items = new List<ReconciliationKpiCalculator.ReconciliationStaging>
            {
                Item("R", -50, isPivot: false, dwingsId: "inv1"),
                Item("P", 50, isPivot: true, dwingsId: "INV1"),
            };
            ReconciliationKpiCalculator.CalculateKpis(items);
            items.Should().AllSatisfy(it => it.IsGrouped.Should().BeTrue());
        }
    }
}
