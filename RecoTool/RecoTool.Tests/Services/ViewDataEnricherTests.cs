using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests pour <see cref="ViewDataEnricher"/> — classe statique avec caches partagés.
    /// /!\ Les caches sont statiques → ces tests appellent <see cref="ViewDataEnricher.InvalidateCaches"/>
    /// dans leur ctor pour partir d'un état propre.
    /// </summary>
    public class ViewDataEnricherTests
    {
        public ViewDataEnricherTests()
        {
            // Reset entre tests pour éviter l'interférence
            ViewDataEnricher.InvalidateCaches();
        }

        // ===== EnrichRow / EnrichAll =====

        [Fact]
        public void EnrichAll_NullData_NoOp()
        {
            // Pas d'exception
            ViewDataEnricher.EnrichAll(null, new List<UserField>());
        }

        [Fact]
        public void EnrichRow_NullRow_NoOp()
        {
            ViewDataEnricher.EnrichRow(null);
        }

        [Fact]
        public void EnrichRow_LooksUpDisplayNameFromCache()
        {
            var fields = new List<UserField>
            {
                new UserField { USR_ID = 5, USR_FieldName = "MATCH", USR_Color = "GREEN" },
                new UserField { USR_ID = 18, USR_FieldName = "PAID", USR_Color = "BLUE" }
            };
            var row = new ReconciliationViewData { Action = 5, KPI = 18 };

            ViewDataEnricher.EnrichAll(new[] { row }, fields);

            row.ActionDisplayName.Should().Be("MATCH");
            row.KpiDisplayName.Should().Be("PAID");
        }

        [Fact]
        public void EnrichRow_UnknownActionId_DisplayNameEmpty()
        {
            ViewDataEnricher.EnrichAll(new[] { new ReconciliationViewData() }, new List<UserField>());

            var row = new ReconciliationViewData { Action = 999 };
            ViewDataEnricher.EnrichRow(row);
            row.ActionDisplayName.Should().Be(string.Empty);
        }

        [Fact]
        public void EnrichRow_ActionStatusTrue_BackgroundColorIsTransparent()
        {
            var fields = new List<UserField>
            {
                new UserField { USR_ID = 5, USR_FieldName = "MATCH", USR_Color = "GREEN" }
            };
            var row = new ReconciliationViewData { Action = 5, ActionStatus = true };
            ViewDataEnricher.EnrichAll(new[] { row }, fields);

            row.ActionBackgroundColor.Should().Be("Transparent",
                "DONE neutralizes the action color");
        }

        [Fact]
        public void EnrichRow_ActionStatusPending_BackgroundColorMappedFromUserFieldColor()
        {
            var fields = new List<UserField>
            {
                new UserField { USR_ID = 5, USR_FieldName = "MATCH", USR_Color = "GREEN" }
            };
            var row = new ReconciliationViewData { Action = 5, ActionStatus = false };
            ViewDataEnricher.EnrichAll(new[] { row }, fields);

            // GREEN → #C8E6C9 (cf. NormalizeColor)
            row.ActionBackgroundColor.Should().Be("#C8E6C9");
        }

        // ===== Cache lookups via TryGet* =====

        [Fact]
        public void TryGetUserFieldName_BeforeCachePrime_ReturnsNull()
        {
            ViewDataEnricher.TryGetUserFieldName(5).Should().BeNull();
        }

        [Fact]
        public void TryGetUserFieldName_AfterPriming_ReturnsCachedName()
        {
            var fields = new List<UserField>
            {
                new UserField { USR_ID = 5, USR_FieldName = "MATCH" }
            };
            ViewDataEnricher.EnrichAll(new[] { new ReconciliationViewData() }, fields);

            ViewDataEnricher.TryGetUserFieldName(5).Should().Be("MATCH");
            ViewDataEnricher.TryGetUserFieldName(999).Should().BeNull();
            ViewDataEnricher.TryGetUserFieldName(null).Should().BeNull();
        }

        [Fact]
        public void TryGetAssigneeName_BeforeCachePrime_ReturnsNull()
        {
            ViewDataEnricher.TryGetAssigneeName("alice").Should().BeNull();
            ViewDataEnricher.TryGetAssigneeName(null).Should().BeNull();
            ViewDataEnricher.TryGetAssigneeName("").Should().BeNull();
        }

        // ===== ApplyUserFieldAndRefresh =====

        [Fact]
        public void ApplyUserFieldAndRefresh_NullArgs_NoOp()
        {
            // Pas d'exception
            ViewDataEnricher.ApplyUserFieldAndRefresh(null, new Reconciliation(), 5, "Action", null);
            ViewDataEnricher.ApplyUserFieldAndRefresh(new ReconciliationViewData(), null, 5, "Action", null);
            ViewDataEnricher.ApplyUserFieldAndRefresh(new ReconciliationViewData(), new Reconciliation(), 5, "", null);
        }

        [Fact]
        public void ApplyUserFieldAndRefresh_KPI_SetsBothEntities()
        {
            var fields = new List<UserField>
            {
                new UserField { USR_ID = 18, USR_FieldName = "PAID" }
            };
            ViewDataEnricher.EnrichAll(new[] { new ReconciliationViewData() }, fields); // prime cache

            var row = new ReconciliationViewData();
            var reco = new Reconciliation();
            ViewDataEnricher.ApplyUserFieldAndRefresh(row, reco, 18, "KPI", fields);

            row.KPI.Should().Be(18);
            reco.KPI.Should().Be(18);
            row.KpiDisplayName.Should().Be("PAID");
        }

        [Fact]
        public void ApplyUserFieldAndRefresh_IncidentTypeAlias_RecognizedBothSpellings()
        {
            var row = new ReconciliationViewData();
            var reco = new Reconciliation();

            ViewDataEnricher.ApplyUserFieldAndRefresh(row, reco, 24, "Incident Type", null);
            row.IncidentType.Should().Be(24);

            ViewDataEnricher.ApplyUserFieldAndRefresh(row, reco, 25, "Incident", null);
            row.IncidentType.Should().Be(25);
        }

        [Fact]
        public void ApplyUserFieldAndRefresh_UnknownCategory_NoOp()
        {
            var row = new ReconciliationViewData();
            var reco = new Reconciliation();
            ViewDataEnricher.ApplyUserFieldAndRefresh(row, reco, 5, "TotallyUnknown", null);
            row.Action.Should().BeNull();
            row.KPI.Should().BeNull();
            row.IncidentType.Should().BeNull();
            row.ReasonNonRisky.Should().BeNull();
        }

        // ===== InvalidateCaches =====

        [Fact]
        public void InvalidateCaches_AfterPrime_ClearsLookups()
        {
            var fields = new List<UserField> { new UserField { USR_ID = 5, USR_FieldName = "MATCH" } };
            ViewDataEnricher.EnrichAll(new[] { new ReconciliationViewData() }, fields);
            ViewDataEnricher.TryGetUserFieldName(5).Should().Be("MATCH");

            ViewDataEnricher.InvalidateCaches();
            ViewDataEnricher.TryGetUserFieldName(5).Should().BeNull();
        }

        // ===== RefreshActionDisplay =====

        [Fact]
        public void RefreshActionDisplay_RefreshesWithCachedData()
        {
            var fields = new List<UserField>
            {
                new UserField { USR_ID = 5, USR_FieldName = "MATCH", USR_Color = "RED" }
            };
            ViewDataEnricher.EnrichAll(new[] { new ReconciliationViewData() }, fields);

            var row = new ReconciliationViewData { Action = 5, ActionStatus = false };
            ViewDataEnricher.RefreshActionDisplay(row);
            row.ActionDisplayName.Should().Be("MATCH");
            row.ActionBackgroundColor.Should().Be("#FFCDD2"); // RED → light red
        }
    }
}
