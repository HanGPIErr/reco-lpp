using System;
using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Models
{
    /// <summary>
    /// Tests pour <see cref="DataAmbre"/> — modèle Ambre. Vérifie les méthodes
    /// utilitaires (IsPivotAccount/IsReceivableAccount/GetUniqueKey).
    /// </summary>
    public class DataAmbreTests
    {
        [Fact]
        public void IsPivotAccount_True_WhenAccountIdMatchesPivotCode()
        {
            var d = new DataAmbre { Account_ID = "PIVOT_FR" };
            d.IsPivotAccount("PIVOT_FR").Should().BeTrue();
        }

        [Fact]
        public void IsPivotAccount_False_WhenDifferentCode()
        {
            var d = new DataAmbre { Account_ID = "PIVOT_FR" };
            d.IsPivotAccount("RECEIVABLE_FR").Should().BeFalse();
        }

        [Fact]
        public void IsPivotAccount_False_WhenNullAccountId()
        {
            var d = new DataAmbre { Account_ID = null };
            d.IsPivotAccount("PIVOT_FR").Should().BeFalse();
        }

        [Fact]
        public void IsReceivableAccount_True_WhenAccountIdMatches()
        {
            var d = new DataAmbre { Account_ID = "RECEIV_DE" };
            d.IsReceivableAccount("RECEIV_DE").Should().BeTrue();
        }

        [Fact]
        public void IsReceivableAccount_False_WhenNullAccountId()
        {
            var d = new DataAmbre { Account_ID = null };
            d.IsReceivableAccount("RECEIV_DE").Should().BeFalse();
        }

        [Fact]
        public void GetUniqueKey_ConcatenatesEventLabelOriginDateAmount()
        {
            var d = new DataAmbre
            {
                Event_Num = "EV1",
                RawLabel = "label",
                ReconciliationOrigin_Num = "ORIG",
                Operation_Date = new DateTime(2024, 5, 1),
                SignedAmount = 123.45m
            };
            d.GetUniqueKey().Should().Be("EV1_label_ORIG_20240501_123.45");
        }

        [Fact]
        public void GetUniqueKey_HandlesNullDateAndStrings()
        {
            var d = new DataAmbre { SignedAmount = 0m };
            // Null parts → vides, date null → "" via ToString sur Nullable<DateTime> non utilisé,
            // Operation_Date?.ToString(...) renvoie null → string vide à l'interpolation
            d.GetUniqueKey().Should().Be("___" + "_" + "0");
        }

        [Fact]
        public void GetUniqueKey_UsesInvariantCulture_ForDate()
        {
            // Pas dépendant de la culture courante
            var d = new DataAmbre
            {
                Event_Num = "EV1",
                RawLabel = "L",
                ReconciliationOrigin_Num = "O",
                Operation_Date = new DateTime(2024, 12, 31),
                SignedAmount = 1m
            };
            d.GetUniqueKey().Should().Contain("20241231");
        }
    }
}
