using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal static readonly ConcurrentDictionary<string, EpisodeIconInfo> _episodeIconCache = new();
        private const int MaxEpisodeCacheSize = 5000;

        public record EpisodeIconInfo
        {
            public HashSet<string> AudioLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubtitleLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public string? ChannelIconName { get; init; }
            public string? VideoFormatIconName { get; init; }
            public string? ResolutionIconName { get; init; }
            public string? AspectRatioIconName { get; init; }
            public string? ParentalRatingIconName { get; init; }
            public long DateModifiedTicks { get; init; }
            public DateTime DateCached { get; init; }
        }

        public EpisodeIconInfo GetOrAddEpisodeIconInfo(string key, Func<string, EpisodeIconInfo> factory)
        {
            if (_episodeIconCache.TryGetValue(key, out var info))
            {
                return info;
            }

            var newInfo = factory(key);
            _episodeIconCache[key] = newInfo;

            PruneEpisodeCache();

            return newInfo;
        }


        /// <param name="episodeId">The ID of the episode to clear from the cache.</param>
        public void ClearEpisodeIconCache(Guid episodeId)
        {
            if (episodeId == Guid.Empty) return;

            var keysToRemove = _episodeIconCache.Keys.Where(k => k.StartsWith(episodeId.ToString())).ToList();
            foreach (var key in keysToRemove)
            {
                if (_episodeIconCache.TryRemove(key, out _))
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Cleared episode cache for key: {key}");
                }
            }
        }

        internal void PruneEpisodeCache()
        {
            if (_episodeIconCache.Count > MaxEpisodeCacheSize)
            {
                Task.Run(() =>
                {
                    var itemsToRemoveCount = _episodeIconCache.Count - MaxEpisodeCacheSize;
                    if (itemsToRemoveCount <= 0) return;

                    var oldestKeys = _episodeIconCache.ToArray()
                        .OrderBy(kvp => kvp.Value.DateCached)
                        .Take(itemsToRemoveCount)
                        .Select(kvp => kvp.Key);

                    foreach (var key in oldestKeys)
                    {
                        _episodeIconCache.TryRemove(key, out _);
                    }

                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Pruned {itemsToRemoveCount} items from the episode icon cache to maintain size limits.");
                });
            }
        }
    }
}