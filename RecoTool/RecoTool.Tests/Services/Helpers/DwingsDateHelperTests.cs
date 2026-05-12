using System;
using FluentAssertions;
using RecoTool.Services.Helpers;
using Xunit;

namespace RecoTool.Tests.Services.Helpers
{
    /// <summary>
    /// Tests pour <see cref="DwingsDateHelper.TryParseDwingsDate"/>.
    /// Couvre les formats DWINGS supportés (mois EN abrégé, slashs, points, ISO).
    /// </summary>
    public class DwingsDateHelperTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryParseDwingsDate_NullOrWhitespace_ReturnsFalse(string input)
        {
            DwingsDateHelper.TryParseDwingsDate(input, out var dt).Should().BeFalse();
            dt.Should().Be(default(DateTime));
        }

        [Theory]
        [InlineData("01-Jan-24", 2024, 1, 1)]
        [InlineData("31-Dec-2024", 2024, 12, 31)]
        [InlineData("1-Jan-24", 2024, 1, 1)]
        [InlineData("1-Jan-2024", 2024, 1, 1)]
        public void TryParseDwingsDate_EnglishMonth_Parses(string input, int year, int month, int day)
        {
            DwingsDateHelper.TryParseDwingsDate(input, out var dt).Should().BeTrue();
            dt.Year.Should().Be(year);
            dt.Month.Should().Be(month);
            dt.Day.Should().Be(day);
        }

        [Theory]
        [InlineData("31/12/2024", 2024, 12, 31)]
        [InlineData("31/12/24", 2024, 12, 31)]
        [InlineData("1/1/24", 2024, 1, 1)]
        public void TryParseDwingsDate_DayMonthYear_Parses(string input, int year, int month, int day)
        {
            DwingsDateHelper.TryParseDwingsDate(input, out var dt).Should().BeTrue();
            dt.Year.Should().Be(year);
            dt.Month.Should().Be(month);
            dt.Day.Should().Be(day);
        }

        [Fact]
        public void TryParseDwingsDate_IsoFormat_Parses()
        {
            DwingsDateHelper.TryParseDwingsDate("2024-05-10", out var dt).Should().BeTrue();
            dt.Should().Be(new DateTime(2024, 5, 10));
        }

        [Theory]
        [InlineData("31.12.2024")]
        [InlineData("1.12.2024")]
        public void TryParseDwingsDate_DotSeparator_Parses(string input)
        {
            DwingsDateHelper.TryParseDwingsDate(input, out var dt).Should().BeTrue();
            dt.Year.Should().Be(2024);
            dt.Month.Should().Be(12);
        }

        [Fact]
        public void TryParseDwingsDate_NormalizesDifferentDashes()
        {
            // En-dash (–) doit être traité comme un trait d'union
            DwingsDateHelper.TryParseDwingsDate("01–Jan–24", out var dt).Should().BeTrue();
            dt.Should().Be(new DateTime(2024, 1, 1));
        }

        [Fact]
        public void TryParseDwingsDate_MonthInUpperCase_StillParses()
        {
            DwingsDateHelper.TryParseDwingsDate("01-JAN-24", out var dt).Should().BeTrue();
            dt.Should().Be(new DateTime(2024, 1, 1));
        }

        [Fact]
        public void TryParseDwingsDate_TotalNonsense_ReturnsFalse()
        {
            DwingsDateHelper.TryParseDwingsDate("xx not a date xx", out _).Should().BeFalse();
        }
    }
}
