using System;
using System.Threading.Tasks;
using FluentAssertions;
using RecoTool.Services.Cache;
using Xunit;

namespace RecoTool.Tests.Services.Cache
{
    /// <summary>
    /// Tests pour <see cref="CacheService"/> (singleton). Couvre Set/TryGet/Invalidate,
    /// GetOrLoad/GetOrLoadAsync (sync &amp; async, miss puis hit), expiration, prefix,
    /// et gardes de validation des arguments.
    /// </summary>
    public class CacheServiceTests : IDisposable
    {
        private readonly string _prefix = "TST_" + Guid.NewGuid().ToString("N") + "_";

        public void Dispose()
        {
            CacheService.Instance.InvalidateByPrefix(_prefix);
        }

        // ----- Singleton -----

        [Fact]
        public void Instance_IsSingleton()
        {
            CacheService.Instance.Should().BeSameAs(CacheService.Instance);
        }

        // ----- Set / TryGet / Invalidate -----

        [Fact]
        public void TryGet_Miss_ReturnsFalse()
        {
            var key = _prefix + "miss";
            CacheService.Instance.TryGet<string>(key, out var v).Should().BeFalse();
            v.Should().BeNull();
        }

        [Fact]
        public void Set_ThenTryGet_Hit()
        {
            var key = _prefix + "k1";
            CacheService.Instance.Set(key, "value");
            CacheService.Instance.TryGet<string>(key, out var v).Should().BeTrue();
            v.Should().Be("value");
        }

        [Fact]
        public void TryGet_NullKey_ReturnsFalse()
        {
            CacheService.Instance.TryGet<string>(null, out _).Should().BeFalse();
            CacheService.Instance.TryGet<string>("   ", out _).Should().BeFalse();
        }

        [Fact]
        public void Invalidate_RemovesEntry()
        {
            var key = _prefix + "k2";
            CacheService.Instance.Set(key, 42);
            CacheService.Instance.Invalidate(key);
            CacheService.Instance.TryGet<int>(key, out _).Should().BeFalse();
        }

        [Fact]
        public void InvalidateByPrefix_RemovesMatchingEntries()
        {
            CacheService.Instance.Set(_prefix + "a", 1);
            CacheService.Instance.Set(_prefix + "b", 2);
            CacheService.Instance.Set("OTHER_" + _prefix, 3); // ne commence pas par le prefix

            CacheService.Instance.InvalidateByPrefix(_prefix);

            CacheService.Instance.TryGet<int>(_prefix + "a", out _).Should().BeFalse();
            CacheService.Instance.TryGet<int>(_prefix + "b", out _).Should().BeFalse();
            CacheService.Instance.TryGet<int>("OTHER_" + _prefix, out _).Should().BeTrue();

            CacheService.Instance.Invalidate("OTHER_" + _prefix);
        }

        [Fact]
        public void Set_NullKey_Throws()
        {
            Action a = () => CacheService.Instance.Set<int>(null, 1);
            a.Should().Throw<ArgumentException>();
        }

        // ----- GetOrLoad (sync) -----

        [Fact]
        public void GetOrLoad_MissThenHit_LoaderCalledOnce()
        {
            var key = _prefix + "load1";
            int calls = 0;

            var v1 = CacheService.Instance.GetOrLoad(key, () => { calls++; return "first"; });
            var v2 = CacheService.Instance.GetOrLoad(key, () => { calls++; return "second"; });

            v1.Should().Be("first");
            v2.Should().Be("first");   // valeur en cache, pas rappel
            calls.Should().Be(1);
        }

        [Fact]
        public void GetOrLoad_NullLoader_Throws()
        {
            Action a = () => CacheService.Instance.GetOrLoad<string>(_prefix + "x", null);
            a.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void GetOrLoad_NullKey_Throws()
        {
            Action a = () => CacheService.Instance.GetOrLoad<string>(null, () => "x");
            a.Should().Throw<ArgumentException>();
        }

        // ----- GetOrLoadAsync -----

        [Fact]
        public async Task GetOrLoadAsync_MissThenHit_LoaderCalledOnce()
        {
            var key = _prefix + "async1";
            int calls = 0;

            var v1 = await CacheService.Instance.GetOrLoadAsync(key, async () =>
            {
                calls++;
                await Task.Yield();
                return 42;
            });
            var v2 = await CacheService.Instance.GetOrLoadAsync(key, async () =>
            {
                calls++;
                await Task.Yield();
                return 99;
            });

            v1.Should().Be(42);
            v2.Should().Be(42);
            calls.Should().Be(1);
        }

        // ----- Expiration -----

        [Fact]
        public async Task Set_WithExpiration_EntryEvictedAfterTimeout()
        {
            var key = _prefix + "exp";
            CacheService.Instance.Set(key, "v", TimeSpan.FromMilliseconds(50));
            CacheService.Instance.TryGet<string>(key, out _).Should().BeTrue();

            await Task.Delay(120);
            CacheService.Instance.TryGet<string>(key, out _).Should().BeFalse();
        }

        [Fact]
        public void GetStats_ReturnsCounts()
        {
            CacheService.Instance.Set(_prefix + "stat", "x");
            var stats = CacheService.Instance.GetStats();
            stats.TotalEntries.Should().BeGreaterOrEqualTo(1);
        }
    }
}
