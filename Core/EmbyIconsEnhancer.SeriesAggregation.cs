using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        private static int MaxSeriesCacheSize => Plugin.Instance?.Configuration.MaxSeriesCacheSize ?? 500;
        
        private const int CACHE_SIZE_CHECK_FREQUENCY = 50;
        private static int _additionsCounter = 0;
        private static readonly KeyedAsyncLock<Guid> _aggregationLocks = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Guid> _aggregatedParentIds = new();
        private static readonly SummaryDiskStore<SeriesSummaryData> _seriesSummaryStore = new("summaries-tv.json");

        internal static bool TryGetAggregatedParentId(long internalId, out Guid parentId)
            => _aggregatedParentIds.TryGetValue(internalId, out parentId);

        internal static void ForgetAggregatedParent(long internalId)
            => _aggregatedParentIds.TryRemove(internalId, out _);

        internal record AggregatedSeriesResult
        {
            public HashSet<string> AudioLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubtitleLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ChannelTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoFormats { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Resolutions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AspectRatios { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SourceIcons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public List<FilenameBasedIconData> FilenameBasedIcons { get; init; } = new();
            public DateTime Timestamp { get; init; } = DateTime.MinValue;
            internal long LastUsedTicks;
        }

        internal sealed class SeriesSummaryData
        {
            public string[]? AudioLangs { get; set; }
            public string[]? SubtitleLangs { get; set; }
            public string[]? ChannelTypes { get; set; }
            public string[]? AudioCodecs { get; set; }
            public string[]? VideoCodecs { get; set; }
            public string[]? VideoFormats { get; set; }
            public string[]? Resolutions { get; set; }
            public string[]? AspectRatios { get; set; }
            public string[]? SourceIcons { get; set; }
            public List<FilenameBasedIconData>? FilenameBasedIcons { get; set; }
        }

        private static SeriesSummaryData ToSummaryData(AggregatedSeriesResult result) => new SeriesSummaryData
        {
            AudioLangs = SummaryStoreKeys.Pack(result.AudioLangs),
            SubtitleLangs = SummaryStoreKeys.Pack(result.SubtitleLangs),
            ChannelTypes = SummaryStoreKeys.Pack(result.ChannelTypes),
            AudioCodecs = SummaryStoreKeys.Pack(result.AudioCodecs),
            VideoCodecs = SummaryStoreKeys.Pack(result.VideoCodecs),
            VideoFormats = SummaryStoreKeys.Pack(result.VideoFormats),
            Resolutions = SummaryStoreKeys.Pack(result.Resolutions),
            AspectRatios = SummaryStoreKeys.Pack(result.AspectRatios),
            SourceIcons = SummaryStoreKeys.Pack(result.SourceIcons),
            FilenameBasedIcons = result.FilenameBasedIcons.Count > 0 ? result.FilenameBasedIcons : null
        };

        private static AggregatedSeriesResult FromSummaryData(SeriesSummaryData data) => new AggregatedSeriesResult
        {
            Timestamp = DateTime.UtcNow,
            LastUsedTicks = Stopwatch.GetTimestamp(),
            AudioLangs = SummaryStoreKeys.Unpack(data.AudioLangs),
            SubtitleLangs = SummaryStoreKeys.Unpack(data.SubtitleLangs),
            ChannelTypes = SummaryStoreKeys.Unpack(data.ChannelTypes),
            AudioCodecs = SummaryStoreKeys.Unpack(data.AudioCodecs),
            VideoCodecs = SummaryStoreKeys.Unpack(data.VideoCodecs),
            VideoFormats = SummaryStoreKeys.Unpack(data.VideoFormats),
            Resolutions = SummaryStoreKeys.Unpack(data.Resolutions),
            AspectRatios = SummaryStoreKeys.Unpack(data.AspectRatios),
            SourceIcons = SummaryStoreKeys.Unpack(data.SourceIcons),
            FilenameBasedIcons = data.FilenameBasedIcons ?? new List<FilenameBasedIconData>()
        };

        private static AggregatedSeriesResult TouchSeriesResult(AggregatedSeriesResult result)
        {
            Volatile.Write(ref result.LastUsedTicks, Stopwatch.GetTimestamp());
            return result;
        }

        private List<string> GetKnownResolutionKeys(PluginOptions globalOptions)
        {
            var customResolutionKeys = _iconCacheManager.GetAllAvailableIconKeys(globalOptions.IconsFolder).GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
            var embeddedResolutionKeys = _iconCacheManager.GetAllAvailableEmbeddedIconKeys().GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
            return globalOptions.IconLoadingMode switch
            {
                IconLoadingMode.CustomOnly => customResolutionKeys,
                IconLoadingMode.BuiltInOnly => embeddedResolutionKeys,
                _ => customResolutionKeys.Union(embeddedResolutionKeys, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        private void CacheSeriesResult(BaseItem parent, AggregatedSeriesResult result)
        {
            _seriesAggregationCache.AddOrUpdate(parent.Id, result, (_, __) => result);
            if (parent.InternalId > 0)
            {
                _aggregatedParentIds[parent.InternalId] = parent.Id;
            }

            if (Interlocked.Increment(ref _additionsCounter) % CACHE_SIZE_CHECK_FREQUENCY == 0)
            {
                PruneSeriesAggregationCacheWithLimit();
            }
        }

        private void PruneSeriesAggregationCacheWithLimit()
        {
            var count = _seriesAggregationCache.Count;
            if (count > MaxSeriesCacheSize)
            {
                var toRemove = count - MaxSeriesCacheSize;
                if (toRemove <= 0) return;
                
                var entries = _seriesAggregationCache.ToArray();
                var lastUsed = new long[entries.Length];
                for (int k = 0; k < entries.Length; k++) lastUsed[k] = Volatile.Read(ref entries[k].Value.LastUsedTicks);
                Array.Sort(lastUsed, entries);
                var keysToRemove = new Guid[toRemove];
                for (int k = 0; k < toRemove; k++) keysToRemove[k] = entries[k].Key;
                    
                foreach (var key in keysToRemove)
                {
                    _seriesAggregationCache.TryRemove(key, out _);
                }
                
                if (Helpers.PluginHelper.IsDebugLoggingEnabled) 
                    _logger.Debug($"[EmbyIcons] Pruned {keysToRemove.Length} items from the series aggregation cache.");
            }
        }

        internal AggregatedSeriesResult GetOrBuildAggregatedDataForParent(BaseItem parent, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (parent.Id == Guid.Empty)
            {
                _logger.Warn($"[EmbyIcons] Attempted to aggregate data for a parent item with an empty ID: {parent.Name}. Returning empty result.");
                return new AggregatedSeriesResult();
            }

            if (_seriesAggregationCache.TryGetValue(parent.Id, out var cachedResult))
            {
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug($"[EmbyIcons] Using cached aggregated data for '{parent.Name}' ({parent.Id}).");
                return TouchSeriesResult(cachedResult);
            }

            using (_aggregationLocks.Lock(parent.Id))
            {
                if (_seriesAggregationCache.TryGetValue(parent.Id, out cachedResult))
                {
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _logger.Debug($"[EmbyIcons] Using cached aggregated data for '{parent.Name}' ({parent.Id}).");
                    return TouchSeriesResult(cachedResult);
                }

                bool useLiteMode;
                InternalItemsQuery? query = null;
                List<BaseItem>? preBuiltItemList = null;

                if (parent is Series)
                {
                    useLiteMode = profileOptions.UseSeriesLiteMode;
                    query = new InternalItemsQuery
                    {
                        Parent = parent,
                        Recursive = true,
                        IncludeItemTypes = new[] { Configuration.Constants.Episode },
                        Limit = useLiteMode ? 1 : null,
                        OrderBy = useLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
                    };
                }
                else if (parent is Season)
                {
                    useLiteMode = profileOptions.UseSeriesLiteMode;
                    query = new InternalItemsQuery
                    {
                        Parent = parent,
                        Recursive = true,
                        IncludeItemTypes = new[] { Configuration.Constants.Episode },
                        Limit = useLiteMode ? 1 : null,
                        OrderBy = useLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
                    };
                }
                else if (parent is BoxSet boxSet)
                {
                    useLiteMode = profileOptions.UseCollectionLiteMode;

                    var sortOrder = useLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>();
                    int? limitPerType = useLiteMode ? 1 : null;

                    var movieItems = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        CollectionIds = new[] { boxSet.InternalId },
                        IncludeItemTypes = new[] { "Movie" },
                        IsVirtualItem = false,
                        Limit = limitPerType,
                        OrderBy = sortOrder
                    });

                    var seriesInCollection = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        CollectionIds = new[] { boxSet.InternalId },
                        IncludeItemTypes = new[] { "Series" }
                    });

                    var episodeItems = new List<BaseItem>();
                    if (seriesInCollection.Any())
                    {
                        var seriesInternalIds = seriesInCollection.Select(s => s.InternalId).ToArray();
                        episodeItems = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            AncestorIds = seriesInternalIds,
                            IncludeItemTypes = new[] { "Episode" },
                            IsVirtualItem = false,
                            Recursive = true,
                            Limit = limitPerType,
                            OrderBy = sortOrder
                        }).ToList();
                    }

                    preBuiltItemList = movieItems.Concat(episodeItems).ToList();
                }
                else
                {
                    return new AggregatedSeriesResult();
                }

                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug($"[EmbyIcons] No valid cache found. Aggregating data for '{parent.Name}' ({parent.Id}). LiteMode: {useLiteMode}.");

                var itemList = preBuiltItemList ?? _libraryManager.GetItemList(query!).ToList();

                if (parent is Series && profileOptions.ExcludeSpecialsFromSeriesAggregation)
                {
                    itemList = itemList.Where(ep => (ep.Parent as Season)?.IndexNumber != 0).ToList();
                }

                if (!itemList.Any())
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] No child items found for '{parent.Name}'. Returning temporary empty result without caching.");
                    return new AggregatedSeriesResult();
                }

                string? storeSignature = null;
                string? storeFingerprint = null;
                if (SummaryDiskStore<SeriesSummaryData>.IsEnabled)
                {
                    storeSignature = SummaryStoreKeys.GetSignature(profileOptions, globalOptions, GetKnownResolutionKeys(globalOptions));
                    storeFingerprint = SummaryStoreKeys.GetFingerprint(parent, itemList);
                    if (_seriesSummaryStore.TryGet(parent.Id, storeSignature, storeFingerprint, out var stored) && stored != null)
                    {
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _logger.Debug($"[EmbyIcons] Using saved summary for '{parent.Name}' ({parent.Id}); its {itemList.Count} item(s) are unchanged.");
                        var restored = FromSummaryData(stored);
                        CacheSeriesResult(parent, restored);
                        return restored;
                    }
                }

                bool checkAudioLangs = profileOptions.AudioIconAlignment != IconAlignment.Disabled;
                bool checkSubLangs = profileOptions.SubtitleIconAlignment != IconAlignment.Disabled;
                bool checkAudioCodecs = profileOptions.AudioCodecIconAlignment != IconAlignment.Disabled;
                bool checkVideoCodecs = profileOptions.VideoCodecIconAlignment != IconAlignment.Disabled;
                bool checkChannels = profileOptions.ChannelIconAlignment != IconAlignment.Disabled;
                bool checkAspectRatio = profileOptions.AspectRatioIconAlignment != IconAlignment.Disabled;
                bool checkResolution = profileOptions.ResolutionIconAlignment != IconAlignment.Disabled;
                bool checkVideoFormat = profileOptions.VideoFormatIconAlignment != IconAlignment.Disabled;

                var firstItem = itemList[0];
                var firstStreams = firstItem.GetMediaStreams() ?? new List<MediaStream>();
                var firstVideoStream = MediaStreamHelper.GetPrimaryVideoStream(firstStreams);

                var commonAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (checkAudioLangs)
                {
                    foreach (var stream in firstStreams)
                    {
                        if (stream.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(stream.DisplayLanguage))
                        {
                            var lang = LanguageHelper.NormalizeLangCode(stream.DisplayLanguage);
                            commonAudioLangs.Add(lang);
                        }
                    }
                }

                var commonSubtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (checkSubLangs)
                {
                    foreach (var stream in firstStreams)
                    {
                        if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.DisplayLanguage))
                        {
                            var lang = LanguageHelper.NormalizeLangCode(stream.DisplayLanguage);
                            commonSubtitleLangs.Add(lang);
                        }
                    }
                }

                var commonAudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var commonVideoCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (checkAudioCodecs)
                {
                    foreach (var stream in firstStreams)
                    {
                        if (stream.Type == MediaStreamType.Audio)
                        {
                            var codec = MediaStreamHelper.GetAudioCodecIconName(stream);
                            if (codec != null) commonAudioCodecs.Add(codec);
                        }
                    }
                }
                if (checkVideoCodecs)
                {
                    foreach (var stream in firstStreams)
                    {
                        if (stream.Type == MediaStreamType.Video)
                        {
                            var codec = MediaStreamHelper.GetVideoCodecIconName(stream);
                            if (codec != null) commonVideoCodecs.Add(codec);
                        }
                    }
                }

                string? commonChannelType = null;
                if (checkChannels)
                {
                    var primaryAudioStream = firstStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
                    commonChannelType = primaryAudioStream != null ? MediaStreamHelper.GetChannelIconName(primaryAudioStream) : null;
                }

                string? commonAspectRatio = checkAspectRatio ? MediaStreamHelper.GetAspectRatioIconName(firstVideoStream, profileOptions.SnapAspectRatioToCommon) : null;

                List<string> knownResolutionKeys = new List<string>();
                string? commonResolution = null;
                if (checkResolution)
                {
                    knownResolutionKeys = GetKnownResolutionKeys(globalOptions);
                    commonResolution = MediaStreamHelper.GetResolutionIconNameFromStream(firstVideoStream, knownResolutionKeys);
                }

                var seenVideoFormats = new HashSet<string>();
                bool videoFormatSettled = true;
                if (checkVideoFormat)
                {
                    var firstVideoFormat = MediaStreamHelper.GetVideoFormatIconName(firstItem, firstStreams);
                    if (firstVideoFormat != null)
                    {
                        seenVideoFormats.Add(firstVideoFormat);
                        videoFormatSettled = false;
                    }
                }

                for (int i = 1; i < itemList.Count; i++)
                {
                    bool allCommonExhausted =
                        (!checkAudioLangs || commonAudioLangs.Count == 0) &&
                        (!checkSubLangs || commonSubtitleLangs.Count == 0) &&
                        (!checkAudioCodecs || commonAudioCodecs.Count == 0) &&
                        (!checkVideoCodecs || commonVideoCodecs.Count == 0) &&
                        (!checkChannels || commonChannelType == null) &&
                        (!checkAspectRatio || commonAspectRatio == null) &&
                        (!checkResolution || commonResolution == null) &&
                        videoFormatSettled;

                    if (allCommonExhausted)
                    {
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _logger.Debug($"[EmbyIcons] Early exit from aggregation for '{parent.Name}' after {i} of {itemList.Count} items; no remaining properties can change.");
                        break;
                    }

                    var item = itemList[i];
                    var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                    var videoStream = MediaStreamHelper.GetPrimaryVideoStream(streams);

                    if (!videoFormatSettled)
                    {
                        var currentVideoFormat = MediaStreamHelper.GetVideoFormatIconName(item, streams);
                        if (currentVideoFormat == null)
                        {
                            seenVideoFormats.Clear();
                            videoFormatSettled = true;
                        }
                        else
                        {
                            seenVideoFormats.Add(currentVideoFormat);
                        }
                    }

                    if (checkAudioLangs)
                    {
                        var currentAudioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                        if (commonAudioLangs.Count > 0) commonAudioLangs.IntersectWith(currentAudioLangs);
                    }

                    if (checkSubLangs)
                    {
                        var currentSubtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                        if (commonSubtitleLangs.Count > 0) commonSubtitleLangs.IntersectWith(currentSubtitleLangs);
                    }

                    if (checkAudioCodecs && commonAudioCodecs.Any()) commonAudioCodecs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(name => name != null).Select(name => name!));
                    if (checkVideoCodecs && commonVideoCodecs.Any()) commonVideoCodecs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(name => name != null).Select(name => name!));

                    if (checkChannels && commonChannelType != null)
                    {
                        var currentPrimaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
                        if (commonChannelType != (currentPrimaryAudio != null ? MediaStreamHelper.GetChannelIconName(currentPrimaryAudio) : null)) commonChannelType = null;
                    }

                    if (checkAspectRatio && commonAspectRatio != null)
                    {
                        var currentAspectRatio = MediaStreamHelper.GetAspectRatioIconName(videoStream, profileOptions.SnapAspectRatioToCommon);
                        if (commonAspectRatio != currentAspectRatio) commonAspectRatio = null;
                    }

                    if (checkResolution && commonResolution != null)
                    {
                        var currentRes = MediaStreamHelper.GetResolutionIconNameFromStream(videoStream, knownResolutionKeys);
                        if (commonResolution != currentRes) commonResolution = null;
                    }
                }

                var finalAudioLangs = checkAudioLangs ? commonAudioLangs : new HashSet<string>();
                var finalSubtitleLangs = checkSubLangs ? commonSubtitleLangs : new HashSet<string>();

                var finalAudioCodecs = checkAudioCodecs ? commonAudioCodecs : new HashSet<string>();
                var finalVideoCodecs = checkVideoCodecs ? commonVideoCodecs : new HashSet<string>();
                var finalChannelTypes = (checkChannels && commonChannelType != null) ? new HashSet<string> { commonChannelType } : new HashSet<string>();
                var finalResolutions = (checkResolution && commonResolution != null) ? new HashSet<string> { commonResolution } : new HashSet<string>();
                var finalAspectRatios = (checkAspectRatio && commonAspectRatio != null) ? new HashSet<string> { commonAspectRatio } : new HashSet<string>();

                var finalVideoFormats = new HashSet<string>();
                if (seenVideoFormats.Count > 1)
                {
                    finalVideoFormats.Add("hdr");
                }
                else if (seenVideoFormats.Count == 1)
                {
                    finalVideoFormats.Add(seenVideoFormats.First());
                }

                var finalSourceIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var filenameBasedIconsList = new List<FilenameBasedIconData>();
                if (profileOptions.FilenameBasedIcons.Any())
                {
                    var uniqueIcons = new Dictionary<string, FilenameBasedIconData>();
                    var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool checkParentPath = false;
                    bool checkEpisodePaths = false;

                    if (parent is Series)
                    {
                        checkParentPath = true;
                        checkEpisodePaths = true;
                    }
                    else if (parent is Season)
                    {
                        checkParentPath = true;
                        checkEpisodePaths = true;
                    }

                    if (checkParentPath && !string.IsNullOrEmpty(parent.Path))
                    {
                        allPaths.Add(parent.Path.ToLowerInvariant());
                    }

                    if (checkEpisodePaths)
                    {
                        foreach (var item in itemList)
                        {
                            if (!string.IsNullOrEmpty(item.Path))
                            {
                                allPaths.Add(item.Path.ToLowerInvariant());
                            }
                        }
                    }

                    foreach (var path in allPaths)
                    {
                        foreach (var mapping in profileOptions.FilenameBasedIcons)
                        {
                            bool shouldApply = false;

                            if (parent is Series && mapping.ApplyToSeries)
                            {
                                shouldApply = true;
                            }
                            else if (parent is Series && mapping.ApplyToEpisodes && checkEpisodePaths)
                            {
                                shouldApply = true;
                            }
                            else if (parent is Season && mapping.ApplyToSeasons)
                            {
                                shouldApply = true;
                            }
                            else if (parent is Season && mapping.ApplyToEpisodes && checkEpisodePaths)
                            {
                                shouldApply = true;
                            }

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
                                        IconName = mapping.IconName.ToLowerInvariant(),
                                        Alignment = mapping.IconAlignment,
                                        Priority = mapping.Priority,
                                        HorizontalLayout = mapping.HorizontalLayout
                                    };
                                }
                            }
                        }
                    }

                    filenameBasedIconsList = uniqueIcons.Values.ToList();
                }

                var result = new AggregatedSeriesResult
                {
                    Timestamp = DateTime.UtcNow,
                    LastUsedTicks = Stopwatch.GetTimestamp(),
                    AudioLangs = finalAudioLangs,
                    SubtitleLangs = finalSubtitleLangs,
                    ChannelTypes = finalChannelTypes,
                    AudioCodecs = finalAudioCodecs,
                    VideoCodecs = finalVideoCodecs,
                    Resolutions = finalResolutions,
                    VideoFormats = finalVideoFormats,
                    AspectRatios = finalAspectRatios,
                    SourceIcons = finalSourceIcons,
                    FilenameBasedIcons = filenameBasedIconsList
                };

                CacheSeriesResult(parent, result);

                if (storeSignature != null && storeFingerprint != null)
                {
                    _seriesSummaryStore.Set(parent.Id, storeSignature, storeFingerprint, ToSummaryData(result));
                }

                return result;
            }
        }
    }
}