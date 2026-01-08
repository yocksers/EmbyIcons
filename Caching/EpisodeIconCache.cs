using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal static MemoryCache? _episodeIconCache;
        private static readonly object _episodeCacheInitLock = new object();

        private static int MaxEpisodeCacheSize => Plugin.Instance?.Configuration.MaxEpisodeCacheSize ?? 2000;
        internal static int EpisodeCacheSlidingExpirationHours => Plugin.Instance?.Configuration.EpisodeCacheSlidingExpirationHours ?? 6;
        internal static void EnsureEpisodeCacheInitialized()
        {
            if (_episodeIconCache == null)
            {
                lock (_episodeCacheInitLock)
                {
                    if (_episodeIconCache == null)
                    {
                        _episodeIconCache = new MemoryCache(new MemoryCacheOptions
                        {
                            SizeLimit = MaxEpisodeCacheSize
                        });
                    }
                }
            }
        }

        public record EpisodeIconInfo
        {
            public HashSet<string> AudioLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubtitleLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SourceIcons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public string? ChannelIconName { get; init; }
            public string? VideoFormatIconName { get; init; }
            public string? ResolutionIconName { get; init; }
            public string? AspectRatioIconName { get; init; }
            public string? ParentalRatingIconName { get; init; }
            public string? FrameRateIconName { get; init; }
            public float? RottenTomatoesRating { get; init; }
            public long DateModifiedTicks { get; init; }
        }

        public void ClearEpisodeIconCache(Guid episodeId)
        {
            if (episodeId == Guid.Empty) return;

            EnsureEpisodeCacheInitialized();
            _episodeIconCache?.Remove(episodeId);
            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
            {
                _logger.Debug($"[EmbyIcons] Event handler cleared icon info cache for item ID: {episodeId}");
            }
        }

        public void ClearAllEpisodeCaches()
        {
            var oldCache = _episodeIconCache;
            _episodeIconCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = MaxEpisodeCacheSize
            });
            
            if (oldCache != null)
            {
                try { oldCache.Dispose(); } 
                catch (Exception ex) 
                { 
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        Plugin.Instance?.Logger.Debug($"[EmbyIcons] Error disposing old episode cache: {ex.Message}"); 
                }
            }
        }

    }
}