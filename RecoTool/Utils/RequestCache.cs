using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;

namespace RecoTool.Utils
{
    public class RequestCache
    {
        // stocke key → (json, expiration)
        private class CacheEntry
        {
            public string Json;
            public DateTime Expiration;
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _dict
            = new ConcurrentDictionary<string, CacheEntry>();

        // Génère une clé unique pour la requête (méthode + URL + body)
        private string Key(HttpRequestMessage req)
        {
            var body = req.Content == null
                ? ""
                : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string url = req.RequestUri.ToString();
            if (req.RequestUri.IsAbsoluteUri)
                url = req.RequestUri.AbsolutePath;
            return $"{req.Method}:{url}:{body}";
        }

        // Essaie de récupérer ; renvoie true si trouvé ET non expiré
        public bool TryGet(HttpRequestMessage req, out string json)
        {
            var key = Key(req);
            if (_dict.TryGetValue(key, out var entry)
             && entry.Expiration > DateTime.UtcNow)
            {
                json = entry.Json;
                return true;
            }
            json = null;
            return false;
        }

        // Stocke pour une durée ttl
        public void Set(HttpRequestMessage req, string json)
        {
            var key = Key(req);
            var entry = new CacheEntry
            {
                Json = json,
                Expiration = DateTime.UtcNow.Add(TimeSpan.FromMinutes(15))
            };
            _dict.AddOrUpdate(key, entry, (_, __) => entry);
        }
    }

}
