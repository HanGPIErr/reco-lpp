using FluentAssertions;
using RecoTool.Services.Rules;
using Xunit;

namespace RecoTool.Tests.Services.Rules
{
    /// <summary>
    /// Tests pour <see cref="TruthRule"/> — defaults + Clone() (deep-copy).
    /// </summary>
    public class TruthRuleTests
    {
        [Fact]
        public void Defaults_AreSane()
        {
            var r = new TruthRule();
            r.Enabled.Should().BeTrue();
            r.Priority.Should().Be(100);
            r.Scope.Should().Be(RuleScope.Both);
            r.AccountSide.Should().Be("*");
            r.Sign.Should().Be("*");
            r.MTStatus.Should().Be(MtStatusCondition.Wildcard);
            r.ApplyTo.Should().Be(ApplyTarget.Self);
            r.AutoApply.Should().BeTrue();
            r.RespectUserEdits.Should().BeTrue();
            r.UserEditLockDays.Should().Be(7);
            r.Mode.Should().Be(RuleMode.Apply);
        }

        [Fact]
        public void Clone_ProducesIndependentCopy()
        {
            var src = new TruthRule
            {
                RuleId = "R1",
                Enabled = false,
                Priority = 50,
                Scope = RuleScope.Edit,
                AccountSide = "P",
                GuaranteeType = "ISSU",
                MTStatus = MtStatusCondition.Acked,
                OutputActionId = 5,
                OutputKpiId = 18,
                Mode = RuleMode.Propose,
                RespectUserEdits = false,
                UserEditLockDays = 14,
                Message = "hello"
            };

            var clone = src.Clone();
            clone.Should().NotBeSameAs(src);
            // Tous les champs scalaires sont identiques
            clone.RuleId.Should().Be("R1");
            clone.Enabled.Should().BeFalse();
            clone.Priority.Should().Be(50);
            clone.Scope.Should().Be(RuleScope.Edit);
            clone.AccountSide.Should().Be("P");
            clone.MTStatus.Should().Be(MtStatusCondition.Acked);
            clone.OutputActionId.Should().Be(5);
            clone.OutputKpiId.Should().Be(18);
            clone.Mode.Should().Be(RuleMode.Propose);
            clone.RespectUserEdits.Should().BeFalse();
            clone.UserEditLockDays.Should().Be(14);
            clone.Message.Should().Be("hello");
        }

        [Fact]
        public void Clone_MutationOnCopy_DoesNotAffectOriginal()
        {
            var src = new TruthRule { RuleId = "X", Priority = 100 };
            var clone = src.Clone();
            clone.RuleId = "MUTATED";
            clone.Priority = 1;

            src.RuleId.Should().Be("X");
            src.Priority.Should().Be(100);
        }
    }
}
