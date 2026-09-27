using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Compat;
using EmbyIcons.Configuration;
using MediaBrowser.Controller.Entities;

namespace EmbyIcons.Services
{
    internal class MDBListService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        private static readonly Dictionary<string, CachedRatingData> _ratingsCache = new Dictionary<string, CachedRatingData>();
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim _httpConcurrencyLock = new SemaphoreSlim(4, 4);
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);
        private static readonly TimeSpan NotFoundCacheExpiration = TimeSpan.FromHours(6);
        private static readonly TimeSpan ErrorCacheExpiration = TimeSpan.FromMinutes(15);
        private static Timer? _cacheCleanupTimer;
        private static readonly object _timerLock = new object();
        private const int MAX_CACHE_ENTRIES = 20000;

        static MDBListService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyIcons/1.0");
            lock (_timerLock)
            {
                if (_cacheCleanupTimer == null)
                {
                    _cacheCleanupTimer = new Timer(_ => PruneExpiredCacheEntries(), null, 
                        TimeSpan.FromHours(1), TimeSpan.FromHours(1));
                }
            }
        }

        private static void PruneExpiredCacheEntries()
        {
            try
            {
                if (!_cacheLock.Wait(0)) return;
                try
                {
                    var now = DateTime.UtcNow;
                    var keysToRemove = _ratingsCache
                        .Where(kvp => now - kvp.Value.CachedAt > kvp.Value.Ttl)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        _ratingsCache.Remove(key);
                    }

                    if (_ratingsCache.Count > MAX_CACHE_ENTRIES)
                    {
                        var oldestKeys = _ratingsCache
                            .OrderBy(kvp => kvp.Value.CachedAt)
                            .Take(_ratingsCache.Count - MAX_CACHE_ENTRIES)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in oldestKeys)
                        {
                            _ratingsCache.Remove(key);
                        }
                    }

                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    {
                        Plugin.Instance.Logger.Debug($"[EmbyIcons] MDBList cache pruned. Removed {keysToRemove.Count} expired entries. Current size: {_ratingsCache.Count}");
                    }
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Error during MDBList cache cleanup: {ex.Message}");
                }
            }
        }

        public async Task<MDBListRatingData?> FetchRatingsAsync(BaseItem item, string apiKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            var tmdbId = GetTmdbId(item);
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return null;
            }

            var mediaType = item is MediaBrowser.Controller.Entities.Movies.Movie ? StringConstants.MediaTypeMovie : StringConstants.MediaTypeShow;
            var cacheKey = $"mdblist_{mediaType}_{tmdbId}";

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ratingsCache.TryGetValue(cacheKey, out var cachedData))
                {
                    if (DateTime.UtcNow - cachedData.CachedAt < cachedData.Ttl)
                    {
                        return cachedData.Data;
                    }
                    else
                    {
                        _ratingsCache.Remove(cacheKey);
                    }
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            try
            {
                await _httpConcurrencyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                var url = $"https://api.mdblist.com/tmdb/{mediaType}/{Uri.EscapeDataString(tmdbId)}?apikey={Uri.EscapeDataString(apiKey)}";
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                
                using var response = await _httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await StoreAsync(cacheKey, new MDBListRatingData(), NotFoundCacheExpiration, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var root = SimpleJson.Parse(content) as Dictionary<string, object?>;

                if (root == null || !root.TryGetValue("ratings", out var ratingsValue) || ratingsValue is not List<object?> ratingsArray)
                {
                    await StoreAsync(cacheKey, new MDBListRatingData(), NotFoundCacheExpiration, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                float? popcornScore = null;
                int? popcornVotes = null;
                float? myAnimeListScore = null;

                foreach (var ratingValue in ratingsArray)
                {
                    if (ratingValue is not Dictionary<string, object?> rating)
                        continue;

                    if (!rating.TryGetValue("source", out var sourceValue) || sourceValue is not string sourceText)
                        continue;

                    var source = sourceText.ToLowerInvariant();

                    if (source.Contains(StringConstants.MdbListPopcornSource) || source.Contains(StringConstants.MdbListAudienceSource))
                    {
                        if (rating.TryGetValue("value", out var valueRaw) && IsNumber(valueRaw))
                        {
                            popcornScore = (float)Convert.ToDouble(valueRaw, System.Globalization.CultureInfo.InvariantCulture);
                        }

                        if (rating.TryGetValue("votes", out var votesRaw) && IsNumber(votesRaw))
                        {
                            popcornVotes = Convert.ToInt32(votesRaw, System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    else if (source.Contains(StringConstants.MdbListMyAnimeListSource) || source.Contains(StringConstants.MdbListMalSource))
                    {
                        if (rating.TryGetValue("value", out var valueRaw) && IsNumber(valueRaw))
                        {
                            myAnimeListScore = (float)Convert.ToDouble(valueRaw, System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                }

                var result = new MDBListRatingData
                {
                    PopcornScore = popcornScore,
                    PopcornVotes = popcornVotes,
                    MyAnimeListScore = myAnimeListScore
                };

                await StoreAsync(cacheKey, result, CacheExpiration, cancellationToken).ConfigureAwait(false);

                return result;
                }
                finally
                {
                    _httpConcurrencyLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Info($"[EmbyIcons] Error fetching MDBList ratings for {tmdbId}: {ex.Message}");
                }

                try { await StoreAsync(cacheKey, new MDBListRatingData(), ErrorCacheExpiration, CancellationToken.None).ConfigureAwait(false); }
                catch { }

                return null;
            }
        }

        private static async Task StoreAsync(string cacheKey, MDBListRatingData data, TimeSpan ttl, CancellationToken cancellationToken)
        {
            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ratingsCache.Count >= MAX_CACHE_ENTRIES && !_ratingsCache.ContainsKey(cacheKey))
                {
                    string? oldestKey = null;
                    var oldestTime = DateTime.MaxValue;
                    foreach (var kvp in _ratingsCache)
                    {
                        if (kvp.Value.CachedAt < oldestTime)
                        {
                            oldestTime = kvp.Value.CachedAt;
                            oldestKey = kvp.Key;
                        }
                    }

                    if (oldestKey != null)
                        _ratingsCache.Remove(oldestKey);
                }

                _ratingsCache[cacheKey] = new CachedRatingData
                {
                    Data = data,
                    CachedAt = DateTime.UtcNow,
                    Ttl = ttl
                };
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private static bool IsNumber(object? value) => value is long || value is double;

        private static string? GetTmdbId(BaseItem item)
        {
            if (item?.ProviderIds == null)
                return null;

            if (item.ProviderIds.TryGetValue("Tmdb", out var tmdbId))
            {
                return tmdbId;
            }

            return null;
        }

        public static void Dispose()
        {
            lock (_timerLock)
            {
                try
                {
                    _cacheCleanupTimer?.Dispose();
                    _cacheCleanupTimer = null;
                }
                catch { }
            }

            try { _cacheLock.Dispose(); } catch { }
            try { _httpConcurrencyLock.Dispose(); } catch { }
        }
    }

    internal class CachedRatingData
    {
        public MDBListRatingData Data { get; set; } = new MDBListRatingData();
        public DateTime CachedAt { get; set; }
        public TimeSpan Ttl { get; set; }
    }

    internal class MDBListRatingData
    {
        public float? PopcornScore { get; set; }
        public int? PopcornVotes { get; set; }
        public float? MyAnimeListScore { get; set; }
    }
}
