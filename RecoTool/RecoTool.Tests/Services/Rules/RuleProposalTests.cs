using System;
using FluentAssertions;
using RecoTool.Services.Rules;
using Xunit;

namespace RecoTool.Tests.Services.Rules
{
    /// <summary>
    /// Tests pour <see cref="RuleProposal"/> (POCO + propriétés calculées).
    /// </summary>
    public class RuleProposalTests
    {
        [Fact]
        public void DefaultStatus_IsPending()
        {
            new RuleProposal().Status.Should().Be(ProposalStatus.Pending);
        }

        [Fact]
        public void Summary_FormatsFieldOldNew()
        {
            var p = new RuleProposal { Field = "Action", OldValue = "1", NewValue = "2" };
            p.Summary.Should().Be("Action: 1 → 2");
        }

        [Fact]
        public void Summary_NullValues_RenderedAsNullText()
        {
            var p = new RuleProposal { Field = "KPI", OldValue = null, NewValue = null };
            p.Summary.Should().Be("KPI: (null) → (null)");
        }

        [Fact]
        public void StatusBadge_IsUpperCaseStatusName()
        {
            var p = new RuleProposal { Status = ProposalStatus.Accepted };
            p.StatusBadge.Should().Be("ACCEPTED");

            p.Status = ProposalStatus.Stale;
            p.StatusBadge.Should().Be("STALE");
        }

        [Fact]
        public void Smoke_AllPropertiesRoundTrip()
        {
            var now = DateTime.UtcNow;
            var p = new RuleProposal
            {
                ProposalId = 7,
                RecoId = "R1",
                RuleId = "RULE_42",
                Field = "RiskyItem",
                OldValue = "false",
                NewValue = "true",
                CreatedAt = now,
                CreatedBy = "alice",
                Status = ProposalStatus.Applied,
                DecidedBy = "bob",
                DecidedAt = now.AddMinutes(5),
                DeleteDate = now.AddDays(1)
            };
            p.ProposalId.Should().Be(7);
            p.Status.Should().Be(ProposalStatus.Applied);
            p.DecidedBy.Should().Be("bob");
        }
    }
}
