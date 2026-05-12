using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace RecoTool.Services.Cache
{
    /// <summary>
    /// Service de cache global avec invalidation explicite.
    /// Thread-safe et optimisé pour les accès concurrents.
    /// Par défaut, les entrées n'expirent jamais automatiquement — elles doivent être
    /// invalidées explicitement (Invalidate / InvalidateByPrefix / InvalidateAll), typiquement
    /// lors d'un import AMBRE ou d'un changement de pays. Une expiration peut toutefois être
    /// passée par appel (paramètre <c>expiration</c>) ; dans ce cas elle est gérée nativement
    /// par <see cref="IMemoryCache"/> via <see cref="MemoryCacheEntryOptions.AbsoluteExpirationRelativeToNow"/>.
    /// </summary>
    /// <remarks>
    /// Le stockage interne s'appuie sur <see cref="MemoryCache"/> (Microsoft.Extensions.Caching.Memory).
    /// Comme <see cref="IMemoryCache"/> n'expose pas d'énumération des clés, on maintient en parallèle
    /// un <see cref="ConcurrentDictionary{TKey, TValue}"/> de clés vivantes (trade-off : duplication
    /// mineure de la clé string ; alternative — <see cref="MemoryCache.Compact(double)"/> — n'autorise
    /// pas l'invalidation ciblée par préfixe). Le set des clés est tenu cohérent via un
    /// <see cref="PostEvictionDelegate"/> qui retire la clé lors de l'éviction par <see cref="IMemoryCache"/>
    /// (expiration, suppression, capacité).
    /// </remarks>
    public sealed class CacheService
    {
        private static readonly Lazy<CacheService> _instance =
            new Lazy<CacheService>(() => new CacheService(), LazyThreadSafetyMode.ExecutionAndPublication);

        public static CacheService Instance => _instance.Value;

        // SizeLimit conservateur : 10 000 entrées max. Chaque entrée déclare Size = 1.
        // Au-delà, IMemoryCache déclenchera sa propre éviction LRU/priority.
        private const long DefaultSizeLimit = 10_000L;

        private readonly IMemoryCache _cache;

        // Suivi explicite des clés vivantes pour supporter InvalidateByPrefix() et GetStats().
        // byte sert juste de placeholder (ConcurrentDictionary<string, byte> ≈ ConcurrentHashSet<string>).
        private readonly ConcurrentDictionary<string, byte> _liveKeys =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private readonly PostEvictionDelegate _onEviction;

        private CacheService()
        {
            _cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = DefaultSizeLimit
            });
            _onEviction = OnEviction;
        }

        private void OnEviction(object key, object value, EvictionReason reason, object state)
        {
            if (key is string s)
            {
                _liveKeys.TryRemove(s, out _);
            }
        }

        private MemoryCacheEntryOptions BuildOptions(TimeSpan? expiration)
        {
            var opts = new MemoryCacheEntryOptions
            {
                Size = 1
            };
            if (expiration.HasValue && expiration.Value != TimeSpan.MaxValue && expiration.Value > TimeSpan.Zero)
            {
                opts.AbsoluteExpirationRelativeToNow = expiration.Value;
            }
            opts.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
            {
                EvictionCallback = _onEviction
            });
            return opts;
        }

        private void StoreInternal<T>(string key, T value, TimeSpan? expiration)
        {
            // Réinsertion : MemoryCache.Set remplace l'entrée existante et déclenche l'éviction
            // de l'ancienne (avec EvictionReason.Replaced) — _liveKeys est nettoyé puis réajouté
            // ci-dessous. On force donc le ré-add dans _liveKeys après le Set.
            _cache.Set(key, value, BuildOptions(expiration));
            _liveKeys[key] = 0;
        }

        /// <summary>
        /// Récupère une valeur du cache ou la charge si absente/expirée (asynchrone).
        /// </summary>
        public async Task<T> GetOrLoadAsync<T>(string key, Func<Task<T>> loader, TimeSpan? expiration = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            if (loader == null)
                throw new ArgumentNullException(nameof(loader));

            if (_cache.TryGetValue(key, out object existing))
            {
                return (T)existing;
            }

            var value = await loader().ConfigureAwait(false);
            StoreInternal(key, value, expiration);
            return value;
        }

        /// <summary>
        /// Récupère une valeur du cache (synchrone) ou la charge si absente/expirée.
        /// </summary>
        public T GetOrLoad<T>(string key, Func<T> loader, TimeSpan? expiration = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            if (loader == null)
                throw new ArgumentNullException(nameof(loader));

            if (_cache.TryGetValue(key, out object existing))
            {
                return (T)existing;
            }

            var value = loader();
            StoreInternal(key, value, expiration);
            return value;
        }

        /// <summary>
        /// Tente de récupérer une valeur du cache.
        /// </summary>
        public bool TryGet<T>(string key, out T value)
        {
            value = default;

            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (_cache.TryGetValue(key, out object raw))
            {
                value = (T)raw;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Met en cache une valeur.
        /// </summary>
        public void Set<T>(string key, T value, TimeSpan? expiration = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            StoreInternal(key, value, expiration);
        }

        /// <summary>
        /// Invalide une entrée du cache.
        /// </summary>
        public void Invalidate(string key)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                _cache.Remove(key); // déclenche PostEviction -> _liveKeys nettoyé
            }
        }

        /// <summary>
        /// Invalide toutes les entrées du cache dont la clé commence par <paramref name="prefix"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="IMemoryCache"/> n'expose pas d'énumération des clés, on utilise donc le set
        /// <c>_liveKeys</c> maintenu en parallèle. La comparaison est insensible à la casse
        /// pour rester strictement compatible avec l'ancienne implémentation
        /// (<see cref="StringComparison.OrdinalIgnoreCase"/>).
        /// </remarks>
        public void InvalidateByPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return;

            // Snapshot des clés pour éviter de modifier la collection pendant l'itération.
            var snapshot = new List<string>(_liveKeys.Count);
            foreach (var k in _liveKeys.Keys)
            {
                if (k != null && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    snapshot.Add(k);
            }

            foreach (var k in snapshot)
            {
                _cache.Remove(k); // PostEviction met à jour _liveKeys
            }
        }

        /// <summary>
        /// Invalide toutes les entrées du cache.
        /// </summary>
        public void InvalidateAll()
        {
            // Snapshot puis Remove pour conserver des callbacks d'éviction propres
            // (et éviter de jeter le MemoryCache lui-même, qui resterait référencé via _cache).
            var snapshot = new List<string>(_liveKeys.Keys);
            foreach (var k in snapshot)
            {
                _cache.Remove(k);
            }
            // Filet de sécurité : si une clé avait été ajoutée hors-piste, on force le reset.
            _liveKeys.Clear();
        }

        /// <summary>
        /// Obtient les statistiques du cache.
        /// </summary>
        /// <remarks>
        /// Sous <see cref="IMemoryCache"/>, les entrées expirées sont évacuées passivement
        /// (au prochain accès ou via le scan interne) — la valeur d'<c>ExpiredEntries</c>
        /// est donc en général 0 ici ; elle est conservée pour rétro-compatibilité d'API.
        /// </remarks>
        public CacheStats GetStats()
        {
            int total = _liveKeys.Count;
            return new CacheStats
            {
                TotalEntries = total,
                FreshEntries = total,
                ExpiredEntries = 0,
                EstimatedSizeBytes = total * 100L
            };
        }

        public class CacheStats
        {
            public int TotalEntries { get; set; }
            public int FreshEntries { get; set; }
            public int ExpiredEntries { get; set; }
            public long EstimatedSizeBytes { get; set; }
        }
    }
}
