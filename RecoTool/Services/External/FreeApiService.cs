using RecoTool.API;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RecoTool.Services.External
{
    // Production-friendly wrapper that handles authentication, throttling and caching.
    // It delegates actual HTTP calls to an inner client (mock or real) implementing IFreeApiClient.
    public sealed class FreeApiService : IFreeApiClient
    {
        private readonly IFreeApiClient _inner;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(2, 2); // max 2 concurrent
        private volatile bool _isAuthenticated;
        private readonly ConcurrentDictionary<string, Task<string>> _cache =
            new ConcurrentDictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);

        public FreeApiService() : this(new Free()) { }
        public FreeApiService(IFreeApiClient inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public async Task<bool> AuthenticateAsync()
        {
            try
            {
                // Delegate to inner if it supports auth; otherwise just mark as true
                var ok = true;
                try { ok = await _inner.AuthenticateAsync().ConfigureAwait(false); } catch { ok = true; }
                _isAuthenticated = ok;
                return ok;
            }
            catch
            {
                _isAuthenticated = false;
                return false;
            }
        }
        public bool IsAuthenticated => _isAuthenticated;

        public async Task<string> SearchAsync(DateTime day, string reference, string cntServiceCode, CancellationToken cancellationToken = default)
        {
            // Ensure authenticated once
            if (!_isAuthenticated)
            {
                try { await AuthenticateAsync().ConfigureAwait(false); } catch { }
                if (!_isAuthenticated)
                {
                    // Authentication failed: per requirement, do not call Free API, return null
                    return null;
                }
            }

            var key = BuildKey(day, reference, cntServiceCode);
            
            // SECURE: Use AsyncLazy pattern to avoid caching exceptions
            // GetOrAdd can return a Lazy whose Value failed, caching the exception forever.
            // Instead, we create a new task each time if the previous one failed.
            if (_cache.TryGetValue(key, out var cachedTask) && cachedTask.Status == TaskStatus.RanToCompletion)
            {
                return cachedTask.Result;
            }
            
            // Remove failed entry if exists
            _cache.TryRemove(key, out _);
            
            // Start new request
            var task = ExecuteThrottledAsync(day, reference, cntServiceCode, cancellationToken);
            _cache[key] = task;
            
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch
            {
                // On failure, drop cache entry to allow retry later
                _cache.TryRemove(key, out _);
                throw;
            }
        }

        private async Task<string> ExecuteThrottledAsync(DateTime day, string reference, string cntServiceCode, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await _inner.SearchAsync(day, reference, cntServiceCode, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string BuildKey(DateTime day, string reference, string cntServiceCode)
        {
            var d = day.Date.ToString("yyyy-MM-dd");
            var r = reference?.Trim() ?? string.Empty;
            var s = cntServiceCode?.Trim() ?? string.Empty;
            return d + "|" + r + "|" + s;
        }
    }
}
