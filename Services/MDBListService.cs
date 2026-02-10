using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace EmbyIcons.Services
{
    internal class MDBListService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly Dictionary<string, CachedRatingData> _ratingsCache = new Dictionary<string, CachedRatingData>();
        private static readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);
        private static Timer? _cacheCleanupTimer;
        private static readonly object _timerLock = new object();
        private const int MAX_CACHE_ENTRIES = 1000;

        static MDBListService()
        {
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
                _cacheLock.Wait();
                try
                {
                    var now = DateTime.UtcNow;
                    var keysToRemove = _ratingsCache
                        .Where(kvp => now - kvp.Value.CachedAt > CacheExpiration)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        _ratingsCache.Remove(key);
                    }

                    // Also enforce max size limit by removing oldest entries
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

        public MDBListRatingData? FetchRatings(BaseItem item, string apiKey)
        {
            try
            {
                return FetchRatingsAsync(item, apiKey, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                return null;
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

            var mediaType = item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : "show";
            var cacheKey = $"mdblist_{mediaType}_{tmdbId}";

            await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_ratingsCache.TryGetValue(cacheKey, out var cachedData))
                {
                    if (DateTime.UtcNow - cachedData.CachedAt < CacheExpiration)
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
                var url = $"https://api.mdblist.com/tmdb/{mediaType}/{tmdbId}?apikey={apiKey}";
                
                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jsonDoc = JsonDocument.Parse(content);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("ratings", out var ratingsArray))
                {
                    return null;
                }

                float? popcornScore = null;
                int? popcornVotes = null;
                float? myAnimeListScore = null;

                foreach (var rating in ratingsArray.EnumerateArray())
                {
                    if (!rating.TryGetProperty("source", out var sourceElement))
                        continue;

                    var source = sourceElement.GetString()?.ToLowerInvariant() ?? string.Empty;
                    
                    if (source.Contains("popcorn") || source.Contains("audience"))
                    {
                        if (rating.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Number)
                        {
                            popcornScore = (float)valueElement.GetDouble();
                        }

                        if (rating.TryGetProperty("votes", out var votesElement) && votesElement.ValueKind == JsonValueKind.Number)
                        {
                            popcornVotes = votesElement.GetInt32();
                        }
                    }
                    else if (source.Contains("myanimelist") || source.Contains("mal"))
                    {
                        if (rating.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Number)
                        {
                            myAnimeListScore = (float)valueElement.GetDouble();
                        }
                    }
                }

                var result = new MDBListRatingData
                {
                    PopcornScore = popcornScore,
                    PopcornVotes = popcornVotes,
                    MyAnimeListScore = myAnimeListScore
                };

                await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _ratingsCache[cacheKey] = new CachedRatingData
                    {
                        Data = result,
                        CachedAt = DateTime.UtcNow
                    };
                }
                finally
                {
                    _cacheLock.Release();
                }

                return result;
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Info($"[EmbyIcons] Error fetching MDBList ratings for {tmdbId}: {ex.Message}");
                }
                return null;
            }
        }

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

        public static void ClearCache()
        {
            _cacheLock.Wait();
            try
            {
                _ratingsCache.Clear();
            }
            finally
            {
                _cacheLock.Release();
            }
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
        }
    }

    internal class CachedRatingData
    {
        public MDBListRatingData Data { get; set; } = new MDBListRatingData();
        public DateTime CachedAt { get; set; }
    }

    internal class MDBListRatingData
    {
        public float? PopcornScore { get; set; }
        public int? PopcornVotes { get; set; }
        public float? MyAnimeListScore { get; set; }
    }
}
