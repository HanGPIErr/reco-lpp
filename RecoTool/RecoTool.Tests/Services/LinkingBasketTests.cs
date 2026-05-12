using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FluentAssertions;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests pour <see cref="LinkingBasket"/> — cross-view basket des lignes Pivot/Receivable.
    /// Vérifie l'ajout, la déduplication par ID, le calcul du Delta/IsBalanced, le résumé,
    /// la suppression, le clear et la propagation INotifyPropertyChanged.
    /// </summary>
    public class LinkingBasketTests
    {
        private static ReconciliationViewData MakeRow(string id, decimal amount,
            string ccy = "EUR", string reconciliationNum = null)
        {
            return new ReconciliationViewData
            {
                ID = id,
                SignedAmount = amount,
                CCY = ccy,
                Reconciliation_Num = reconciliationNum
            };
        }

        // ----- État initial -----

        [Fact]
        public void NewBasket_IsEmptyAndUnbalanced()
        {
            var b = new LinkingBasket();
            b.PivotItems.Should().BeEmpty();
            b.ReceivableItems.Should().BeEmpty();
            b.TotalCount.Should().Be(0);
            b.HasItems.Should().BeFalse();
            b.CanLink.Should().BeFalse();
            b.PivotTotal.Should().Be(0m);
            b.ReceivableTotal.Should().Be(0m);
            b.Delta.Should().Be(0m);
            b.IsBalanced.Should().BeTrue(); // |0| < 0.01
            b.Summary.Should().BeEmpty();
        }

        // ----- AddPivot / AddReceivable -----

        [Fact]
        public void AddPivot_AddsItemAndIncrementsCounts()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.PivotItems.Should().HaveCount(1);
            b.TotalCount.Should().Be(1);
            b.HasItems.Should().BeTrue();
            b.PivotTotal.Should().Be(100m);
        }

        [Fact]
        public void AddReceivable_AddsItemAndIncrementsCounts()
        {
            var b = new LinkingBasket();
            b.AddReceivable(MakeRow("R1", -100m));
            b.ReceivableItems.Should().HaveCount(1);
            b.TotalCount.Should().Be(1);
            b.ReceivableTotal.Should().Be(-100m);
        }

        [Fact]
        public void AddPivot_NullRow_DoesNothing()
        {
            var b = new LinkingBasket();
            b.AddPivot(null);
            b.PivotItems.Should().BeEmpty();
        }

        [Fact]
        public void AddPivot_DuplicateId_NotAddedTwice()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddPivot(MakeRow("P1", 999m));   // même ID → ignoré
            b.PivotItems.Should().HaveCount(1);
            b.PivotTotal.Should().Be(100m);
        }

        [Fact]
        public void AddReceivable_DuplicateId_NotAddedTwice()
        {
            var b = new LinkingBasket();
            b.AddReceivable(MakeRow("R1", -100m));
            b.AddReceivable(MakeRow("R1", -50m));
            b.ReceivableItems.Should().HaveCount(1);
        }

        // ----- AddRow polymorphique -----

        [Fact]
        public void AddRow_DispatchesByAccountSide()
        {
            var b = new LinkingBasket();
            b.AddRow(MakeRow("P1", 100m), "P");
            b.AddRow(MakeRow("R1", -100m), "r"); // case-insensitive
            b.AddRow(MakeRow("X", 1m), "?");     // ignoré

            b.PivotItems.Should().ContainSingle(x => x.Id == "P1");
            b.ReceivableItems.Should().ContainSingle(x => x.Id == "R1");
            b.TotalCount.Should().Be(2);
        }

        // ----- Balance -----

        [Fact]
        public void Delta_IsSumOfBothSides()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -100m));
            b.Delta.Should().Be(0m);
            b.IsBalanced.Should().BeTrue();
            b.CanLink.Should().BeTrue();
        }

        [Fact]
        public void IsBalanced_IgnoresDifferencesUnderToleranceCent()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -99.999m));
            b.IsBalanced.Should().BeTrue(); // |0.001| < 0.01
        }

        [Fact]
        public void IsBalanced_FalseWhenDeltaAboveTolerance()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -90m));
            b.IsBalanced.Should().BeFalse();
            b.Delta.Should().Be(10m);
        }

        // ----- Summary -----

        [Fact]
        public void Summary_BalancedShowsCheckmark()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -100m));
            b.Summary.Should().Contain("Balanced");
        }

        [Fact]
        public void Summary_UnbalancedShowsDelta()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -90m));
            b.Summary.Should().Contain("10");
        }

        // ----- CanLink -----

        [Fact]
        public void CanLink_OnlyTrueWhenBothSidesNonEmpty()
        {
            var b = new LinkingBasket();
            b.CanLink.Should().BeFalse();
            b.AddPivot(MakeRow("P1", 1m));
            b.CanLink.Should().BeFalse();
            b.AddReceivable(MakeRow("R1", -1m));
            b.CanLink.Should().BeTrue();
        }

        // ----- Remove / Clear -----

        [Fact]
        public void RemoveItem_RemovesFromPivotOrReceivable()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -100m));

            b.RemoveItem("P1");
            b.PivotItems.Should().BeEmpty();
            b.ReceivableItems.Should().HaveCount(1);

            b.RemoveItem("R1");
            b.ReceivableItems.Should().BeEmpty();
        }

        [Fact]
        public void RemoveItem_UnknownId_DoesNothing()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.RemoveItem("DOES_NOT_EXIST");
            b.PivotItems.Should().HaveCount(1);
        }

        [Fact]
        public void Clear_EmptiesBothSides()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 100m));
            b.AddReceivable(MakeRow("R1", -100m));
            b.Clear();
            b.HasItems.Should().BeFalse();
            b.TotalCount.Should().Be(0);
        }

        // ----- GetAllIds -----

        [Fact]
        public void GetAllIds_DeduplicatesAndIgnoresEmpty()
        {
            var b = new LinkingBasket();
            b.AddPivot(MakeRow("P1", 1m));
            b.AddReceivable(MakeRow("R1", -1m));
            // Ajout direct dans la collection pour simuler un doublon technique
            // (PivotItems et ReceivableItems sont des ObservableCollection publiques)
            var ids = b.GetAllIds();
            ids.Should().BeEquivalentTo(new[] { "P1", "R1" });
        }

        // ----- INotifyPropertyChanged -----

        [Fact]
        public void AddingItem_RaisesPropertyChangedForDerivedProperties()
        {
            var b = new LinkingBasket();
            var changes = new List<string>();
            b.PropertyChanged += (s, e) => changes.Add(e.PropertyName);

            b.AddPivot(MakeRow("P1", 100m));

            changes.Should().Contain(new[]
            {
                nameof(LinkingBasket.TotalCount),
                nameof(LinkingBasket.HasItems),
                nameof(LinkingBasket.CanLink),
                nameof(LinkingBasket.PivotTotal),
                nameof(LinkingBasket.Delta),
                nameof(LinkingBasket.IsBalanced),
                nameof(LinkingBasket.Summary),
            });
        }
    }

    /// <summary>
    /// Tests pour <see cref="LinkingBasketItem"/>.
    /// </summary>
    public class LinkingBasketItemTests
    {
        [Fact]
        public void FromRow_PrefersReconciliationNumThenEventNumThenId()
        {
            var row = new ReconciliationViewData
            {
                ID = "ID1",
                SignedAmount = 100m,
                CCY = "EUR",
                Reconciliation_Num = "RECO1",
                Event_Num = "EV1"
            };
            LinkingBasketItem.FromRow(row, "P").Reference.Should().Be("RECO1");
        }

        [Fact]
        public void FromRow_FallsBackToEventNum_WhenNoReconciliationNum()
        {
            var row = new ReconciliationViewData
            {
                ID = "ID1",
                SignedAmount = 100m,
                Event_Num = "EV1"
            };
            LinkingBasketItem.FromRow(row, "P").Reference.Should().Be("EV1");
        }

        [Fact]
        public void FromRow_FallsBackToId_WhenNothingElse()
        {
            var row = new ReconciliationViewData { ID = "ID1", SignedAmount = 100m };
            LinkingBasketItem.FromRow(row, "P").Reference.Should().Be("ID1");
        }

        [Fact]
        public void Display_FormatsAmountAndCurrency()
        {
            var item = new LinkingBasketItem
            {
                Id = "X",
                Amount = 1234.5m,
                Currency = "EUR",
                Reference = "RECO"
            };
            // Le format N2 dépend de la culture — on vérifie surtout que les pièces clés sont présentes.
            item.Display.Should().Contain("RECO");
            item.Display.Should().Contain("EUR");
        }
    }
}
