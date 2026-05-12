using System;
using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using RecoTool.Helpers;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Services.Helpers
{
    /// <summary>
    /// Tests pour <see cref="ValidationHelper"/> — validation Ambre, devises,
    /// formats invoice, parsing sécurisé décimal/date.
    /// </summary>
    public class ValidationHelperTests
    {
        // ===== ValidateAmbreData =====

        [Fact]
        public void ValidateAmbreData_AllRequiredMissing_ReturnsErrors()
        {
            var data = new Dictionary<string, object>();
            var errors = ValidationHelper.ValidateAmbreData(data);
            errors.Should().Contain(e => e.Contains("Account_ID"));
            errors.Should().Contain(e => e.Contains("Event_Num"));
            errors.Should().Contain(e => e.Contains("ReconciliationOrigin_Num"));
        }

        [Fact]
        public void ValidateAmbreData_RequiredEmptyString_ReturnsErrors()
        {
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "  " },
                { "Event_Num", null },
                { "ReconciliationOrigin_Num", "" }
            };
            ValidationHelper.ValidateAmbreData(data)
                .Should().Contain(e => e.Contains("Account_ID"))
                .And.Contain(e => e.Contains("Event_Num"))
                .And.Contain(e => e.Contains("ReconciliationOrigin_Num"));
        }

        [Fact]
        public void ValidateAmbreData_RequiredOk_ButInvalidNumeric_ReturnsNumericError()
        {
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "ACC" },
                { "Event_Num", "EV" },
                { "ReconciliationOrigin_Num", "ORIG" },
                { "SignedAmount", "not a number" },
            };
            var errors = ValidationHelper.ValidateAmbreData(data);
            errors.Should().Contain(e => e.Contains("SignedAmount"));
        }

        [Fact]
        public void ValidateAmbreData_InvalidDate_ReturnsDateError()
        {
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "ACC" },
                { "Event_Num", "EV" },
                { "ReconciliationOrigin_Num", "ORIG" },
                { "Operation_Date", "not-a-date" },
            };
            var errors = ValidationHelper.ValidateAmbreData(data);
            errors.Should().Contain(e => e.Contains("Operation_Date"));
        }

        [Fact]
        public void ValidateAmbreData_InvalidCurrency_ReturnsCurrencyError()
        {
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "ACC" },
                { "Event_Num", "EV" },
                { "ReconciliationOrigin_Num", "ORIG" },
                { "CCY", "abc" } // 3 chars mais minuscules
            };
            var errors = ValidationHelper.ValidateAmbreData(data);
            errors.Should().Contain(e => e.Contains("CCY"));
        }

        [Fact]
        public void ValidateAmbreData_ValidCurrency_NoCurrencyError()
        {
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "ACC" },
                { "Event_Num", "EV" },
                { "ReconciliationOrigin_Num", "ORIG" },
                { "CCY", "EUR" }
            };
            ValidationHelper.ValidateAmbreData(data).Should().NotContain(e => e.Contains("CCY"));
        }

        [Fact]
        public void ValidateAmbreData_InvalidCountryCode_ReturnsCountryError()
        {
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "ACC" },
                { "Event_Num", "EV" },
                { "ReconciliationOrigin_Num", "ORIG" },
                { "Country", "FRA" } // 3 chars
            };
            ValidationHelper.ValidateAmbreData(data).Should().Contain(e => e.Contains("Country"));
        }

        // ===== ValidateInvoiceIdFormat =====

        [Theory]
        [InlineData("BGI2024010000000", true)]
        [InlineData("BGI2024129999999", true)]
        [InlineData("BGI2024130000000", false)]   // mois 13 invalide
        [InlineData("BGI2024000000000", false)]   // mois 00 invalide
        [InlineData("INV20240100000000", false)]  // mauvais préfixe
        [InlineData("BGI202401000000", false)]    // pas assez de chiffres
        [InlineData(null, false)]
        [InlineData("", false)]
        public void ValidateInvoiceIdFormat_KnownPatterns(string input, bool expected)
        {
            ValidationHelper.ValidateInvoiceIdFormat(input).Should().Be(expected);
        }

        // ===== ValidateCurrency =====

        [Theory]
        [InlineData("EUR", true)]
        [InlineData("usd", true)]   // case-insensitive (ToUpper appliqué)
        [InlineData("XBT", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void ValidateCurrency_Variants(string input, bool expected)
        {
            ValidationHelper.ValidateCurrency(input).Should().Be(expected);
        }

        // ===== ValidateCountry =====

        [Fact]
        public void ValidateCountry_NullArgs_False()
        {
            ValidationHelper.ValidateCountry(null, new List<Country>()).Should().BeFalse();
            ValidationHelper.ValidateCountry("FR", null).Should().BeFalse();
        }

        [Fact]
        public void ValidateCountry_KnownCode_True()
        {
            var countries = new[] { new Country { CNT_Id = "FR" }, new Country { CNT_Id = "IT" } };
            ValidationHelper.ValidateCountry("fr", countries).Should().BeTrue();
            ValidationHelper.ValidateCountry("DE", countries).Should().BeFalse();
        }

        // ===== ValidateDataCoherence =====

        [Fact]
        public void ValidateDataCoherence_NullDataAmbre_ReturnsError()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P", CNT_AmbreReceivable = "R" };
            ValidationHelper.ValidateDataCoherence(null, country)
                .Should().Contain(e => e.Contains("Ambre"));
        }

        [Fact]
        public void ValidateDataCoherence_NullCountry_ReturnsError()
        {
            ValidationHelper.ValidateDataCoherence(new DataAmbre(), null)
                .Should().Contain(e => e.Contains("pays"));
        }

        [Fact]
        public void ValidateDataCoherence_AccountIdNotMatchingPivotOrRecv_ReturnsError()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P", CNT_AmbreReceivable = "R" };
            var data = new DataAmbre { Account_ID = "OTHER", SignedAmount = 1m };
            ValidationHelper.ValidateDataCoherence(data, country)
                .Should().Contain(e => e.Contains("ne correspond"));
        }

        [Fact]
        public void ValidateDataCoherence_BothAmountsZero_ReturnsError()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P", CNT_AmbreReceivable = "R" };
            var data = new DataAmbre { Account_ID = "P", SignedAmount = 0m, LocalSignedAmount = 0m };
            ValidationHelper.ValidateDataCoherence(data, country)
                .Should().Contain(e => e.Contains("montants"));
        }

        [Fact]
        public void ValidateDataCoherence_HappyPath_NoErrors()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P", CNT_AmbreReceivable = "R" };
            var data = new DataAmbre { Account_ID = "P", SignedAmount = 100m };
            ValidationHelper.ValidateDataCoherence(data, country).Should().BeEmpty();
        }

        // ===== SafeParseDecimal =====

        [Fact]
        public void SafeParseDecimal_NullReturnsZero()
        {
            ValidationHelper.SafeParseDecimal(null).Should().Be(0m);
        }

        [Theory]
        [InlineData(123)]
        [InlineData(123L)]
        [InlineData(123.45)]
        [InlineData("123.45")]
        public void SafeParseDecimal_NumericTypes_ParseToDecimal(object input)
        {
            // Le SafeParseDecimal doit gérer ces types et renvoyer du décimal proche
            ValidationHelper.SafeParseDecimal(input).Should().BeApproximately(123m, 0.5m);
        }

        [Fact]
        public void SafeParseDecimal_PreferredCulture_ParsesEuropeanFormat()
        {
            // "1 234,56" en fr-FR → 1234.56
            var fr = CultureInfo.GetCultureInfo("fr-FR");
            ValidationHelper.SafeParseDecimal("1 234,56", fr).Should().Be(1234.56m);
        }

        [Fact]
        public void SafeParseDecimal_NonBreakingSpaceCleanedUp()
        {
            var fr = CultureInfo.GetCultureInfo("fr-FR");
            ValidationHelper.SafeParseDecimal("1 234,56", fr).Should().Be(1234.56m);
        }

        [Fact]
        public void SafeParseDecimal_Garbage_ReturnsZero()
        {
            ValidationHelper.SafeParseDecimal("xyz").Should().Be(0m);
        }

        // ===== SafeParseDateTime =====

        [Fact]
        public void SafeParseDateTime_BoxedDateTime_Returned()
        {
            var d = new DateTime(2024, 5, 1);
            ValidationHelper.SafeParseDateTime(d).Should().Be(d);
        }

        [Fact]
        public void SafeParseDateTime_OleAutomationDouble_Converted()
        {
            // Excel OA serial : 45413 ≈ 2024-04-22
            var d = ValidationHelper.SafeParseDateTime(45413.0);
            d.Should().NotBeNull();
            d.Value.Year.Should().Be(2024);
        }

        [Fact]
        public void SafeParseDateTime_FrenchFormat_Parses()
        {
            ValidationHelper.SafeParseDateTime("31/12/2024").Should().Be(new DateTime(2024, 12, 31));
        }

        [Fact]
        public void SafeParseDateTime_Null_ReturnsNull()
        {
            ValidationHelper.SafeParseDateTime(null).Should().BeNull();
        }

        [Fact]
        public void SafeParseDateTime_Garbage_ReturnsNull()
        {
            ValidationHelper.SafeParseDateTime("not-a-date").Should().BeNull();
        }
    }
}
