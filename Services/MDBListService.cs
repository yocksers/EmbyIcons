using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        private static readonly Dictionary<string, CachedRatingData> _ratingsCache = new Dictionary<string, CachedRatingData>(StringComparer.Ordinal);
        private static readonly object _cacheGate = new object();
        private static readonly ConcurrentDictionary<string, Lazy<Task<MDBListRatingData?>>> _inFlight = new ConcurrentDictionary<string, Lazy<Task<MDBListRatingData?>>>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> _failedUntil = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim _httpConcurrencyLock = new SemaphoreSlim(4, 4);
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);
        private const int MaxCacheEntries = 50000;
        private const string DiskCacheFileName = "mdblist-ratings.json";
        private const int DiskCacheFormatVersion = 1;
        private static Timer? _maintenanceTimer;
        private static readonly object _timerLock = new object();
        private static readonly object _diskGate = new object();
        private static bool _diskCacheLoaded;
        private static bool _dirty;

        static MDBListService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyIcons/1.0");
            lock (_timerLock)
            {
                if (_maintenanceTimer == null)
                {
                    _maintenanceTimer = new Timer(_ => RunMaintenance(), null, MaintenanceInterval, MaintenanceInterval);
                }
            }
        }

        private static bool IsDiskCacheEnabled => Plugin.Instance?.Configuration.EnableMdbListDiskCache ?? false;

        private static string? GetDiskCachePath()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, DiskCacheFileName);
        }

        private static void RunMaintenance()
        {
            try
            {
                PruneCache();

                if (IsDiskCacheEnabled)
                {
                    SaveDiskCache();
                }
            }
            catch (Exception ex)
            {
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Error during MDBList cache maintenance: {ex.Message}");
                }
            }
        }

        private static void PruneCache()
        {
            var now = DateTime.UtcNow;
            int removed = 0;

            lock (_cacheGate)
            {
                var expiredKeys = _ratingsCache
                    .Where(kvp => now - kvp.Value.CachedAt > CacheExpiration)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _ratingsCache.Remove(key);
                }
                removed += expiredKeys.Count;

                if (_ratingsCache.Count > MaxCacheEntries)
                {
                    var oldestKeys = _ratingsCache
                        .OrderBy(kvp => kvp.Value.CachedAt)
                        .Take(_ratingsCache.Count - MaxCacheEntries)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in oldestKeys)
                    {
                        _ratingsCache.Remove(key);
                    }
                    removed += oldestKeys.Count;
                }

                if (removed > 0)
                {
                    _dirty = true;
                }
            }

            foreach (var failure in _failedUntil.Where(kvp => kvp.Value <= now).ToList())
            {
                ((ICollection<KeyValuePair<string, DateTime>>)_failedUntil).Remove(failure);
            }

            if (removed > 0 && Helpers.PluginHelper.IsDebugLoggingEnabled)
            {
                Plugin.Instance?.Logger.Debug($"[EmbyIcons] MDBList cache pruned {removed} entries.");
            }
        }

        private static void EnsureDiskCacheLoaded()
        {
            if (_diskCacheLoaded || !IsDiskCacheEnabled) return;

            lock (_diskGate)
            {
                if (_diskCacheLoaded) return;
                _diskCacheLoaded = true;

                var path = GetDiskCachePath();
                if (path == null || !File.Exists(path)) return;

                try
                {
                    MDBListDiskCacheFile? file;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                    {
                        file = JsonSerializer.Deserialize<MDBListDiskCacheFile>(stream);
                    }
                    if (file?.Entries == null || file.Version != DiskCacheFormatVersion) return;

                    var now = DateTime.UtcNow;
                    int loaded = 0;
                    lock (_cacheGate)
                    {
                        foreach (var entry in file.Entries)
                        {
                            if (entry.Value?.Data == null || now - entry.Value.CachedAt > CacheExpiration) continue;
                            if (_ratingsCache.ContainsKey(entry.Key)) continue;

                            _ratingsCache[entry.Key] = entry.Value;
                            loaded++;
                        }
                    }

                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    {
                        Plugin.Instance?.Logger.Debug($"[EmbyIcons] Loaded {loaded} MDBList ratings from '{path}'.");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Instance?.Logger.Warn($"[EmbyIcons] The saved MDBList ratings could not be read and will be rebuilt: {ex.Message}");
                }
            }
        }

        private static void SaveDiskCache()
        {
            lock (_diskGate)
            {
                Dictionary<string, CachedRatingData> snapshot;
                lock (_cacheGate)
                {
                    if (!_dirty) return;
                    snapshot = new Dictionary<string, CachedRatingData>(_ratingsCache, StringComparer.Ordinal);
                    _dirty = false;
                }

                var path = GetDiskCachePath();
                if (path == null) return;

                var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        JsonSerializer.Serialize(stream, new MDBListDiskCacheFile { Version = DiskCacheFormatVersion, Entries = snapshot });
                    }

                    if (File.Exists(path))
                    {
                        File.Replace(tempPath, path, null);
                    }
                    else
                    {
                        File.Move(tempPath, path);
                    }
                }
                catch (Exception ex)
                {
                    lock (_cacheGate)
                    {
                        _dirty = true;
                    }

                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    {
                        Plugin.Instance?.Logger.Debug($"[EmbyIcons] Could not save MDBList ratings to '{path}': {ex.Message}");
                    }
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

            EnsureDiskCacheLoaded();

            lock (_cacheGate)
            {
                if (_ratingsCache.TryGetValue(cacheKey, out var cachedData))
                {
                    if (DateTime.UtcNow - cachedData.CachedAt < CacheExpiration)
                    {
                        return cachedData.Data;
                    }

                    _ratingsCache.Remove(cacheKey);
                    _dirty = true;
                }
            }

            if (_failedUntil.TryGetValue(cacheKey, out var retryAfter) && DateTime.UtcNow < retryAfter)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var fetch = _inFlight.GetOrAdd(cacheKey, key => new Lazy<Task<MDBListRatingData?>>(() => FetchAndStoreAsync(key, mediaType, tmdbId!, apiKey)));
            try
            {
                return await fetch.Value.ConfigureAwait(false);
            }
            finally
            {
                ((ICollection<KeyValuePair<string, Lazy<Task<MDBListRatingData?>>>>)_inFlight).Remove(new KeyValuePair<string, Lazy<Task<MDBListRatingData?>>>(cacheKey, fetch));
            }
        }

        private static async Task<MDBListRatingData?> FetchAndStoreAsync(string cacheKey, string mediaType, string tmdbId, string apiKey)
        {
            await _httpConcurrencyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var url = $"https://api.mdblist.com/tmdb/{mediaType}/{Uri.EscapeDataString(tmdbId)}?apikey={Uri.EscapeDataString(apiKey)}";
                using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(requestMessage).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var notFound = new MDBListRatingData();
                    StoreResult(cacheKey, notFound);
                    return notFound;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _failedUntil[cacheKey] = DateTime.UtcNow + FailureRetryInterval;
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    {
                        Plugin.Instance?.Logger.Debug($"[EmbyIcons] MDBList returned {(int)response.StatusCode} for {tmdbId}; retrying in {FailureRetryInterval.TotalMinutes:F0} minutes.");
                    }
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = ParseRatings(content);

                StoreResult(cacheKey, result);
                _failedUntil.TryRemove(cacheKey, out _);
                return result;
            }
            catch (Exception ex)
            {
                _failedUntil[cacheKey] = DateTime.UtcNow + FailureRetryInterval;
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Error fetching MDBList ratings for {tmdbId}: {ex.Message}");
                }
                return null;
            }
            finally
            {
                _httpConcurrencyLock.Release();
            }
        }

        private static MDBListRatingData ParseRatings(string content)
        {
            using var jsonDoc = JsonDocument.Parse(content);
            var root = jsonDoc.RootElement;

            float? popcornScore = null;
            int? popcornVotes = null;
            float? myAnimeListScore = null;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ratings", out var ratingsArray) &&
                ratingsArray.ValueKind == JsonValueKind.Array)
            {
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
            }

            return new MDBListRatingData
            {
                PopcornScore = popcornScore,
                PopcornVotes = popcornVotes,
                MyAnimeListScore = myAnimeListScore
            };
        }

        private static void StoreResult(string cacheKey, MDBListRatingData data)
        {
            lock (_cacheGate)
            {
                _ratingsCache[cacheKey] = new CachedRatingData
                {
                    Data = data,
                    CachedAt = DateTime.UtcNow
                };
                _dirty = true;
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
                    _maintenanceTimer?.Dispose();
                    _maintenanceTimer = null;
                }
                catch { }
            }

            try
            {
                if (IsDiskCacheEnabled)
                {
                    SaveDiskCache();
                }
            }
            catch { }
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

    internal class MDBListDiskCacheFile
    {
        public int Version { get; set; }
        public Dictionary<string, CachedRatingData> Entries { get; set; } = new Dictionary<string, CachedRatingData>();
    }
}
