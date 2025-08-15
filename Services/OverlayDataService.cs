using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Services
{
    internal record ItemAnalysisResult(long Ticks, string StreamHash, OverlayData Data);

    internal class OverlayDataService
    {
        private readonly EmbyIconsEnhancer _enhancer;

        private static readonly ConcurrentDictionary<string, Lazy<ItemAnalysisResult>> _itemDataCache = new();
        private const int MaxItemCacheSize = 4000;

        public OverlayDataService(EmbyIconsEnhancer enhancer)
        {
            _enhancer = enhancer;
        }

        public long GetCacheMemoryUsage()
        {
            if (_itemDataCache.IsEmpty) return 0;

            const int avgOverlayDataSize = 1280; // 1.25 KB estimate per entry, accounting for overhead
            return (long)_itemDataCache.Count * avgOverlayDataSize;
        }

        public void InvalidateCacheForItem(Guid itemId)
        {
            if (itemId == Guid.Empty) return;

            var keysToRemove = _itemDataCache.Keys.Where(k => k.StartsWith(itemId.ToString())).ToList();
            foreach (var key in keysToRemove)
            {
                if (_itemDataCache.TryRemove(key, out _))
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                        _enhancer.Logger.Debug($"[EmbyIcons] Invalidated overlay data cache for key: {key}");
                }
            }
        }

        private static readonly char[] _whitespaceChars = { ' ', '\t', '\r', '\n' };
        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            var parts = tag.Trim().ToLowerInvariant().Split(_whitespaceChars, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("-", parts);
        }

        private ItemAnalysisResult GetOrBuildAnalysisResult(BaseItem item, IconProfile profile, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            var cacheKey = $"{item.Id}_{profile.Id}_{globalOptions.IconLoadingMode}_{profileOptions.SnapAspectRatioToCommon}";
            var currentTicks = item.DateModified.Ticks;

            var lazyResult = _itemDataCache.GetOrAdd(cacheKey, key =>
            {
                PruneItemDataCache();
                return new Lazy<ItemAnalysisResult>(() =>
                {
                    if (globalOptions.EnableDebugLogging) _enhancer.Logger.Debug($"[EmbyIcons] Cache factory invoked for '{item.Name}'. Processing streams.");

                    var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                    var newData = ProcessMediaStreams(item, streams, globalOptions, profileOptions);
                    var newHash = MediaStreamHelper.GetItemMediaStreamHashV2(item, streams);

                    return new ItemAnalysisResult(currentTicks, newHash, newData);
                });
            });

            var cachedEntry = lazyResult.Value;

            if (cachedEntry.Ticks != currentTicks)
            {
                _itemDataCache.TryRemove(cacheKey, out _);
                if (globalOptions.EnableDebugLogging) _enhancer.Logger.Debug($"[EmbyIcons] Stale cache detected for '{item.Name}'. Re-processing.");
                return GetOrBuildAnalysisResult(item, profile, profileOptions, globalOptions);
            }

            return cachedEntry;
        }

        public string GetItemStreamHash(BaseItem item, IconProfile profile, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (item is Series || item is BoxSet) return string.Empty;
            var result = GetOrBuildAnalysisResult(item, profile, profileOptions, globalOptions);
            return result.StreamHash;
        }

        private OverlayData CreateOverlayDataFromAggregate(EmbyIconsEnhancer.AggregatedSeriesResult aggResult, BaseItem item)
        {
            var tags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    var nt = NormalizeTag(tag);
                    if (!string.IsNullOrEmpty(nt)) tags.Add(nt);
                }
            }

            return new OverlayData
            {
                AudioLanguages = aggResult.AudioLangs,
                SubtitleLanguages = aggResult.SubtitleLangs,
                AudioCodecs = aggResult.AudioCodecs,
                VideoCodecs = aggResult.VideoCodecs,
                ChannelIconName = aggResult.ChannelTypes.FirstOrDefault(),
                VideoFormatIconName = aggResult.VideoFormats.FirstOrDefault(),
                ResolutionIconName = aggResult.Resolutions.FirstOrDefault(),
                CommunityRating = item.CommunityRating,
                Tags = tags,
                AspectRatioIconName = aggResult.AspectRatios.FirstOrDefault(),
                ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating)
            };
        }

        public OverlayData GetOverlayData(BaseItem item, IconProfile profile, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (item is Series seriesItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(seriesItem, profile, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, seriesItem);
            }

            if (item is BoxSet collectionItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(collectionItem, profile, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, collectionItem);
            }

            var result = GetOrBuildAnalysisResult(item, profile, profileOptions, globalOptions);
            if (globalOptions.EnableDebugLogging) _enhancer.Logger.Debug($"[EmbyIcons] Using cached overlay data for '{item.Name}'.");
            return result.Data;
        }

        private void PruneItemDataCache()
        {
            if (_itemDataCache.Count >= MaxItemCacheSize)
            {
                int itemsToRemove = _itemDataCache.Count - (int)(MaxItemCacheSize * 0.9);
                var keysToRemove = _itemDataCache.Keys.Take(itemsToRemove).ToList();
                foreach (var key in keysToRemove)
                {
                    _itemDataCache.TryRemove(key, out _);
                }
            }
        }

        private OverlayData ProcessMediaStreams(BaseItem item, IReadOnlyList<MediaStream> mainItemStreams, PluginOptions options, ProfileSettings profileOptions)
        {
            var data = new OverlayData();

            if (profileOptions.CommunityScoreIconAlignment != IconAlignment.Disabled)
                data.CommunityRating = item.CommunityRating;

            if (profileOptions.ParentalRatingIconAlignment != IconAlignment.Disabled)
                data.ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating);

            if (profileOptions.TagIconAlignment != IconAlignment.Disabled && item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    var nt = NormalizeTag(tag);
                    if (!string.IsNullOrEmpty(nt)) data.Tags.Add(nt);
                }
            }

            if (!mainItemStreams.Any()) return data;

            MediaStream? primaryVideoStream = null;
            MediaStream? primaryAudioStream = null;

            foreach (var stream in mainItemStreams)
            {
                switch (stream.Type)
                {
                    case MediaStreamType.Audio:
                        if (profileOptions.AudioIconAlignment != IconAlignment.Disabled && !string.IsNullOrEmpty(stream.DisplayLanguage))
                            data.AudioLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));

                        if (profileOptions.AudioCodecIconAlignment != IconAlignment.Disabled)
                        {
                            var audioCodec = MediaStreamHelper.GetAudioCodecIconName(stream);
                            if (audioCodec != null) data.AudioCodecs.Add(audioCodec);
                        }

                        if (primaryAudioStream == null || (stream.Channels ?? 0) > (primaryAudioStream.Channels ?? 0))
                        {
                            primaryAudioStream = stream;
                        }
                        break;
                    case MediaStreamType.Subtitle:
                        if (profileOptions.SubtitleIconAlignment != IconAlignment.Disabled && !string.IsNullOrEmpty(stream.DisplayLanguage))
                            data.SubtitleLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                        break;
                    case MediaStreamType.Video:
                        if (profileOptions.VideoCodecIconAlignment != IconAlignment.Disabled)
                        {
                            var videoCodec = MediaStreamHelper.GetVideoCodecIconName(stream);
                            if (videoCodec != null) data.VideoCodecs.Add(videoCodec);
                        }
                        if (primaryVideoStream == null) primaryVideoStream = stream;
                        break;
                }
            }

            if (profileOptions.ChannelIconAlignment != IconAlignment.Disabled && primaryAudioStream != null)
                data.ChannelIconName = MediaStreamHelper.GetChannelIconName(primaryAudioStream);

            if (profileOptions.VideoFormatIconAlignment != IconAlignment.Disabled)
                data.VideoFormatIconName = MediaStreamHelper.GetVideoFormatIconName(item, mainItemStreams);

            if (profileOptions.ResolutionIconAlignment != IconAlignment.Disabled && primaryVideoStream != null)
            {
                _enhancer._iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder)
                    .TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutionKeys);
                data.ResolutionIconName = MediaStreamHelper.GetResolutionIconNameFromStream(primaryVideoStream, knownResolutionKeys ?? new List<string>());
            }

            if (profileOptions.AspectRatioIconAlignment != IconAlignment.Disabled)
                data.AspectRatioIconName = MediaStreamHelper.GetAspectRatioIconName(primaryVideoStream, profileOptions.SnapAspectRatioToCommon);

            return data;
        }
    }
}
