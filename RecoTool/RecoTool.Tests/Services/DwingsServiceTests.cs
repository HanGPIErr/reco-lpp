using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests unitaires pour <see cref="DwingsService"/> exploitant la nouvelle
    /// surface <see cref="IOfflineFirstService"/>. Couvre les chemins absents
    /// (PrimeCaches → liste vide) et l'invalidation des caches statiques.
    /// </summary>
    public class DwingsServiceTests
    {
        [Fact]
        public void Ctor_AcceptsNullDependency_DoesNotThrow()
        {
            // Le ctor ne valide pas le paramètre (cf. impl.) — donc null est accepté.
            // Lever une exception serait préférable, mais on documente le comportement actuel.
            Action a = () => new DwingsService(null);
            a.Should().NotThrow();
        }

        [Fact]
        public async Task PrimeCachesAsync_NullPath_LeavesEmptyCaches()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetLocalDWDatabasePath(It.IsAny<string>())).Returns((string)null);

            var sut = new DwingsService(ofs.Object);
            await sut.PrimeCachesAsync();

            (await sut.GetInvoicesAsync()).Should().BeEmpty();
            (await sut.GetGuaranteesAsync()).Should().BeEmpty();
        }

        [Fact]
        public async Task PrimeCachesAsync_PathDoesNotExist_LeavesEmptyCaches()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetLocalDWDatabasePath(It.IsAny<string>())).Returns(@"Z:\does\not\exist.accdb");

            var sut = new DwingsService(ofs.Object);
            await sut.PrimeCachesAsync();

            (await sut.GetInvoicesAsync()).Should().BeEmpty();
            (await sut.GetGuaranteesAsync()).Should().BeEmpty();
        }

        [Fact]
        public void InvalidateCaches_DoesNotThrow_WhenNothingPrimed()
        {
            var sut = new DwingsService(Mock.Of<IOfflineFirstService>());
            Action a = () => sut.InvalidateCaches();
            a.Should().NotThrow();
        }

        [Fact]
        public void InvalidateSharedCacheForPath_NullOrEmpty_NoOp()
        {
            Action a1 = () => DwingsService.InvalidateSharedCacheForPath(null);
            Action a2 = () => DwingsService.InvalidateSharedCacheForPath("");
            Action a3 = () => DwingsService.InvalidateSharedCacheForPath("   ");
            a1.Should().NotThrow();
            a2.Should().NotThrow();
            a3.Should().NotThrow();
        }

        [Fact]
        public async Task LoadFromPathAsync_NullOrWhitespace_Throws()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => DwingsService.LoadFromPathAsync(null));
            await Assert.ThrowsAsync<ArgumentException>(() => DwingsService.LoadFromPathAsync(""));
            await Assert.ThrowsAsync<ArgumentException>(() => DwingsService.LoadFromPathAsync("   "));
        }

        [Fact]
        public async Task LoadFromPathAsync_FileMissing_ThrowsFileNotFound()
        {
            var fakePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".accdb");
            await Assert.ThrowsAsync<FileNotFoundException>(() => DwingsService.LoadFromPathAsync(fakePath));
        }
    }
}
