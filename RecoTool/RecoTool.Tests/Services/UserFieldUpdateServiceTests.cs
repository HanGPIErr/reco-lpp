using System;
using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests pour <see cref="UserFieldUpdateService"/> — classe statique pure
    /// qui synchronise <see cref="ReconciliationViewData"/> et <see cref="Reconciliation"/>
    /// lors d'éditions de Action/KPI/IncidentType/ActionStatus.
    /// </summary>
    public class UserFieldUpdateServiceTests
    {
        private static (ReconciliationViewData row, Reconciliation reco) MakePair()
        {
            return (new ReconciliationViewData { ID = "X" }, new Reconciliation { ID = "X" });
        }

        // ===== IsActionNA =====

        [Fact]
        public void IsActionNA_NullId_ReturnsTrue()
        {
            UserFieldUpdateService.IsActionNA(null, new List<UserField>()).Should().BeTrue();
        }

        [Fact]
        public void IsActionNA_NullFields_ReturnsFalse()
        {
            UserFieldUpdateService.IsActionNA(5, null).Should().BeFalse();
        }

        [Fact]
        public void IsActionNA_UnknownId_ReturnsFalse()
        {
            var fields = new List<UserField> { new UserField { USR_ID = 1, USR_FieldName = "MATCH" } };
            UserFieldUpdateService.IsActionNA(99, fields).Should().BeFalse();
        }

        [Theory]
        [InlineData("N/A")]
        [InlineData("NA")]
        [InlineData("Not Applicable")]
        [InlineData("NOT APPLICABLE")]
        [InlineData("not applicable")]
        public void IsActionNA_VariousNALabels_ReturnsTrue(string label)
        {
            var fields = new List<UserField> { new UserField { USR_ID = 1, USR_FieldName = label } };
            UserFieldUpdateService.IsActionNA(1, fields).Should().BeTrue();
        }

        [Fact]
        public void IsActionNA_NonNALabel_ReturnsFalse()
        {
            var fields = new List<UserField> { new UserField { USR_ID = 1, USR_FieldName = "MATCH" } };
            UserFieldUpdateService.IsActionNA(1, fields).Should().BeFalse();
        }

        [Fact]
        public void IsActionNA_FieldWithEmptyName_ReturnsFalse()
        {
            var fields = new List<UserField> { new UserField { USR_ID = 1, USR_FieldName = "" } };
            UserFieldUpdateService.IsActionNA(1, fields).Should().BeFalse();
        }

        // ===== ApplyAction =====

        [Fact]
        public void ApplyAction_NullId_ResetsStatusAndDate()
        {
            var (row, reco) = MakePair();
            row.ActionStatus = true;
            row.ActionDate = new DateTime(2024, 1, 1);
            reco.ActionStatus = true;
            reco.ActionDate = new DateTime(2024, 1, 1);

            UserFieldUpdateService.ApplyAction(row, reco, null, new List<UserField>());

            row.Action.Should().BeNull();
            row.ActionStatus.Should().BeNull();
            row.ActionDate.Should().BeNull();
            reco.Action.Should().BeNull();
            reco.ActionStatus.Should().BeNull();
            reco.ActionDate.Should().BeNull();
        }

        [Fact]
        public void ApplyAction_NAAction_ResetsStatusAndDate()
        {
            var (row, reco) = MakePair();
            row.ActionStatus = true;
            row.ActionDate = new DateTime(2024, 1, 1);
            var fields = new List<UserField> { new UserField { USR_ID = 1, USR_FieldName = "N/A" } };

            UserFieldUpdateService.ApplyAction(row, reco, 1, fields);

            row.Action.Should().Be(1);
            row.ActionStatus.Should().BeNull();
            row.ActionDate.Should().BeNull();
            reco.Action.Should().Be(1);
            reco.ActionStatus.Should().BeNull();
        }

        [Fact]
        public void ApplyAction_RegularAction_SetsPendingAndStampsDate()
        {
            var (row, reco) = MakePair();
            var fields = new List<UserField> { new UserField { USR_ID = 5, USR_FieldName = "MATCH" } };

            var before = DateTime.Now.AddSeconds(-1);
            UserFieldUpdateService.ApplyAction(row, reco, 5, fields);
            var after = DateTime.Now.AddSeconds(1);

            row.Action.Should().Be(5);
            row.ActionStatus.Should().BeFalse(); // PENDING
            row.ActionDate.Should().NotBeNull();
            row.ActionDate.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

            reco.Action.Should().Be(5);
            reco.ActionStatus.Should().BeFalse();
            reco.ActionDate.Should().Be(row.ActionDate);
        }

        // ===== ApplyActionStatus =====

        [Fact]
        public void ApplyActionStatus_NewStatusDifferent_StampsDate()
        {
            var (row, reco) = MakePair();
            row.ActionStatus = false;
            UserFieldUpdateService.ApplyActionStatus(row, reco, true);

            row.ActionStatus.Should().BeTrue();
            row.ActionDate.Should().NotBeNull();
            reco.ActionStatus.Should().BeTrue();
            reco.ActionDate.Should().Be(row.ActionDate);
        }

        [Fact]
        public void ApplyActionStatus_SameStatus_DoesNotChangeDate()
        {
            var oldDate = new DateTime(2024, 1, 1);
            var (row, reco) = MakePair();
            row.ActionStatus = true;
            row.ActionDate = oldDate;

            UserFieldUpdateService.ApplyActionStatus(row, reco, true);

            row.ActionStatus.Should().BeTrue();
            row.ActionDate.Should().Be(oldDate, "le statut n'a pas changé → la date est préservée");
        }

        [Fact]
        public void ApplyActionStatus_NullStatus_ClearsDate()
        {
            var (row, reco) = MakePair();
            row.ActionStatus = true;
            row.ActionDate = DateTime.Now;

            UserFieldUpdateService.ApplyActionStatus(row, reco, null);

            row.ActionStatus.Should().BeNull();
            row.ActionDate.Should().BeNull();
            reco.ActionStatus.Should().BeNull();
            reco.ActionDate.Should().BeNull();
        }

        // ===== ApplyKpi / ApplyIncidentType =====

        [Fact]
        public void ApplyKpi_SetsBothRowAndReco()
        {
            var (row, reco) = MakePair();
            UserFieldUpdateService.ApplyKpi(row, reco, 18);
            row.KPI.Should().Be(18);
            reco.KPI.Should().Be(18);
        }

        [Fact]
        public void ApplyKpi_NullClearsBoth()
        {
            var (row, reco) = MakePair();
            row.KPI = 5; reco.KPI = 5;
            UserFieldUpdateService.ApplyKpi(row, reco, null);
            row.KPI.Should().BeNull();
            reco.KPI.Should().BeNull();
        }

        [Fact]
        public void ApplyIncidentType_SetsBothRowAndReco()
        {
            var (row, reco) = MakePair();
            UserFieldUpdateService.ApplyIncidentType(row, reco, 24);
            row.IncidentType.Should().Be(24);
            reco.IncidentType.Should().Be(24);
        }
    }
}
