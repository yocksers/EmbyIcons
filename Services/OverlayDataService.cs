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
    internal class OverlayDataService
    {
        private readonly EmbyIconsEnhancer _enhancer;

        private static readonly ConcurrentDictionary<string, Lazy<(long Ticks, OverlayData Data)>> _itemDataCache = new();
        private const int MaxItemCacheSize = 4000;

        public OverlayDataService(EmbyIconsEnhancer enhancer)
        {
            _enhancer = enhancer;
        }

        public void InvalidateCacheForItem(Guid itemId)
        {
            if (itemId != Guid.Empty && _itemDataCache.TryRemove(itemId.ToString(), out _))
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    _enhancer.Logger.Debug($"[EmbyIcons] Invalidated overlay data cache for item ID: {itemId}");
            }
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(tag.Trim().ToLowerInvariant(), "\\s+", "-");
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

            var cacheKey = item.Id.ToString();
            var currentTicks = item.DateModified.Ticks;

            var lazyResult = _itemDataCache.GetOrAdd(cacheKey, key =>
            {
                return new Lazy<(long Ticks, OverlayData Data)>(() =>
                {
                    if (globalOptions.EnableDebugLogging) _enhancer.Logger.Debug($"[EmbyIcons] Cache factory invoked for '{item.Name}'. Processing streams.");
                    var newData = ProcessMediaStreams(item, globalOptions, profileOptions);
                    return (currentTicks, newData);
                });
            });

            var cachedEntry = lazyResult.Value;

            if (cachedEntry.Ticks != currentTicks)
            {
                _itemDataCache.TryRemove(cacheKey, out _);
                if (globalOptions.EnableDebugLogging) _enhancer.Logger.Debug($"[EmbyIcons] Stale cache detected for '{item.Name}'. Re-processing.");
                return GetOverlayData(item, profile, profileOptions, globalOptions);
            }

            if (globalOptions.EnableDebugLogging) _enhancer.Logger.Debug($"[EmbyIcons] Using cached overlay data for '{item.Name}'.");

            if (_itemDataCache.Count > MaxItemCacheSize)
            {
                var keyToRemove = _itemDataCache.FirstOrDefault(kvp => kvp.Value.IsValueCreated).Key;
                if (keyToRemove != null)
                {
                    _itemDataCache.TryRemove(keyToRemove, out _);
                }
            }

            return cachedEntry.Data;
        }

        private OverlayData ProcessMediaStreams(BaseItem item, PluginOptions options, ProfileSettings profileOptions)
        {
            var data = new OverlayData
            {
                CommunityRating = item.CommunityRating,
                ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating)
            };

            if (item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    var nt = NormalizeTag(tag);
                    if (!string.IsNullOrEmpty(nt)) data.Tags.Add(nt);
                }
            }

            var mainItemStreams = item.GetMediaStreams() ?? new List<MediaStream>();
            if (!mainItemStreams.Any()) return data;

            MediaStream? primaryVideoStream = mainItemStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            MediaStream? primaryAudioStream = mainItemStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();

            foreach (var stream in mainItemStreams)
            {
                switch (stream.Type)
                {
                    case MediaStreamType.Audio:
                        if (!string.IsNullOrEmpty(stream.DisplayLanguage)) data.AudioLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                        var audioCodec = MediaStreamHelper.GetAudioCodecIconName(stream);
                        if (audioCodec != null) data.AudioCodecs.Add(audioCodec);
                        break;
                    case MediaStreamType.Subtitle:
                        if (!string.IsNullOrEmpty(stream.DisplayLanguage)) data.SubtitleLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                        break;
                    case MediaStreamType.Video:
                        var videoCodec = MediaStreamHelper.GetVideoCodecIconName(stream);
                        if (videoCodec != null) data.VideoCodecs.Add(videoCodec);
                        break;
                }
            }

            _enhancer._iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder)
                .TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutionKeys);

            if (primaryAudioStream != null) data.ChannelIconName = MediaStreamHelper.GetChannelIconName(primaryAudioStream);
            data.VideoFormatIconName = MediaStreamHelper.GetVideoFormatIconName(item, mainItemStreams);
            data.ResolutionIconName = primaryVideoStream != null ? MediaStreamHelper.GetResolutionIconNameFromStream(primaryVideoStream, knownResolutionKeys ?? new List<string>()) : null;
            data.AspectRatioIconName = MediaStreamHelper.GetAspectRatioIconName(primaryVideoStream, profileOptions.SnapAspectRatioToCommon);

            return data;
        }
    }
}