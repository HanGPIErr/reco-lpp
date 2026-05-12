using System;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Helpers;
using Xunit;

namespace RecoTool.Tests.Helpers
{
    /// <summary>
    /// Tests pour <see cref="MultiUserHelper"/>. Couvre la mise en forme des durées
    /// et le scénario "session tracker null" / exception du résumé asynchrone.
    /// </summary>
    public class MultiUserHelperTests
    {
        // ----- FormatDuration -----

        [Fact]
        public void FormatDuration_LessThan30Seconds_ReturnsJustNow()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromSeconds(0)).Should().Be("just now");
            MultiUserHelper.FormatDuration(TimeSpan.FromSeconds(15)).Should().Be("just now");
            MultiUserHelper.FormatDuration(TimeSpan.FromSeconds(29)).Should().Be("just now");
        }

        [Fact]
        public void FormatDuration_LessThan1Minute_ReturnsSeconds()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromSeconds(45)).Should().Be("45 seconds");
        }

        [Fact]
        public void FormatDuration_OneMinute_ReturnsSingularMinute()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromMinutes(1)).Should().Be("1 minute");
        }

        [Fact]
        public void FormatDuration_SeveralMinutes_ReturnsPluralMinutes()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromMinutes(5)).Should().Be("5 minutes");
            MultiUserHelper.FormatDuration(TimeSpan.FromMinutes(59)).Should().Be("59 minutes");
        }

        [Fact]
        public void FormatDuration_OneHour_ReturnsSingular()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromMinutes(60)).Should().Be("1 hour");
        }

        [Fact]
        public void FormatDuration_SeveralHours_ReturnsPlural()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromHours(3)).Should().Be("3 hours");
            MultiUserHelper.FormatDuration(TimeSpan.FromHours(23)).Should().Be("23 hours");
        }

        [Fact]
        public void FormatDuration_OneDay_ReturnsSingular()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromHours(24)).Should().Be("1 day");
        }

        [Fact]
        public void FormatDuration_SeveralDays_ReturnsPlural()
        {
            MultiUserHelper.FormatDuration(TimeSpan.FromDays(5)).Should().Be("5 days");
        }

        // ----- GetSessionSummaryAsync -----

        [Fact]
        public async Task GetSessionSummaryAsync_NullTracker_ReturnsEmpty()
        {
            var summary = await MultiUserHelper.GetSessionSummaryAsync(null, 1);
            summary.Should().Be(string.Empty);
        }
    }
}
