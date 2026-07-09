using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal static readonly ConcurrentDictionary<Guid, AggregatedAlbumResult> _albumAggregationCache = new();
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _albumAggregationLocks = new();
        private static int _albumAdditionsCounter = 0;
        private const int ALBUM_CACHE_SIZE_CHECK_FREQUENCY = 50;

        private static int MaxAlbumCacheSize => Plugin.Instance?.Configuration.MaxAlbumCacheSize ?? 2000;

        internal record AggregatedAlbumResult
        {
            public HashSet<string> AudioLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public string? ChannelType { get; init; }
            public string? SampleRate { get; init; }
            public string? AudioBitRate { get; init; }
            public string? BitDepth { get; init; }
            public List<FilenameBasedIconData> FilenameBasedIcons { get; init; } = new();
            public string CombinedTracksHashShort { get; init; } = "";
            public DateTime Timestamp { get; init; } = DateTime.MinValue;
        }

        private void PruneAlbumAggregationCacheWithLimit()
        {
            var count = _albumAggregationCache.Count;
            if (count <= MaxAlbumCacheSize) return;

            var toRemove = count - MaxAlbumCacheSize;
            var entries = _albumAggregationCache.ToArray();
            Array.Sort(entries, (a, b) => a.Value.Timestamp.CompareTo(b.Value.Timestamp));
            var keysToRemove = new Guid[toRemove];
            for (int k = 0; k < toRemove; k++) keysToRemove[k] = entries[k].Key;

            foreach (var key in keysToRemove)
                _albumAggregationCache.TryRemove(key, out _);

            if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                _logger.Debug($"[EmbyIcons] Pruned {keysToRemove.Length} items from the album aggregation cache.");
        }

        public void ClearAlbumAggregationCache(Guid albumId)
        {
            if (albumId != Guid.Empty && _albumAggregationCache.TryRemove(albumId, out _))
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    _logger.Debug($"[EmbyIcons] Cleared album aggregation cache for ID: {albumId}");
            }
        }

        internal AggregatedAlbumResult GetOrBuildAggregatedDataForAlbum(BaseItem parent, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (parent.Id == Guid.Empty)
                return new AggregatedAlbumResult();

            if (_albumAggregationCache.TryGetValue(parent.Id, out var cachedResult))
            {
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug($"[EmbyIcons] Using cached album aggregation for '{parent.Name}' ({parent.Id}).");
                return cachedResult;
            }

            var sem = _albumAggregationLocks.GetOrAdd(parent.Id, _ => new SemaphoreSlim(1, 1));
            sem.Wait();
            try
            {
                if (_albumAggregationCache.TryGetValue(parent.Id, out cachedResult))
                    return cachedResult;

                bool useLiteMode = profileOptions.UseMusicAlbumLiteMode;

                var query = new InternalItemsQuery
                {
                    Parent = parent,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Audio" },
                    Limit = useLiteMode ? 1 : null,
                    OrderBy = useLiteMode
                        ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
                        : Array.Empty<(string, SortOrder)>()
                };

                var itemList = _libraryManager.GetItemList(query).ToList();

                if (!itemList.Any())
                {
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _logger.Debug($"[EmbyIcons] No tracks found for '{parent.Name}'. Returning empty result without caching.");
                    return new AggregatedAlbumResult();
                }

                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug($"[EmbyIcons] Aggregating {itemList.Count} track(s) for '{parent.Name}'. LiteMode: {useLiteMode}.");

                bool checkAudioLangs   = profileOptions.AudioIconAlignment       != IconAlignment.Disabled;
                bool checkAudioCodecs  = profileOptions.AudioCodecIconAlignment  != IconAlignment.Disabled;
                bool checkChannels     = profileOptions.ChannelIconAlignment      != IconAlignment.Disabled;
                bool checkSampleRate   = profileOptions.SampleRateIconAlignment   != IconAlignment.Disabled;
                bool checkAudioBitRate = profileOptions.AudioBitRateIconAlignment != IconAlignment.Disabled;
                bool checkBitDepth     = profileOptions.BitDepthIconAlignment     != IconAlignment.Disabled;

                var firstItem = itemList[0];
                var firstStreams = firstItem.GetMediaStreams() ?? new List<MediaStream>();
                var primaryAudio = firstStreams
                    .Where(s => s.Type == MediaStreamType.Audio)
                    .OrderByDescending(s => s.Channels ?? 0)
                    .FirstOrDefault();

                var allAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (checkAudioLangs && primaryAudio != null)
                {
                    var lang = !string.IsNullOrEmpty(primaryAudio.DisplayLanguage) ? primaryAudio.DisplayLanguage : primaryAudio.Language;
                    if (!string.IsNullOrEmpty(lang)) allAudioLangs.Add(LanguageHelper.NormalizeLangCode(lang));
                }

                var commonAudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (checkAudioCodecs)
                {
                    foreach (var s in firstStreams.Where(s => s.Type == MediaStreamType.Audio))
                    {
                        var c = MediaStreamHelper.GetAudioCodecIconName(s);
                        if (c != null) commonAudioCodecs.Add(c);
                    }
                }

                string? commonChannelType  = checkChannels     && primaryAudio != null ? MediaStreamHelper.GetChannelIconName(primaryAudio)      : null;
                string? commonSampleRate   = checkSampleRate   && primaryAudio != null ? MediaStreamHelper.GetSampleRateIconName(primaryAudio)   : null;
                string? commonAudioBitRate = checkAudioBitRate && primaryAudio != null ? MediaStreamHelper.GetAudioBitRateIconName(primaryAudio) : null;
                string? commonBitDepth     = checkBitDepth     && primaryAudio != null ? MediaStreamHelper.GetBitDepthIconName(primaryAudio)     : null;

                var itemHashes = new List<string>(itemList.Count)
                {
                    $"{firstItem.Id}:{MediaStreamHelper.GetItemMediaStreamHash(firstItem, firstStreams)}"
                };

                for (int i = 1; i < itemList.Count; i++)
                {
                    bool allCommonExhausted =
                        (!checkAudioCodecs  || commonAudioCodecs.Count == 0) &&
                        (!checkChannels     || commonChannelType  == null) &&
                        (!checkSampleRate   || commonSampleRate   == null) &&
                        (!checkAudioBitRate || commonAudioBitRate == null) &&
                        (!checkBitDepth     || commonBitDepth     == null);

                    if (allCommonExhausted && !checkAudioLangs)
                        break;

                    var trackItem = itemList[i];
                    var streams = trackItem.GetMediaStreams() ?? new List<MediaStream>();
                    var trackPrimary = streams
                        .Where(s => s.Type == MediaStreamType.Audio)
                        .OrderByDescending(s => s.Channels ?? 0)
                        .FirstOrDefault();

                    if (checkAudioLangs && trackPrimary != null)
                    {
                        var lang = !string.IsNullOrEmpty(trackPrimary.DisplayLanguage) ? trackPrimary.DisplayLanguage : trackPrimary.Language;
                        if (!string.IsNullOrEmpty(lang)) allAudioLangs.Add(LanguageHelper.NormalizeLangCode(lang));
                    }

                    if (checkAudioCodecs && commonAudioCodecs.Any())
                    {
                        var trackCodecs = streams.Where(s => s.Type == MediaStreamType.Audio)
                            .Select(MediaStreamHelper.GetAudioCodecIconName)
                            .Where(c => c != null).Select(c => c!);
                        commonAudioCodecs.IntersectWith(trackCodecs);
                    }

                    if (checkChannels && commonChannelType != null)
                    {
                        var current = trackPrimary != null ? MediaStreamHelper.GetChannelIconName(trackPrimary) : null;
                        if (commonChannelType != current) commonChannelType = null;
                    }

                    if (checkSampleRate && commonSampleRate != null)
                    {
                        var current = trackPrimary != null ? MediaStreamHelper.GetSampleRateIconName(trackPrimary) : null;
                        if (commonSampleRate != current) commonSampleRate = null;
                    }

                    if (checkAudioBitRate && commonAudioBitRate != null)
                    {
                        var current = trackPrimary != null ? MediaStreamHelper.GetAudioBitRateIconName(trackPrimary) : null;
                        if (commonAudioBitRate != current) commonAudioBitRate = null;
                    }

                    if (checkBitDepth && commonBitDepth != null)
                    {
                        var current = trackPrimary != null ? MediaStreamHelper.GetBitDepthIconName(trackPrimary) : null;
                        if (commonBitDepth != current) commonBitDepth = null;
                    }

                    itemHashes.Add($"{trackItem.Id}:{MediaStreamHelper.GetItemMediaStreamHash(trackItem, streams)}");
                }

                // Filename-based icons from album/artist path and track paths
                var filenameBasedIconsList = new List<FilenameBasedIconData>();
                if (profileOptions.FilenameBasedIcons.Any())
                {
                    var uniqueIcons = new Dictionary<string, FilenameBasedIconData>();
                    var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(parent.Path))
                        allPaths.Add(parent.Path.ToLowerInvariant());

                    foreach (var trackItem in itemList)
                    {
                        if (!string.IsNullOrEmpty(trackItem.Path))
                            allPaths.Add(trackItem.Path.ToLowerInvariant());
                    }

                    bool isAlbum  = parent is MusicAlbum;
                    bool isArtist = parent is MusicArtist;

                    foreach (var path in allPaths)
                    {
                        foreach (var mapping in profileOptions.FilenameBasedIcons)
                        {
                            bool shouldApply =
                                (isAlbum  && (mapping.ApplyToAlbums || mapping.ApplyToTracks)) ||
                                (isArtist && (mapping.ApplyToArtists || mapping.ApplyToAlbums || mapping.ApplyToTracks));

                            if (shouldApply &&
                                !string.IsNullOrWhiteSpace(mapping.Keyword) &&
                                !string.IsNullOrWhiteSpace(mapping.IconName) &&
                                mapping.IconAlignment != IconAlignment.Disabled &&
                                path.Contains(mapping.Keyword.ToLowerInvariant()))
                            {
                                var iconKey = $"{mapping.IconName.ToLowerInvariant()}|{mapping.IconAlignment}|{mapping.Priority}|{mapping.HorizontalLayout}";
                                if (!uniqueIcons.ContainsKey(iconKey))
                                {
                                    uniqueIcons[iconKey] = new FilenameBasedIconData
                                    {
                                        IconName         = mapping.IconName.ToLowerInvariant(),
                                        Alignment        = mapping.IconAlignment,
                                        Priority         = mapping.Priority,
                                        HorizontalLayout = mapping.HorizontalLayout
                                    };
                                }
                            }
                        }
                    }

                    filenameBasedIconsList = uniqueIcons.Values.ToList();
                }

                byte[] hashBytes;
                using (var md5 = MD5.Create())
                {
                    var encoding = Encoding.UTF8;
                    var separator = encoding.GetBytes(";");
                    var orderedHashes = itemHashes.OrderBy(h => h).ToList();

                    for (int i = 0; i < orderedHashes.Count; i++)
                    {
                        var bytes = encoding.GetBytes(orderedHashes[i]);
                        if (i < orderedHashes.Count - 1)
                        {
                            md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
                            md5.TransformBlock(separator, 0, separator.Length, null, 0);
                        }
                        else
                        {
                            md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
                            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        }
                    }

                    hashBytes = md5.Hash!;
                }

                var result = new AggregatedAlbumResult
                {
                    Timestamp               = DateTime.UtcNow,
                    AudioLangs              = checkAudioLangs   ? allAudioLangs      : new HashSet<string>(),
                    AudioCodecs             = checkAudioCodecs  ? commonAudioCodecs  : new HashSet<string>(),
                    ChannelType             = checkChannels     ? commonChannelType  : null,
                    SampleRate              = checkSampleRate   ? commonSampleRate   : null,
                    AudioBitRate            = checkAudioBitRate ? commonAudioBitRate : null,
                    BitDepth                = checkBitDepth     ? commonBitDepth     : null,
                    FilenameBasedIcons      = filenameBasedIconsList,
                    CombinedTracksHashShort = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8)
                };

                _albumAggregationCache.AddOrUpdate(parent.Id, result, (_, __) => result);

                if (Interlocked.Increment(ref _albumAdditionsCounter) % ALBUM_CACHE_SIZE_CHECK_FREQUENCY == 0)
                    PruneAlbumAggregationCacheWithLimit();

                return result;
            }
            finally
            {
                sem.Release();
            }
        }
    }
}
