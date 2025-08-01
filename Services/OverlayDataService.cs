﻿﻿using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Services
{
    internal class OverlayDataService
    {
        private readonly EmbyIconsEnhancer _enhancer;

        public OverlayDataService(EmbyIconsEnhancer enhancer)
        {
            _enhancer = enhancer;
        }

        private OverlayData CreateOverlayDataFromAggregate(EmbyIconsEnhancer.AggregatedSeriesResult aggResult, BaseItem item)
        {
            var tags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    tags.Add(string.Intern(tag));
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

        public OverlayData GetOverlayData(BaseItem item, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (item is Series seriesItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(seriesItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, seriesItem);
            }

            if (item is BoxSet collectionItem)
            {
                if (!profileOptions.UseCollectionLiteMode && !profileOptions.ShowCollectionIconsIfAllChildrenHaveLanguage)
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    {
                        Plugin.Instance.Logger.Debug($"[EmbyIcons] Overlays for collections are disabled in the current profile (Full Mode). Skipping '{item.Name}'.");
                    }
                    return new OverlayData();
                }

                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(collectionItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, collectionItem);
            }

            if (EmbyIconsEnhancer._episodeIconCache.TryGetValue(item.Id, out var cachedInfo) && cachedInfo.DateModifiedTicks == item.DateModified.Ticks)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) Plugin.Instance?.Logger.Debug($"[EmbyIcons] Using cached icon info for '{item.Name}'.");
                var updatedInfo = cachedInfo with { DateCached = System.DateTime.UtcNow };
                EmbyIconsEnhancer._episodeIconCache.TryUpdate(item.Id, updatedInfo, cachedInfo);
                return new OverlayData
                {
                    AudioLanguages = cachedInfo.AudioLangs,
                    SubtitleLanguages = cachedInfo.SubtitleLangs,
                    AudioCodecs = cachedInfo.AudioCodecs,
                    VideoCodecs = cachedInfo.VideoCodecs,
                    Tags = cachedInfo.Tags,
                    ChannelIconName = cachedInfo.ChannelIconName,
                    VideoFormatIconName = cachedInfo.VideoFormatIconName,
                    ResolutionIconName = cachedInfo.ResolutionIconName,
                    CommunityRating = item.CommunityRating,
                    AspectRatioIconName = cachedInfo.AspectRatioIconName,
                    ParentalRatingIconName = cachedInfo.ParentalRatingIconName
                };
            }

            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) Plugin.Instance?.Logger.Debug($"[EmbyIcons] No valid cache. Processing streams for '{item.Name}'.");
            var overlayData = ProcessMediaStreams(item, globalOptions);

            var newInfo = new EmbyIconsEnhancer.EpisodeIconInfo
            {
                AudioLangs = overlayData.AudioLanguages,
                SubtitleLangs = overlayData.SubtitleLanguages,
                AudioCodecs = overlayData.AudioCodecs,
                VideoCodecs = overlayData.VideoCodecs,
                Tags = overlayData.Tags,
                ChannelIconName = overlayData.ChannelIconName,
                VideoFormatIconName = overlayData.VideoFormatIconName,
                ResolutionIconName = overlayData.ResolutionIconName,
                DateModifiedTicks = item.DateModified.Ticks,
                DateCached = System.DateTime.UtcNow,
                AspectRatioIconName = overlayData.AspectRatioIconName,
                ParentalRatingIconName = overlayData.ParentalRatingIconName
            };
            EmbyIconsEnhancer._episodeIconCache[item.Id] = newInfo;
            _enhancer.PruneEpisodeCache();
            return overlayData;
        }

        private OverlayData ProcessMediaStreams(BaseItem item, PluginOptions options)
        {
            var data = new OverlayData
            {
                CommunityRating = item.CommunityRating,
                ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating)
            };
            var mainItemStreams = item.GetMediaStreams() ?? new List<MediaStream>();

            MediaStream? primaryVideoStream = mainItemStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            MediaStream? primaryAudioStream = mainItemStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();

            foreach (var stream in mainItemStreams)
            {
                if (stream.Type == MediaStreamType.Audio)
                {
                    if (!string.IsNullOrEmpty(stream.DisplayLanguage)) data.AudioLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                    var codecIcon = MediaStreamHelper.GetAudioCodecIconName(stream);
                    if (codecIcon != null) data.AudioCodecs.Add(codecIcon);
                }
                else if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.DisplayLanguage))
                {
                    data.SubtitleLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                }
                else if (stream.Type == MediaStreamType.Video)
                {
                    var codecIcon = MediaStreamHelper.GetVideoCodecIconName(stream);
                    if (codecIcon != null) data.VideoCodecs.Add(codecIcon);
                }
            }

            _enhancer._iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder)
                .TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutionKeys);

            if (primaryAudioStream != null) data.ChannelIconName = MediaStreamHelper.GetChannelIconName(primaryAudioStream);
            data.VideoFormatIconName = MediaStreamHelper.GetVideoFormatIconName(item, mainItemStreams);
            data.ResolutionIconName = primaryVideoStream != null ? MediaStreamHelper.GetResolutionIconNameFromStream(primaryVideoStream, knownResolutionKeys ?? new List<string>()) : null;
            data.AspectRatioIconName = MediaStreamHelper.GetAspectRatioIconName(primaryVideoStream);

            if (item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    data.Tags.Add(string.Intern(tag));
                }
            }
            return data;
        }
    }
}