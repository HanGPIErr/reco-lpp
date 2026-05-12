using System;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.IntegrationTests.Fixtures;
using RecoTool.Services;
using Xunit;

namespace RecoTool.IntegrationTests.Services
{
    /// <summary>
    /// Fixture pour <see cref="UserViewPreferenceService"/>.
    /// </summary>
    public sealed class UserViewPreferenceFixture : TempAccessDbFixture
    {
        public bool Ready { get; }

        public UserViewPreferenceFixture()
        {
            Ready = AccessAvailable.AnyAce && Created;
            if (!Ready) return;

            ExecuteNonQuery(@"CREATE TABLE T_Ref_User_Fields_Preference
                               (UPF_id COUNTER PRIMARY KEY,
                                UPF_Name TEXT(100),
                                UPF_user TEXT(50),
                                UPF_SQL MEMO,
                                UPF_ColumnWidths MEMO)");
        }
    }

    /// <summary>
    /// Tests d'intégration pour <see cref="UserViewPreferenceService"/>.
    /// </summary>
    public class UserViewPreferenceServiceIntegrationTests : IClassFixture<UserViewPreferenceFixture>
    {
        private readonly UserViewPreferenceFixture _fx;
        private readonly bool _ready;

        public UserViewPreferenceServiceIntegrationTests(UserViewPreferenceFixture fx)
        {
            _fx = fx;
            _ready = _fx.Ready;
        }

        [SkippableFact]
        public async Task InsertAsync_ReturnsGeneratedId_AndAppearsInGetAll()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserViewPreferenceService(_fx.ConnectionString, "alice");
            var name = "View_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var id = await sut.InsertAsync(name, "WHERE x = 1", "{\"col\":42}");

            id.Should().BeGreaterThan(0);

            var all = await sut.GetAllAsync();
            all.Should().Contain(p => p.UPF_Name == name && p.UPF_user == "alice");
        }

        [SkippableFact]
        public async Task UpdateAsync_ChangesPersistedFields()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserViewPreferenceService(_fx.ConnectionString, "alice");
            var name = "ViewU_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var id = await sut.InsertAsync(name, "WHERE a = 1", "{}");

            var ok = await sut.UpdateAsync(id, name + "_updated", "WHERE b = 2", "{\"new\":1}");
            ok.Should().BeTrue();

            var all = await sut.GetAllAsync();
            all.Should().Contain(p => p.UPF_id == id && p.UPF_Name == name + "_updated"
                                                     && p.UPF_SQL == "WHERE b = 2");
        }

        [SkippableFact]
        public async Task UpdateAsync_UnknownId_ReturnsFalse()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserViewPreferenceService(_fx.ConnectionString, "alice");
            var ok = await sut.UpdateAsync(999_999, "x", "y", "z");
            ok.Should().BeFalse();
        }
    }
}
