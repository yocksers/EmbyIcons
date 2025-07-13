using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal static readonly ConcurrentDictionary<Guid, EpisodeIconInfo> _episodeIconCache = new();
        private const int MaxEpisodeCacheSize = 2000;

        public record EpisodeIconInfo
        {
            public HashSet<string> AudioLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubtitleLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Tags { get; init; } = new();
            public string? ChannelIconName { get; init; }
            public string? VideoFormatIconName { get; init; }
            public string? ResolutionIconName { get; init; }
            public long DateModifiedTicks { get; init; }
            public DateTime DateCached { get; init; }
        }

        /// <param name="episodeId">The ID of the episode to clear from the cache.</param>
        public void ClearEpisodeIconCache(Guid episodeId)
        {
            if (episodeId == Guid.Empty) return;

            if (_episodeIconCache.TryRemove(episodeId, out _))
            {
                _logger.Info($"[EmbyIcons] Event handler cleared icon info cache for item ID: {episodeId}");
            }
        }

        internal void PruneEpisodeCache()
        {
            if (_episodeIconCache.Count > MaxEpisodeCacheSize)
            {
                var toRemove = _episodeIconCache.Count - MaxEpisodeCacheSize;
                if (toRemove <= 0) return;

                var oldest = _episodeIconCache.ToArray()
                    .OrderBy(kvp => kvp.Value.DateCached)
                    .Take(toRemove);

                foreach (var item in oldest)
                {
                    _episodeIconCache.TryRemove(item.Key, out _);
                }
                _logger.Info($"[EmbyIcons] Pruned {toRemove} items from the episode icon cache.");
            }
        }
    }
}
