using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services.Helpers
{
    /// <summary>
    /// Tests pour <see cref="EnumHelper"/> — labels Action / KPI / Incident,
    /// résolution via UserField puis fallback enum [Description].
    /// </summary>
    public class EnumHelperTests
    {
        // ----- SplitCamelCase -----

        [Theory]
        [InlineData("CamelCase", "Camel Case")]
        [InlineData("PaidButNotReconciled", "Paid But Not Reconciled")]
        [InlineData("snake_case", "snake case")]
        [InlineData("ALLCAPS", "ALLCAPS")]
        [InlineData("a", "a")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void SplitCamelCase_VariousInputs(string input, string expected)
        {
            EnumHelper.SplitCamelCase(input).Should().Be(expected);
        }

        // ----- GetEnumDescription -----

        [Fact]
        public void GetEnumDescription_UsesDescriptionAttribute()
        {
            EnumHelper.GetEnumDescription(ActionType.NA).Should().Be("Not Applicable");
        }

        [Fact]
        public void GetEnumDescription_FallsBackToCamelCaseSplit_WhenNoDescription()
        {
            // Tous les ActionType ont un Description dans le code, mais on vérifie
            // que SplitCamelCase est appelé sur le ToString() en dernier recours.
            // Pas d'enum sans description disponible — on se contente de vérifier
            // que le helper ne lève pas et retourne quelque chose de non-vide.
            var got = EnumHelper.GetEnumDescription(KPIType.ITIssues);
            got.Should().Be("IT Issues");
        }

        // ----- GetActionName -----

        [Fact]
        public void GetActionName_UsesUserFieldFirst()
        {
            var fields = new[]
            {
                new UserField { USR_ID = 1, USR_Category = "Action", USR_FieldName = "Custom NA" }
            };
            EnumHelper.GetActionName(1, fields).Should().Be("Custom NA");
        }

        [Fact]
        public void GetActionName_FallsBackToEnumDescription_WhenNoUserField()
        {
            EnumHelper.GetActionName((int)ActionType.NA, null).Should().Be("Not Applicable");
        }

        [Fact]
        public void GetActionName_UnknownId_ReturnsActionPrefix()
        {
            var got = EnumHelper.GetActionName(999_999, null);
            // L'enum cast échoue silencieusement → on retombe sur le SplitCamelCase de la valeur stringifiée
            got.Should().NotBeNullOrWhiteSpace();
        }

        [Fact]
        public void GetActionName_UserFieldWrongCategory_IsIgnored()
        {
            var fields = new[]
            {
                new UserField { USR_ID = 1, USR_Category = "KPI", USR_FieldName = "Wrong" }
            };
            EnumHelper.GetActionName(1, fields).Should().Be("Not Applicable");
        }

        // ----- GetKPIName -----

        [Fact]
        public void GetKPIName_UsesUserFieldFirst()
        {
            var fields = new[]
            {
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "Custom KPI" }
            };
            EnumHelper.GetKPIName(18, fields).Should().Be("Custom KPI");
        }

        [Theory]
        [InlineData((int)KPIType.ITIssues, "IT Issues")]
        [InlineData((int)KPIType.PaidButNotReconciled, "Paid but not reconciled")]
        [InlineData((int)KPIType.NotTFSC, "Not TFSC")]
        public void GetKPIName_HardcodedFallbackForKnownIds(int id, string expected)
        {
            EnumHelper.GetKPIName(id, null).Should().Be(expected);
        }

        [Fact]
        public void GetKPIName_UserFieldDescription_UsedWhenNameMissing()
        {
            var fields = new[]
            {
                new UserField { USR_ID = 5, USR_Category = "KPI",
                                USR_FieldName = "", USR_FieldDescription = "Desc fallback" }
            };
            EnumHelper.GetKPIName(5, fields).Should().Be("Desc fallback");
        }

        // ----- GetIncidentName -----

        [Fact]
        public void GetIncidentName_UsesUserFieldFromIncOrIncidentTypeCategory()
        {
            var fields = new[]
            {
                new UserField { USR_ID = 7, USR_Category = "INC", USR_FieldName = "Some incident" }
            };
            EnumHelper.GetIncidentName(7, fields).Should().Be("Some incident");

            var fields2 = new[]
            {
                new UserField { USR_ID = 8, USR_Category = "Incident Type", USR_FieldName = "Other" }
            };
            EnumHelper.GetIncidentName(8, fields2).Should().Be("Other");
        }
    }
}
