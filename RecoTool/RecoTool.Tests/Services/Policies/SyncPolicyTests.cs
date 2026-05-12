using FluentAssertions;
using RecoTool.Services.Policies;
using Xunit;

namespace RecoTool.Tests.Services.Policies
{
    /// <summary>
    /// Tests pour <see cref="SyncPolicy"/>.
    /// </summary>
    public class SyncPolicyTests
    {
        [Fact]
        public void Default_Values_AreAsDocumented()
        {
            var p = new SyncPolicy();
            p.AllowBackgroundPushes.Should().BeTrue();
            p.ShouldSyncOnCountryChange.Should().BeTrue();
            p.ShouldSyncOnPageUnload.Should().BeFalse();
            p.ShouldSyncOnAppClose.Should().BeTrue();
        }

        [Fact]
        public void WithBackgroundPushes_ReturnsNewInstanceWithFlagFlipped()
        {
            var p = new SyncPolicy();
            var p2 = (SyncPolicy)p.WithBackgroundPushes(false);

            p2.Should().NotBeSameAs(p);
            p2.AllowBackgroundPushes.Should().BeFalse();
            // Le reste des flags est préservé
            p2.ShouldSyncOnCountryChange.Should().Be(p.ShouldSyncOnCountryChange);
            p2.ShouldSyncOnPageUnload.Should().Be(p.ShouldSyncOnPageUnload);
            p2.ShouldSyncOnAppClose.Should().Be(p.ShouldSyncOnAppClose);
        }

        [Fact]
        public void WithBackgroundPushes_TrueRoundTrip()
        {
            var p = new SyncPolicy().WithBackgroundPushes(false).WithBackgroundPushes(true);
            p.AllowBackgroundPushes.Should().BeTrue();
        }
    }
}
