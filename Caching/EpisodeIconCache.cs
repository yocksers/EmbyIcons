using System;
using System.Collections.Generic;
using System.Threading;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal static volatile MemoryCache? _episodeIconCache;
        private static readonly object _episodeCacheInitLock = new object();

        private const int StreamHashCacheSize = 20000;
        private static volatile MemoryCache? _streamHashCache;

        private sealed class StreamHashEntry
        {
            public long DateModifiedTicks { get; set; }
            public string Hash { get; set; } = string.Empty;
        }

        private static MemoryCache GetStreamHashCache()
        {
            var cache = _streamHashCache;
            if (cache != null) return cache;

            lock (_episodeCacheInitLock)
            {
                return _streamHashCache ??= new MemoryCache(new MemoryCacheOptions { SizeLimit = StreamHashCacheSize });
            }
        }

        internal static string GetCachedItemMediaStreamHash(BaseItem item)
        {
            var cache = GetStreamHashCache();
            var ticks = item.DateModified.Ticks;

            if (item.Id != Guid.Empty &&
                cache.TryGetValue(item.Id, out StreamHashEntry? cached) &&
                cached != null &&
                cached.DateModifiedTicks == ticks)
            {
                return cached.Hash;
            }

            var streams = item.GetMediaStreams() ?? new List<MediaStream>();
            var hash = MediaStreamHelper.GetItemMediaStreamHashV2(item, streams);

            if (item.Id != Guid.Empty)
            {
                try
                {
                    cache.Set(item.Id, new StreamHashEntry { DateModifiedTicks = ticks, Hash = hash },
                        new MemoryCacheEntryOptions()
                            .SetSize(1)
                            .SetSlidingExpiration(TimeSpan.FromHours(EpisodeCacheSlidingExpirationHours)));
                }
                catch (ObjectDisposedException) { }
            }

            return hash;
        }

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
            internal List<Models.FilenameBasedIconData> FilenameBasedIcons { get; init; } = new();
            public HashSet<string> SourceIcons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public string? ChannelIconName { get; init; }
            public string? VideoFormatIconName { get; init; }
            public string? ResolutionIconName { get; init; }
            public string? AspectRatioIconName { get; init; }
            public string? ParentalRatingIconName { get; init; }
            public string? FrameRateIconName { get; init; }
            public string? OriginalLanguageIconName { get; init; }
            public string? SampleRateIconName { get; init; }
            public string? AudioBitRateIconName { get; init; }
            public string? BitDepthIconName { get; init; }
            public long DateModifiedTicks { get; init; }
        }

        public void ClearEpisodeIconCache(Guid episodeId)
        {
            if (episodeId == Guid.Empty) return;

            EnsureEpisodeCacheInitialized();
            _episodeIconCache?.Remove(episodeId);
            _streamHashCache?.Remove(episodeId);
            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
            {
                _logger.Debug($"[EmbyIcons] Event handler cleared icon info cache for item ID: {episodeId}");
            }
        }

        public void ClearAllEpisodeCaches()
        {
            var newCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = MaxEpisodeCacheSize
            });

            var oldCache = Interlocked.Exchange(ref _episodeIconCache, newCache);

            var oldHashCache = Interlocked.Exchange(ref _streamHashCache, new MemoryCache(new MemoryCacheOptions { SizeLimit = StreamHashCacheSize }));
            try { oldHashCache?.Dispose(); } catch { }
            
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