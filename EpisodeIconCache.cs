using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory; 

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal static MemoryCache _episodeIconCache;

        private const int MaxEpisodeCacheSize = 2000;

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
        }

        public void ClearEpisodeIconCache(Guid episodeId)
        {
            if (episodeId == Guid.Empty) return;

            _episodeIconCache.Remove(episodeId);
            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
            {
                _logger.Debug($"[EmbyIcons] Event handler cleared icon info cache for item ID: {episodeId}");
            }
        }

    }
}