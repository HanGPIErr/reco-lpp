using System;
using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Models
{
    /// <summary>
    /// Tests pour <see cref="Reconciliation"/>.
    /// </summary>
    public class ReconciliationTests
    {
        [Fact]
        public void CreateForAmbreLine_AssignsIdOnly()
        {
            var r = Reconciliation.CreateForAmbreLine("LINE_42");
            r.ID.Should().Be("LINE_42");
            r.Action.Should().BeNull();
            r.KPI.Should().BeNull();
            r.ACK.Should().BeFalse();
        }

        [Fact]
        public void HasDWINGSData_TrueWhenAnyDwingsFieldFilled()
        {
            new Reconciliation { DWINGS_GuaranteeID = "G" }.HasDWINGSData.Should().BeTrue();
            new Reconciliation { DWINGS_InvoiceID = "BGI" }.HasDWINGSData.Should().BeTrue();
            new Reconciliation { DWINGS_BGPMT = "BGPMT" }.HasDWINGSData.Should().BeTrue();
        }

        [Fact]
        public void HasDWINGSData_FalseWhenAllNullOrEmpty()
        {
            new Reconciliation().HasDWINGSData.Should().BeFalse();
            new Reconciliation { DWINGS_GuaranteeID = "", DWINGS_InvoiceID = "", DWINGS_BGPMT = "" }
                .HasDWINGSData.Should().BeFalse();
        }

        // Régression documentaire : HasDWINGSData utilise IsNullOrEmpty (pas IsNullOrWhiteSpace),
        // donc une chaîne contenant uniquement des espaces blancs est considérée comme "ayant
        // de la donnée DWINGS". Si ce comportement est durci un jour (passage à WhiteSpace),
        // ce test échouera et signalera la régression.
        [Fact]
        public void HasDWINGSData_TrueWhenWhitespaceOnly_DocumentsCurrentBehavior()
        {
            new Reconciliation { DWINGS_InvoiceID = "  " }.HasDWINGSData.Should().BeTrue();
        }

        [Fact]
        public void RequiresReminder_TrueWhenToRemindAndDateInPast()
        {
            var r = new Reconciliation
            {
                ToRemind = true,
                ToRemindDate = DateTime.Today.AddDays(-1)
            };
            r.RequiresReminder.Should().BeTrue();
        }

        [Fact]
        public void RequiresReminder_TrueWhenToRemindDateIsToday()
        {
            var r = new Reconciliation
            {
                ToRemind = true,
                ToRemindDate = DateTime.Today
            };
            r.RequiresReminder.Should().BeTrue();
        }

        [Fact]
        public void RequiresReminder_FalseWhenToRemindFlagOff()
        {
            new Reconciliation { ToRemind = false, ToRemindDate = DateTime.Today.AddDays(-2) }
                .RequiresReminder.Should().BeFalse();
        }

        [Fact]
        public void RequiresReminder_FalseWhenDateInFuture()
        {
            new Reconciliation { ToRemind = true, ToRemindDate = DateTime.Today.AddDays(1) }
                .RequiresReminder.Should().BeFalse();
        }

        [Fact]
        public void RequiresReminder_FalseWhenNoDate()
        {
            new Reconciliation { ToRemind = true, ToRemindDate = null }
                .RequiresReminder.Should().BeFalse();
        }

        [Fact]
        public void IsRiskyEffective_TrueOnlyWhenRiskyItemTrue()
        {
            new Reconciliation { RiskyItem = true }.IsRiskyEffective.Should().BeTrue();
            new Reconciliation { RiskyItem = false }.IsRiskyEffective.Should().BeFalse();
            new Reconciliation { RiskyItem = null }.IsRiskyEffective.Should().BeFalse();
        }

        [Fact]
        public void InheritsBaseEntity_VersionStartsAtOne()
        {
            var r = Reconciliation.CreateForAmbreLine("X");
            r.Version.Should().Be(1);
            r.IsDeleted.Should().BeFalse();
        }
    }
}
