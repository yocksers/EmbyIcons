using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        private const int MaxSeriesCacheSize = 500;

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
            public string CombinedEpisodesHashShort { get; init; } = "";
            public DateTime Timestamp { get; init; } = DateTime.MinValue;
        }

        private void PruneSeriesAggregationCacheWithLimit()
        {
            if (_seriesAggregationCache.Count > MaxSeriesCacheSize)
            {
                var toRemove = _seriesAggregationCache.Count - MaxSeriesCacheSize;
                if (toRemove <= 0) return;
                var oldest = _seriesAggregationCache.ToArray().OrderBy(kvp => kvp.Value.Timestamp).Take(toRemove);
                foreach (var item in oldest) _seriesAggregationCache.TryRemove(item.Key, out _);
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Pruned {toRemove} items from the series aggregation cache.");
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
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Using cached aggregated data for '{parent.Name}' ({parent.Id}).");
                return cachedResult;
            }

            bool useLiteMode;
            bool requireAllItemsToMatchForLanguage;
            InternalItemsQuery query;

            if (parent is Series)
            {
                useLiteMode = profileOptions.UseSeriesLiteMode;
                requireAllItemsToMatchForLanguage = useLiteMode || profileOptions.ShowSeriesIconsIfAllEpisodesHaveLanguage;
                query = new InternalItemsQuery
                {
                    Parent = parent,
                    Recursive = true,
                    IncludeItemTypes = new[] { EmbyIcons.Constants.Episode },
                    Limit = useLiteMode ? 1 : null,
                    OrderBy = useLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
                };
            }
            else if (parent is Season)
            {
                useLiteMode = profileOptions.UseSeriesLiteMode; // Seasons should probably follow series settings for lite mode
                requireAllItemsToMatchForLanguage = useLiteMode || profileOptions.ShowSeriesIconsIfAllEpisodesHaveLanguage;
                query = new InternalItemsQuery
                {
                    Parent = parent,
                    Recursive = true,
                    IncludeItemTypes = new[] { EmbyIcons.Constants.Episode },
                    Limit = useLiteMode ? 1 : null,
                    OrderBy = useLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
                };
            }
            else if (parent is BoxSet boxSet)
            {
                useLiteMode = profileOptions.UseCollectionLiteMode;
                requireAllItemsToMatchForLanguage = useLiteMode || profileOptions.ShowCollectionIconsIfAllChildrenHaveLanguage;
                query = new InternalItemsQuery
                {
                    CollectionIds = new[] { boxSet.InternalId },
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode" },
                    IsVirtualItem = false,
                    Limit = useLiteMode ? 1 : null,
                    OrderBy = useLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
                };
            }
            else
            {
                return new AggregatedSeriesResult();
            }


            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] No valid cache found. Aggregating data for '{parent.Name}' ({parent.Id}). LiteMode: {useLiteMode}.");

            var items = _libraryManager.GetItemList(query);
            var itemList = items.ToList();

            if (parent is Series && profileOptions.ExcludeSpecialsFromSeriesAggregation)
            {
                itemList = itemList.Where(ep => (ep.Parent as Season)?.IndexNumber != 0).ToList();
            }

            if (!itemList.Any())
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] No child items found for '{parent.Name}'. Returning temporary empty result without caching.");
                return new AggregatedSeriesResult();
            }

            // Determine which checks are enabled to avoid unnecessary work
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
            var firstVideoStream = firstStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            var commonAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (checkAudioLangs)
            {
                var firstAudioLangs = firstStreams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                commonAudioLangs.UnionWith(firstAudioLangs);
                allAudioLangs.UnionWith(firstAudioLangs);
            }

            var commonSubtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allSubtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (checkSubLangs)
            {
                var firstSubtitleLangs = firstStreams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                commonSubtitleLangs.UnionWith(firstSubtitleLangs);
                allSubtitleLangs.UnionWith(firstSubtitleLangs);
            }

            var commonAudioCodecs = checkAudioCodecs ? firstStreams.Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            var commonVideoCodecs = checkVideoCodecs ? firstStreams.Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

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
                var customResolutionKeys = _iconCacheManager.GetAllAvailableIconKeys(globalOptions.IconsFolder).GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
                var embeddedResolutionKeys = _iconCacheManager.GetAllAvailableEmbeddedIconKeys().GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
                knownResolutionKeys = globalOptions.IconLoadingMode switch
                {
                    IconLoadingMode.CustomOnly => customResolutionKeys,
                    IconLoadingMode.BuiltInOnly => embeddedResolutionKeys,
                    _ => customResolutionKeys.Union(embeddedResolutionKeys, StringComparer.OrdinalIgnoreCase).ToList()
                };
                commonResolution = MediaStreamHelper.GetResolutionIconNameFromStream(firstVideoStream, knownResolutionKeys);
            }

            var itemHashes = new List<string>(itemList.Count) { $"{firstItem.Id}:{MediaStreamHelper.GetItemMediaStreamHash(firstItem, firstStreams)}" };

            for (int i = 1; i < itemList.Count; i++)
            {
                if (!checkChannels && !checkAspectRatio && !checkResolution && !checkAudioCodecs && !checkVideoCodecs &&
                    (!requireAllItemsToMatchForLanguage || (!checkAudioLangs && !checkSubLangs)))
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Early exit from aggregation for '{parent.Name}'. No common properties are enabled to check.");
                    break;
                }

                var item = itemList[i];
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

                if (checkAudioLangs)
                {
                    var currentAudioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                    if (requireAllItemsToMatchForLanguage) { if (commonAudioLangs.Any()) commonAudioLangs.IntersectWith(currentAudioLangs); }
                    allAudioLangs.UnionWith(currentAudioLangs);
                }

                if (checkSubLangs)
                {
                    var currentSubtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                    if (requireAllItemsToMatchForLanguage) { if (commonSubtitleLangs.Any()) commonSubtitleLangs.IntersectWith(currentSubtitleLangs); }
                    allSubtitleLangs.UnionWith(currentSubtitleLangs);
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

                itemHashes.Add($"{item.Id}:{MediaStreamHelper.GetItemMediaStreamHash(item, streams)}");
            }

            var finalAudioLangs = checkAudioLangs ? (requireAllItemsToMatchForLanguage ? commonAudioLangs : allAudioLangs) : new HashSet<string>();
            var finalSubtitleLangs = checkSubLangs ? (requireAllItemsToMatchForLanguage ? commonSubtitleLangs : allSubtitleLangs) : new HashSet<string>();

            var finalAudioCodecs = checkAudioCodecs ? commonAudioCodecs : new HashSet<string>();
            var finalVideoCodecs = checkVideoCodecs ? commonVideoCodecs : new HashSet<string>();
            var finalChannelTypes = (checkChannels && commonChannelType != null) ? new HashSet<string> { commonChannelType } : new HashSet<string>();
            var finalResolutions = (checkResolution && commonResolution != null) ? new HashSet<string> { commonResolution } : new HashSet<string>();
            var finalAspectRatios = (checkAspectRatio && commonAspectRatio != null) ? new HashSet<string> { commonAspectRatio } : new HashSet<string>();

            var finalVideoFormats = new HashSet<string>();
            if (checkVideoFormat && itemList.Any())
            {
                var itemHdrStates = itemList.Select(ep =>
                {
                    var streams = ep.GetMediaStreams() ?? new List<MediaStream>();
                    return MediaStreamHelper.GetVideoFormatIconName(ep, streams);
                }).ToList();

                if (!itemHdrStates.Contains(null))
                {
                    var distinctFormats = itemHdrStates.Where(s => s != null).Distinct().ToList();
                    if (distinctFormats.Count > 1)
                    {
                        finalVideoFormats.Add("hdr");
                    }
                    else if (distinctFormats.Count == 1)
                    {
                        finalVideoFormats.Add(distinctFormats.First()!);
                    }
                }
            }

            var combinedHashString = string.Join(";", itemHashes.OrderBy(h => h));
            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(combinedHashString));

            var result = new AggregatedSeriesResult
            {
                Timestamp = DateTime.UtcNow,
                AudioLangs = finalAudioLangs,
                SubtitleLangs = finalSubtitleLangs,
                ChannelTypes = finalChannelTypes,
                AudioCodecs = finalAudioCodecs,
                VideoCodecs = finalVideoCodecs,
                Resolutions = finalResolutions,
                VideoFormats = finalVideoFormats,
                AspectRatios = finalAspectRatios,
                CombinedEpisodesHashShort = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8)
            };

            _seriesAggregationCache.AddOrUpdate(parent.Id, result, (_, __) => result);
            PruneSeriesAggregationCacheWithLimit();
            return result;
        }
    }
}