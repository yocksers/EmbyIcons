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

            var firstItem = itemList[0];
            var firstStreams = firstItem.GetMediaStreams() ?? new List<MediaStream>();
            var firstVideoStream = firstStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            var firstAudioLangs = firstStreams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
            var firstSubtitleLangs = firstStreams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));

            var commonAudioLangs = new HashSet<string>(firstAudioLangs, StringComparer.OrdinalIgnoreCase);
            var allAudioLangs = new HashSet<string>(firstAudioLangs, StringComparer.OrdinalIgnoreCase);
            var commonSubtitleLangs = new HashSet<string>(firstSubtitleLangs, StringComparer.OrdinalIgnoreCase);
            var allSubtitleLangs = new HashSet<string>(firstSubtitleLangs, StringComparer.OrdinalIgnoreCase);

            var commonAudioCodecs = firstStreams.Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commonVideoCodecs = firstStreams.Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var primaryAudioStream = firstStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
            var commonChannelType = primaryAudioStream != null ? MediaStreamHelper.GetChannelIconName(primaryAudioStream) : null;
            var commonAspectRatio = MediaStreamHelper.GetAspectRatioIconName(firstVideoStream, profileOptions.SnapAspectRatioToCommon);

            var customResolutionKeys = _iconCacheManager.GetAllAvailableIconKeys(globalOptions.IconsFolder).GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
            var embeddedResolutionKeys = _iconCacheManager.GetAllAvailableEmbeddedIconKeys().GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());

            List<string> knownResolutionKeys = globalOptions.IconLoadingMode switch
            {
                IconLoadingMode.CustomOnly => customResolutionKeys,
                IconLoadingMode.BuiltInOnly => embeddedResolutionKeys,
                _ => customResolutionKeys.Union(embeddedResolutionKeys, StringComparer.OrdinalIgnoreCase).ToList()
            };

            var commonResolution = MediaStreamHelper.GetResolutionIconNameFromStream(firstVideoStream, knownResolutionKeys);

            var itemHashes = new List<string>(itemList.Count) { $"{firstItem.Id}:{MediaStreamHelper.GetItemMediaStreamHash(firstItem, firstStreams)}" };

            for (int i = 1; i < itemList.Count; i++)
            {
                if (commonChannelType == null &&
                    commonAspectRatio == null &&
                    commonResolution == null &&
                    !commonAudioCodecs.Any() &&
                    !commonVideoCodecs.Any() &&
                    (!requireAllItemsToMatchForLanguage || (!commonAudioLangs.Any() && !commonSubtitleLangs.Any())))
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Early exit from aggregation for '{parent.Name}' at item {i} of {itemList.Count}. No common properties remaining to check.");
                    break;
                }

                var item = itemList[i];
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

                var currentAudioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));
                var currentSubtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage));

                if (requireAllItemsToMatchForLanguage)
                {
                    if (commonAudioLangs.Any()) commonAudioLangs.IntersectWith(currentAudioLangs);
                    if (commonSubtitleLangs.Any()) commonSubtitleLangs.IntersectWith(currentSubtitleLangs);
                }
                allAudioLangs.UnionWith(currentAudioLangs);
                allSubtitleLangs.UnionWith(currentSubtitleLangs);

                if (commonAudioCodecs.Any()) commonAudioCodecs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(name => name != null).Select(name => name!));
                if (commonVideoCodecs.Any()) commonVideoCodecs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(name => name != null).Select(name => name!));

                var currentPrimaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
                if (commonChannelType != null && commonChannelType != (currentPrimaryAudio != null ? MediaStreamHelper.GetChannelIconName(currentPrimaryAudio) : null)) commonChannelType = null;

                var currentAspectRatio = MediaStreamHelper.GetAspectRatioIconName(videoStream, profileOptions.SnapAspectRatioToCommon);
                if (commonAspectRatio != null && commonAspectRatio != currentAspectRatio) commonAspectRatio = null;

                var currentRes = MediaStreamHelper.GetResolutionIconNameFromStream(videoStream, knownResolutionKeys);
                if (commonResolution != null && commonResolution != currentRes) commonResolution = null;

                itemHashes.Add($"{item.Id}:{MediaStreamHelper.GetItemMediaStreamHash(item, streams)}");
            }

            var finalAudioLangs = (profileOptions.AudioIconAlignment != IconAlignment.Disabled)
                ? (requireAllItemsToMatchForLanguage ? commonAudioLangs : allAudioLangs)
                : new HashSet<string>();
            var finalSubtitleLangs = (profileOptions.SubtitleIconAlignment != IconAlignment.Disabled)
                ? (requireAllItemsToMatchForLanguage ? commonSubtitleLangs : allSubtitleLangs)
                : new HashSet<string>();

            var finalAudioCodecs = (profileOptions.AudioCodecIconAlignment != IconAlignment.Disabled) ? commonAudioCodecs : new HashSet<string>();
            var finalVideoCodecs = (profileOptions.VideoCodecIconAlignment != IconAlignment.Disabled) ? commonVideoCodecs : new HashSet<string>();
            var finalChannelTypes = (profileOptions.ChannelIconAlignment != IconAlignment.Disabled && commonChannelType != null) ? new HashSet<string> { commonChannelType } : new HashSet<string>();
            var finalResolutions = (profileOptions.ResolutionIconAlignment != IconAlignment.Disabled && commonResolution != null) ? new HashSet<string> { commonResolution } : new HashSet<string>();
            var finalAspectRatios = (profileOptions.AspectRatioIconAlignment != IconAlignment.Disabled && commonAspectRatio != null) ? new HashSet<string> { commonAspectRatio } : new HashSet<string>();

            var finalVideoFormats = new HashSet<string>();
            if (profileOptions.VideoFormatIconAlignment != IconAlignment.Disabled && itemList.Any())
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