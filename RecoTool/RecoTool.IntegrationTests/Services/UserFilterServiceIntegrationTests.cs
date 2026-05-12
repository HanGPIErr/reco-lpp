using System;
using FluentAssertions;
using RecoTool.IntegrationTests.Fixtures;
using RecoTool.Services;
using Xunit;

namespace RecoTool.IntegrationTests.Services
{
    /// <summary>
    /// Fixture spécifique : crée la BDD + le schéma T_Ref_User_Filter une seule fois.
    /// </summary>
    public sealed class UserFilterFixture : TempAccessDbFixture
    {
        public bool Ready { get; }

        public UserFilterFixture()
        {
            Ready = AccessAvailable.AnyAce && Created;
            if (!Ready) return;

            ExecuteNonQuery(@"CREATE TABLE T_Ref_User_Filter
                               (UFI_id COUNTER PRIMARY KEY,
                                UFI_Name TEXT(100),
                                UFI_SQL MEMO,
                                UFI_CreatedBy TEXT(50))");
        }
    }

    /// <summary>
    /// Tests d'intégration pour <see cref="UserFilterService"/> contre une vraie BDD Access.
    /// Couvre Save (insert + update), LoadUserFilterWhere et ListUserFilterNames.
    /// </summary>
    public class UserFilterServiceIntegrationTests : IClassFixture<UserFilterFixture>
    {
        private readonly UserFilterFixture _fx;
        private readonly bool _ready;

        public UserFilterServiceIntegrationTests(UserFilterFixture fx)
        {
            _fx = fx;
            _ready = _fx.Ready;
        }

        [SkippableFact]
        public void SaveUserFilter_NewName_Inserts()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserFilterService(_fx.ConnectionString, "alice");
            var unique = "F1_" + Guid.NewGuid().ToString("N");
            sut.SaveUserFilter(unique, "CCY = 'EUR'");

            var got = sut.LoadUserFilterWhere(unique);
            got.Should().Be("CCY = 'EUR'");
        }

        [SkippableFact]
        public void SaveUserFilter_ExistingName_UpdatesInPlace()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserFilterService(_fx.ConnectionString, "alice");
            var unique = "F2_" + Guid.NewGuid().ToString("N");

            sut.SaveUserFilter(unique, "CCY = 'EUR'");
            sut.SaveUserFilter(unique, "CCY = 'USD'");

            // OPTIM: cache busté par Save → relecture renvoie la nouvelle valeur
            sut.LoadUserFilterWhere(unique).Should().Be("CCY = 'USD'");
        }

        [SkippableFact]
        public void ListUserFilterNames_ReturnsSavedFilters()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserFilterService(_fx.ConnectionString, "alice");
            var prefix = "L" + Guid.NewGuid().ToString("N").Substring(0, 6);
            sut.SaveUserFilter(prefix + "_a", "x = 1");
            sut.SaveUserFilter(prefix + "_b", "x = 2");

            var names = sut.ListUserFilterNames(prefix);
            names.Should().HaveCountGreaterOrEqualTo(2);
            names.Should().Contain(n => n.StartsWith(prefix));
        }

        [SkippableFact]
        public void LoadUserFilterWhere_UnknownName_ReturnsNull()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserFilterService(_fx.ConnectionString, "alice");
            var got = sut.LoadUserFilterWhere("DOES_NOT_EXIST_" + Guid.NewGuid().ToString("N"));
            got.Should().BeNull();
        }

        [SkippableFact]
        public void SaveUserFilter_NullOrEmptyName_Throws()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserFilterService(_fx.ConnectionString, "alice");
            Action a1 = () => sut.SaveUserFilter(null, "x");
            Action a2 = () => sut.SaveUserFilter("", "x");
            Action a3 = () => sut.SaveUserFilter("   ", "x");
            a1.Should().Throw<ArgumentException>();
            a2.Should().Throw<ArgumentException>();
            a3.Should().Throw<ArgumentException>();
        }
    }
}
