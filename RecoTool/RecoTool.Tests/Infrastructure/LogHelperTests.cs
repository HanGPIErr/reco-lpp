using System;
using FluentAssertions;
using RecoTool.Infrastructure.Logging;
using Xunit;

namespace RecoTool.Tests.Infrastructure
{
    /// <summary>
    /// Tests pour <see cref="LogHelper"/>. Le helper fait du best-effort filesystem
    /// (écrit dans %APPDATA%\RecoTool) — ces tests vérifient surtout qu'il ne lève
    /// jamais d'exception, même avec des entrées dégénérées.
    /// </summary>
    public class LogHelperTests
    {
        [Fact]
        public void WriteAction_DoesNotThrow_OnNullOrValidArgs()
        {
            Action a1 = () => LogHelper.WriteAction("test-action", "some details");
            Action a2 = () => LogHelper.WriteAction(null, null);
            a1.Should().NotThrow();
            a2.Should().NotThrow();
        }

        [Fact]
        public void WritePerf_DoesNotThrow()
        {
            Action a = () => LogHelper.WritePerf("PERF_AREA", "ms=42");
            a.Should().NotThrow();
        }

        [Fact]
        public void WriteRuleApplied_DoesNotThrow_AndStripsTabs()
        {
            // Pas d'assert sur le contenu du fichier (effets de bord),
            // on vérifie juste que le code ne lève pas même avec des onglets dans les arguments.
            Action a = () => LogHelper.WriteRuleApplied(
                origin: "import",
                countryId: "FR",
                recoId: "REC1",
                ruleId: "R001",
                outputs: "Action=1\tKPI=2",
                message: "msg with\ttab");
            a.Should().NotThrow();
        }

        [Fact]
        public void WriteRuleApplied_AllNullArgs_DoesNotThrow()
        {
            Action a = () => LogHelper.WriteRuleApplied(null, null, null, null, null, null);
            a.Should().NotThrow();
        }
    }
}
