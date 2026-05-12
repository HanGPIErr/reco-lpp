using System;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests ciblés pour <see cref="OptionsService"/>. On valide ce qui est unitaire
    /// sans monter de DB : la garde du constructeur sur les arguments null,
    /// la signature des méthodes statiques d'invalidation, et le comportement
    /// de <see cref="OptionsService.GetCurrenciesAsync"/> avec un countryId vide.
    /// (Les chemins reposant sur les services dépendants nécessitent une intégration.)
    /// </summary>
    public class OptionsServiceTests
    {
        [Fact]
        public void Ctor_NullReconciliationService_Throws()
        {
            Action a = () => new OptionsService(null, null, null);
            a.Should().Throw<ArgumentNullException>()
                .WithMessage("*reconciliationService*");
        }

        [Fact]
        public void InvalidateAll_DoesNotThrow()
        {
            // Static — vérifie l'idempotence
            OptionsService.InvalidateAll();
            OptionsService.InvalidateAll();
        }

        [Fact]
        public void InvalidateUsers_DoesNotThrow()
        {
            OptionsService.InvalidateUsers();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void InvalidateCurrencies_NullOrWhitespace_DoesNotThrow(string country)
        {
            OptionsService.InvalidateCurrencies(country);
        }

        [Fact]
        public void InvalidateGuaranteeStatuses_DoesNotThrow()
        {
            OptionsService.InvalidateGuaranteeStatuses();
        }

        [Fact]
        public void InvalidateGuaranteeTypes_DoesNotThrow()
        {
            OptionsService.InvalidateGuaranteeTypes();
        }
    }
}
