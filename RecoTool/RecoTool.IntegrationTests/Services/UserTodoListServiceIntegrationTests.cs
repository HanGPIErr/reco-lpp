using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.IntegrationTests.Fixtures;
using RecoTool.Models;
using RecoTool.Services;
using Xunit;

namespace RecoTool.IntegrationTests.Services
{
    /// <summary>
    /// Tests d'intégration pour <see cref="UserTodoListService"/>.
    /// La fixture est partagée par classe ; <c>EnsureTableAsync</c> est appelé une
    /// seule fois pour créer T_Ref_TodoList.
    /// </summary>
    public sealed class UserTodoListFixture : TempAccessDbFixture
    {
        public bool Ready { get; }

        public UserTodoListFixture()
        {
            Ready = AccessAvailable.AnyAce && Created;
            if (!Ready) return;

            // EnsureTableAsync est testé sur cette fixture — on la laisse créer la table.
            var svc = new UserTodoListService(ConnectionString);
            try { svc.EnsureTableAsync().GetAwaiter().GetResult(); }
            catch { /* tests verront le pb */ }
        }
    }

    public class UserTodoListServiceIntegrationTests : IClassFixture<UserTodoListFixture>
    {
        private readonly UserTodoListFixture _fx;
        private readonly bool _ready;

        public UserTodoListServiceIntegrationTests(UserTodoListFixture fx)
        {
            _fx = fx;
            _ready = _fx.Ready;
        }

        [SkippableFact]
        public async Task EnsureTableAsync_Idempotent_DoesNotThrow()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");
            var sut = new UserTodoListService(_fx.ConnectionString);
            // Devrait juste détecter que la table existe et ne pas la recréer
            (await sut.EnsureTableAsync()).Should().BeTrue();
        }

        [SkippableFact]
        public async Task UpsertAndList_RoundTrip()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserTodoListService(_fx.ConnectionString);
            var item = new TodoListItem
            {
                TDL_Name = "Test_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                TDL_FilterName = "FilterA",
                TDL_ViewName = "ViewA",
                TDL_Account = "Pivot",
                TDL_Order = 1,
                TDL_Active = true,
                TDL_CountryId = "FR"
            };

            // UpsertAsync renvoie l'ID inséré/mis à jour (int > 0 sur succès)
            var id = await sut.UpsertAsync(item);
            id.Should().BeGreaterThan(0);

            // List filtrée par pays
            var listFr = await sut.ListAsync("FR");
            listFr.Should().Contain(t => t.TDL_Name == item.TDL_Name);

            // Cleanup pour ne pas polluer la fixture
            await sut.DeleteAsync(id);
        }

        [SkippableFact]
        public async Task DeleteAsync_UnknownId_ReturnsFalse()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = new UserTodoListService(_fx.ConnectionString);
            (await sut.DeleteAsync(999_999)).Should().BeFalse();
        }
    }
}
