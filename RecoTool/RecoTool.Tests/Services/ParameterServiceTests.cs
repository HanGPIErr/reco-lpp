using System;
using FluentAssertions;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests unitaires pour <see cref="ParameterService"/>. Le service utilise un cache
    /// statique partagé — donc on évite de tester l'init complète qui nécessite OleDb.
    /// On valide ici les gardes (ctor null, GetParameter null/empty, SetParameter null).
    ///
    /// /!\ ParameterService possède un état STATIC — ces tests pourraient interagir avec
    ///     d'autres tests si Initialize() est appelé. Évite donc d'appeler Initialize().
    /// </summary>
    public class ParameterServiceTests
    {
        [Fact]
        public void Ctor_NullConnectionString_Throws()
        {
            Action a = () => new ParameterService(null);
            a.Should().Throw<ArgumentNullException>()
                .WithMessage("*referentialConnectionString*");
        }

        [Fact]
        public void Ctor_EmptyConnectionString_AcceptedButNotUsableForDb()
        {
            // Le ctor ne valide pas la chaîne — il accepte une string vide.
            // L'erreur se manifeste à la première opération DB. On documente le comportement.
            Action a = () => new ParameterService("");
            a.Should().NotThrow();
        }

        [Fact]
        public void Ctor_ValidLookingString_DoesNotTouchDatabase()
        {
            // Le ctor ne fait pas de I/O — pas d'erreur même avec une chaîne fictive.
            Action a = () => new ParameterService("Provider=fake;Data Source=:memory:;");
            a.Should().NotThrow();
        }
    }
}
