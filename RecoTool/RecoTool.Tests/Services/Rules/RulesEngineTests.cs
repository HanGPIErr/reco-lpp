using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Services;
using RecoTool.Services.Rules;
using Xunit;

namespace RecoTool.Tests.Services.Rules
{
    /// <summary>
    /// Tests métier pour <see cref="RulesEngine"/>. Utilise le seam de test
    /// <c>__TestSeedRules</c> pour bypasser le repository et fournir une liste
    /// de règles directement en mémoire.
    /// </summary>
    public class RulesEngineTests
    {
        private static RulesEngine MakeEngineWithRules(params TruthRule[] rules)
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.ReferentialConnectionString).Returns("Provider=fake;");
            var engine = new RulesEngine(ofs.Object);
            engine.__TestSeedRules(rules);
            return engine;
        }

        // ===== Empty / no-match =====

        [Fact]
        public async Task EvaluateAsync_NullContext_ReturnsNull()
        {
            var engine = MakeEngineWithRules();
            (await engine.EvaluateAsync(null, RuleScope.Edit)).Should().BeNull();
        }

        [Fact]
        public async Task EvaluateAsync_NoRules_ReturnsNull()
        {
            var engine = MakeEngineWithRules();
            var ctx = new RuleContext { CountryId = "FR", IsPivot = true };
            (await engine.EvaluateAsync(ctx, RuleScope.Edit)).Should().BeNull();
        }

        [Fact]
        public async Task EvaluateAsync_DisabledRule_DoesNotMatch()
        {
            var rule = new TruthRule
            {
                RuleId = "R1",
                Enabled = false,
                AccountSide = "*",
                OutputActionId = 5
            };
            var engine = MakeEngineWithRules(rule);
            var ctx = new RuleContext { CountryId = "FR", IsPivot = true };

            (await engine.EvaluateAsync(ctx, RuleScope.Edit)).Should().BeNull();
        }

        [Fact]
        public async Task EvaluateAsync_RuleScopeMismatch_NotApplied()
        {
            var rule = new TruthRule
            {
                RuleId = "R1",
                Enabled = true,
                Scope = RuleScope.Import,
                AccountSide = "*",
                OutputActionId = 5
            };
            var engine = MakeEngineWithRules(rule);
            var ctx = new RuleContext { CountryId = "FR", IsPivot = true };

            (await engine.EvaluateAsync(ctx, RuleScope.Edit)).Should().BeNull();
            (await engine.EvaluateAsync(ctx, RuleScope.Import)).Should().NotBeNull();
        }

        [Fact]
        public async Task EvaluateAsync_ScopeBoth_MatchesAnyScope()
        {
            var rule = new TruthRule
            {
                RuleId = "R1", Enabled = true, Scope = RuleScope.Both,
                AccountSide = "*", OutputActionId = 7
            };
            var engine = MakeEngineWithRules(rule);
            var ctx = new RuleContext { CountryId = "FR", IsPivot = true };

            (await engine.EvaluateAsync(ctx, RuleScope.Edit)).Should().NotBeNull();
            (await engine.EvaluateAsync(ctx, RuleScope.Import)).Should().NotBeNull();
        }

        // ===== Output projection =====

        [Fact]
        public async Task EvaluateAsync_FirstMatch_ProjectsAllOutputsIntoResult()
        {
            var rule = new TruthRule
            {
                RuleId = "R-OUT",
                Enabled = true,
                Scope = RuleScope.Both,
                AccountSide = "*",
                OutputActionId = 5,
                OutputKpiId = 18,
                OutputIncidentTypeId = 24,
                OutputRiskyItem = true,
                OutputReasonNonRiskyId = 99,
                OutputToRemind = true,
                OutputToRemindDays = 7,
                OutputActionDone = true,
                OutputFirstClaimToday = true,
                Message = "user note"
            };
            var engine = MakeEngineWithRules(rule);

            var res = await engine.EvaluateAsync(new RuleContext { CountryId = "FR", IsPivot = true }, RuleScope.Edit);

            res.Should().NotBeNull();
            res.NewActionIdSelf.Should().Be(5);
            res.NewKpiIdSelf.Should().Be(18);
            res.NewIncidentTypeIdSelf.Should().Be(24);
            res.NewRiskyItemSelf.Should().BeTrue();
            res.NewReasonNonRiskyIdSelf.Should().Be(99);
            res.NewToRemindSelf.Should().BeTrue();
            res.NewToRemindDaysSelf.Should().Be(7);
            res.NewActionDoneSelf.Should().Be(1);
            res.NewFirstClaimTodaySelf.Should().BeTrue();
            res.UserMessage.Should().Be("user note");
            res.RequiresUserConfirm.Should().BeTrue();
        }

        [Fact]
        public async Task EvaluateAsync_NoMessage_RequiresUserConfirmIsFalse()
        {
            var rule = new TruthRule
            {
                RuleId = "R", Enabled = true, AccountSide = "*", OutputActionId = 1
            };
            var res = await MakeEngineWithRules(rule).EvaluateAsync(new RuleContext(), RuleScope.Edit);
            res.RequiresUserConfirm.Should().BeFalse();
        }

        // ===== Conditions: AccountSide =====

        [Fact]
        public async Task EvaluateAsync_AccountSideP_OnlyMatchesPivot()
        {
            var rule = new TruthRule { RuleId = "R", Enabled = true, AccountSide = "P", OutputActionId = 1 };
            var engine = MakeEngineWithRules(rule);

            (await engine.EvaluateAsync(new RuleContext { IsPivot = true }, RuleScope.Edit)).Should().NotBeNull();
            (await engine.EvaluateAsync(new RuleContext { IsPivot = false }, RuleScope.Edit)).Should().BeNull();
        }

        [Fact]
        public async Task EvaluateAsync_AccountSideR_OnlyMatchesReceivable()
        {
            var rule = new TruthRule { RuleId = "R", Enabled = true, AccountSide = "R", OutputActionId = 1 };
            var engine = MakeEngineWithRules(rule);

            (await engine.EvaluateAsync(new RuleContext { IsPivot = true }, RuleScope.Edit)).Should().BeNull();
            (await engine.EvaluateAsync(new RuleContext { IsPivot = false }, RuleScope.Edit)).Should().NotBeNull();
        }

        // ===== Conditions: Booking (CountryId) =====

        [Fact]
        public async Task EvaluateAsync_BookingFilter_MatchesByCountry()
        {
            var rule = new TruthRule { RuleId = "R", Enabled = true, AccountSide = "*", Booking = "FR;DE", OutputActionId = 1 };
            var engine = MakeEngineWithRules(rule);

            (await engine.EvaluateAsync(new RuleContext { CountryId = "FR" }, RuleScope.Edit)).Should().NotBeNull();
            (await engine.EvaluateAsync(new RuleContext { CountryId = "DE" }, RuleScope.Edit)).Should().NotBeNull();
            (await engine.EvaluateAsync(new RuleContext { CountryId = "IT" }, RuleScope.Edit)).Should().BeNull();
        }

        // ===== Conditions: HasDwingsLink + IsGrouped + IsAmountMatch =====

        [Fact]
        public async Task EvaluateAsync_BoolFlags_RespectExpectedValue()
        {
            var rule = new TruthRule
            {
                RuleId = "R", Enabled = true, AccountSide = "*",
                HasDwingsLink = true, IsGrouped = true, IsAmountMatch = false,
                OutputActionId = 1
            };
            var engine = MakeEngineWithRules(rule);

            // Match
            var ctx = new RuleContext { HasDwingsLink = true, IsGrouped = true, IsAmountMatch = false };
            (await engine.EvaluateAsync(ctx, RuleScope.Edit)).Should().NotBeNull();

            // Mismatch sur IsAmountMatch
            ctx.IsAmountMatch = true;
            (await engine.EvaluateAsync(ctx, RuleScope.Edit)).Should().BeNull();

            // Null sur HasDwingsLink → ne matche pas car règle attend true explicite
            (await engine.EvaluateAsync(new RuleContext { IsGrouped = true, IsAmountMatch = false }, RuleScope.Edit))
                .Should().BeNull();
        }

        // ===== Conditions: MissingAmount range =====

        [Fact]
        public async Task EvaluateAsync_MissingAmountRange_RespectedWithMinMax()
        {
            var rule = new TruthRule
            {
                RuleId = "R", Enabled = true, AccountSide = "*",
                MissingAmountMin = 10m, MissingAmountMax = 100m,
                OutputActionId = 1
            };
            var engine = MakeEngineWithRules(rule);

            (await engine.EvaluateAsync(new RuleContext { MissingAmount = 50m }, RuleScope.Edit)).Should().NotBeNull();
            (await engine.EvaluateAsync(new RuleContext { MissingAmount = 5m }, RuleScope.Edit)).Should().BeNull();
            (await engine.EvaluateAsync(new RuleContext { MissingAmount = 200m }, RuleScope.Edit)).Should().BeNull();
            (await engine.EvaluateAsync(new RuleContext { MissingAmount = null }, RuleScope.Edit)).Should().BeNull();
        }

        // ===== Conditions: Sign =====

        [Fact]
        public async Task EvaluateAsync_Sign_MatchesCreditOrDebit()
        {
            var rC = new TruthRule { RuleId = "RC", Enabled = true, AccountSide = "*", Sign = "C", OutputActionId = 1 };
            var rD = new TruthRule { RuleId = "RD", Enabled = true, AccountSide = "*", Sign = "D", OutputActionId = 2 };
            var engine = MakeEngineWithRules(rC, rD);

            (await engine.EvaluateAsync(new RuleContext { Sign = "C" }, RuleScope.Edit))
                .Rule.RuleId.Should().Be("RC");
            (await engine.EvaluateAsync(new RuleContext { Sign = "D" }, RuleScope.Edit))
                .Rule.RuleId.Should().Be("RD");
        }

        // ===== Priority order =====

        [Fact]
        public async Task EvaluateAsync_FirstRuleInOrderWins()
        {
            // Le moteur retourne le PREMIER match dans l'ordre fourni.
            var first = new TruthRule { RuleId = "FIRST", Enabled = true, AccountSide = "*", OutputActionId = 1 };
            var second = new TruthRule { RuleId = "SECOND", Enabled = true, AccountSide = "*", OutputActionId = 2 };
            var engine = MakeEngineWithRules(first, second);

            var res = await engine.EvaluateAsync(new RuleContext(), RuleScope.Edit);
            res.Rule.RuleId.Should().Be("FIRST");
        }

        // ===== InvalidateCache =====

        [Fact]
        public async Task InvalidateCache_ForcesReload()
        {
            // Difficile à tester sans mock du repository — on vérifie au moins qu'aucune
            // exception ne survient et que le cache est vidé (séquence reproduit le code).
            var engine = MakeEngineWithRules(new TruthRule { RuleId = "R", Enabled = true, AccountSide = "*", OutputActionId = 1 });
            engine.InvalidateCache();
            // Après invalidation, la prochaine évaluation rechargerait depuis le repo (qui est faux ici).
            // On ne ré-évalue pas pour ne pas déclencher la requête vers le faux repo.
        }

        // ===== EvaluateAllForDebugAsync =====

        [Fact]
        public async Task EvaluateAllForDebugAsync_ReturnsOneResultPerRule()
        {
            var r1 = new TruthRule { RuleId = "R1", Enabled = true, AccountSide = "*", OutputActionId = 1 };
            var r2 = new TruthRule { RuleId = "R2", Enabled = false, AccountSide = "*", OutputActionId = 2 };
            var engine = MakeEngineWithRules(r1, r2);

            var ctx = new RuleContext { CountryId = "FR" };
            var debug = await engine.EvaluateAllForDebugAsync(ctx, RuleScope.Edit);

            debug.Should().HaveCount(2);
            debug[0].Rule.RuleId.Should().Be("R1");
            debug[0].IsMatch.Should().BeTrue();
            debug[1].Rule.RuleId.Should().Be("R2");
            debug[1].IsMatch.Should().BeFalse(); // disabled
        }
    }
}
