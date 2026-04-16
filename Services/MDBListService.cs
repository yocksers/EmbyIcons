using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        private static Timer? _cacheCleanupTimer;
        private static readonly object _timerLock = new object();
        private const int MAX_CACHE_ENTRIES = 1000;

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
                await _httpConcurrencyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                var url = $"https://api.mdblist.com/tmdb/{mediaType}/{Uri.EscapeDataString(tmdbId)}";
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                requestMessage.Headers.Add("X-Api-Key", apiKey);
                
                var response = await _httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var jsonDoc = JsonDocument.Parse(content);
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
                    
                    if (source.Contains(StringConstants.MdbListPopcornSource) || source.Contains(StringConstants.MdbListAudienceSource))
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
                    else if (source.Contains(StringConstants.MdbListMyAnimeListSource) || source.Contains(StringConstants.MdbListMalSource))
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
    }

    internal class MDBListRatingData
    {
        public float? PopcornScore { get; set; }
        public int? PopcornVotes { get; set; }
        public float? MyAnimeListScore { get; set; }
    }
}
