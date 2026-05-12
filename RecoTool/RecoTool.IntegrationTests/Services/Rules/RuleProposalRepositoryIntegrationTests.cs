using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.IntegrationTests.Fixtures;
using RecoTool.Services.Rules;
using Xunit;

namespace RecoTool.IntegrationTests.Services.Rules
{
    /// <summary>
    /// Tests d'intégration pour <see cref="RuleProposalRepository"/> — CRUD sur
    /// <c>T_RuleProposals</c>. La table est créée à la demande par EnsureTableAsync.
    /// </summary>
    public class RuleProposalRepositoryIntegrationTests : IClassFixture<TempAccessDbFixture>
    {
        private readonly TempAccessDbFixture _fx;
        private readonly bool _ready;

        public RuleProposalRepositoryIntegrationTests(TempAccessDbFixture fx)
        {
            _fx = fx;
            _ready = AccessAvailable.AnyAce && _fx.Created;
        }

        private RuleProposalRepository MakeSut() => new RuleProposalRepository(_fx.ConnectionString);

        private static RuleProposal NewProposal(string recoId = "R1", string ruleId = "RULE-A", string field = "Action")
            => new RuleProposal
            {
                RecoId = recoId,
                RuleId = ruleId,
                Field = field,
                OldValue = "1",
                NewValue = "5",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "alice",
                Status = ProposalStatus.Pending
            };

        // ===== Ctor =====

        [SkippableFact]
        public void Ctor_NullConnectionString_Throws()
        {
            Skip.IfNot(AccessAvailable.AnyAce, AccessAvailable.SkipReasonOrNull);
            ((Action)(() => new RuleProposalRepository(null))).Should().Throw<ArgumentNullException>();
            ((Action)(() => new RuleProposalRepository(""))).Should().Throw<ArgumentNullException>();
        }

        // ===== EnsureTable =====

        [SkippableFact]
        public async Task EnsureTableAsync_CreatesTable_IdempotentOnSecondCall()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            await sut.EnsureTableAsync();
            // Second call should be a no-op
            await sut.EnsureTableAsync();

            // Verify by inserting something
            var n = await sut.InsertProposalsAsync(new[] { NewProposal(recoId: "TEST_ENSURE_" + Guid.NewGuid().ToString("N").Substring(0, 8)) });
            n.Should().Be(1);
        }

        // ===== InsertProposals =====

        [SkippableFact]
        public async Task InsertProposals_NullOrEmpty_ReturnsZero()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            (await sut.InsertProposalsAsync(null)).Should().Be(0);
            (await sut.InsertProposalsAsync(new List<RuleProposal>())).Should().Be(0);
        }

        [SkippableFact]
        public async Task InsertProposals_FiltersInvalidEntries()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            var batch = new[]
            {
                NewProposal(recoId: "VALID_" + Guid.NewGuid().ToString("N").Substring(0, 6)),
                new RuleProposal { RecoId = null, RuleId = "X", Field = "Action" },         // bad RecoId
                new RuleProposal { RecoId = "X", RuleId = null, Field = "Action" },         // bad RuleId
                new RuleProposal { RecoId = "X", RuleId = "X", Field = null },              // bad Field
                null,                                                                       // bad entry
            };
            var n = await sut.InsertProposalsAsync(batch);
            n.Should().Be(1, "seul l'entry valide est inséré");
        }

        [SkippableFact]
        public async Task InsertProposals_DuplicatePendingSkipped()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            var recoId = "DUP_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var p = NewProposal(recoId: recoId, ruleId: "R-DUP", field: "KPI");

            (await sut.InsertProposalsAsync(new[] { p })).Should().Be(1);
            // Same (RecoId, RuleId, Field) in Pending → skipped
            (await sut.InsertProposalsAsync(new[] { p })).Should().Be(0);
        }

        // ===== Load =====

        [SkippableFact]
        public async Task Load_NoFilter_ReturnsAllNonDeleted()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            var recoId = "LOAD_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await sut.InsertProposalsAsync(new[]
            {
                NewProposal(recoId: recoId, ruleId: "RA", field: "Action"),
                NewProposal(recoId: recoId, ruleId: "RB", field: "KPI"),
            });

            var all = await sut.LoadAsync(null);
            all.Where(p => p.RecoId == recoId).Should().HaveCount(2);
        }

        [SkippableFact]
        public async Task Load_FilteredByStatus_ReturnsOnlyMatching()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            var recoId = "FILT_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await sut.InsertProposalsAsync(new[]
            {
                NewProposal(recoId: recoId, ruleId: "RP", field: "Action"),
                NewProposal(recoId: recoId, ruleId: "RQ", field: "KPI"),
            });

            // Accept one
            var pending = await sut.LoadAsync(ProposalStatus.Pending);
            var first = pending.First(p => p.RecoId == recoId);
            await sut.UpdateStatusAsync(first.ProposalId.Value, ProposalStatus.Accepted, "bob");

            var accepted = await sut.LoadAsync(ProposalStatus.Accepted);
            accepted.Should().Contain(p => p.ProposalId == first.ProposalId);

            var stillPending = await sut.LoadAsync(ProposalStatus.Pending);
            stillPending.Should().NotContain(p => p.ProposalId == first.ProposalId);
        }

        // ===== UpdateStatus =====

        [SkippableFact]
        public async Task UpdateStatus_StampsDecidedByAndAt()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            var recoId = "DEC_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await sut.InsertProposalsAsync(new[] { NewProposal(recoId: recoId) });

            var p = (await sut.LoadAsync(ProposalStatus.Pending))
                .First(x => x.RecoId == recoId);

            (await sut.UpdateStatusAsync(p.ProposalId.Value, ProposalStatus.Rejected, "carol")).Should().BeTrue();

            var refreshed = (await sut.LoadAsync(ProposalStatus.Rejected))
                .First(x => x.ProposalId == p.ProposalId);
            refreshed.DecidedBy.Should().Be("carol");
            refreshed.DecidedAt.Should().NotBeNull();
            refreshed.Status.Should().Be(ProposalStatus.Rejected);
        }

        [SkippableFact]
        public async Task UpdateStatus_UnknownId_ReturnsFalse()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            await sut.EnsureTableAsync();
            (await sut.UpdateStatusAsync(999_999, ProposalStatus.Accepted, "alice")).Should().BeFalse();
        }

        // ===== MarkRecoProposalsStale =====

        [SkippableFact]
        public async Task MarkStale_FlipsAllPendingForReco()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            var recoId = "STALE_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await sut.InsertProposalsAsync(new[]
            {
                NewProposal(recoId: recoId, ruleId: "RX", field: "Action"),
                NewProposal(recoId: recoId, ruleId: "RY", field: "KPI"),
            });

            var n = await sut.MarkRecoProposalsStaleAsync(recoId);
            n.Should().BeGreaterOrEqualTo(2);

            var stale = (await sut.LoadAsync(ProposalStatus.Stale)).Where(p => p.RecoId == recoId);
            stale.Should().HaveCount(2);
        }

        [SkippableFact]
        public async Task MarkStale_NullRecoId_ReturnsZero()
        {
            Skip.IfNot(_ready, AccessAvailable.SkipReasonOrNull ?? "Fixture not ready");

            var sut = MakeSut();
            (await sut.MarkRecoProposalsStaleAsync(null)).Should().Be(0);
            (await sut.MarkRecoProposalsStaleAsync("")).Should().Be(0);
            (await sut.MarkRecoProposalsStaleAsync("   ")).Should().Be(0);
        }
    }
}
