using System;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests unitaires pour <see cref="LookupService"/> exploitant la nouvelle
    /// interface <see cref="IOfflineFirstService"/>. Couvre les cas où les chemins
    /// sont absents/invalides — ces cas retournent toujours une liste vide
    /// sans toucher à la BDD (pas de OleDb réel).
    /// </summary>
    public class LookupServiceTests
    {
        [Fact]
        public void Ctor_NullDependency_Throws()
        {
            Action act = () => new LookupService(null);
            act.Should().Throw<ArgumentNullException>()
                .WithMessage("*offlineFirstService*");
        }

        [Fact]
        public async Task GetCurrenciesAsync_EmptyCountryId_ReturnsEmpty()
        {
            var ofs = new Mock<IOfflineFirstService>(MockBehavior.Strict);
            // Aucune méthode n'est censée être appelée si countryId vide
            var sut = new LookupService(ofs.Object);

            (await sut.GetCurrenciesAsync(null)).Should().BeEmpty();
            (await sut.GetCurrenciesAsync("")).Should().BeEmpty();
            (await sut.GetCurrenciesAsync("   ")).Should().BeEmpty();

            ofs.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task GetCurrenciesAsync_PathDoesNotExist_ReturnsEmpty()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetLocalAmbreDatabasePath("FR"))
               .Returns(@"Z:\does\not\exist.accdb");

            var sut = new LookupService(ofs.Object);
            var got = await sut.GetCurrenciesAsync("FR");
            got.Should().BeEmpty();
        }

        [Fact]
        public async Task GetCurrenciesAsync_NullPath_ReturnsEmpty()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetLocalAmbreDatabasePath(It.IsAny<string>())).Returns((string)null);

            var sut = new LookupService(ofs.Object);
            (await sut.GetCurrenciesAsync("FR")).Should().BeEmpty();
        }

        [Fact]
        public async Task GetGuaranteeStatusesAsync_PathDoesNotExist_ReturnsEmpty()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetLocalDWDatabasePath(It.IsAny<string>())).Returns(@"Z:\nope.accdb");

            var sut = new LookupService(ofs.Object);
            (await sut.GetGuaranteeStatusesAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task GetGuaranteeTypesAsync_PathDoesNotExist_ReturnsEmpty()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetLocalDWDatabasePath(It.IsAny<string>())).Returns((string)null);

            var sut = new LookupService(ofs.Object);
            (await sut.GetGuaranteeTypesAsync()).Should().BeEmpty();
        }
    }
}
