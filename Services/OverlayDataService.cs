using EmbyIcons.Helpers;
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

        public OverlayData GetOverlayData(BaseItem item, PluginOptions options)
        {
            if (item is Series seriesItem && options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
            {
                var aggResult = _enhancer.GetAggregatedDataForParentSync(seriesItem, options);
                var seriesTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                if (seriesItem.Tags != null && seriesItem.Tags.Length > 0)
                {
                    foreach (var tag in seriesItem.Tags)
                    {
                        seriesTags.Add(string.Intern(tag));
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
                    Tags = seriesTags
                };
            }

            if (EmbyIconsEnhancer._episodeIconCache.TryGetValue(item.Id, out var cachedInfo) && cachedInfo.DateModifiedTicks == item.DateModified.Ticks)
            {
                Plugin.Instance?.Logger.Debug($"[EmbyIcons] Using cached icon info for '{item.Name}'.");

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
                    CommunityRating = item.CommunityRating
                };
            }

            Plugin.Instance?.Logger.Debug($"[EmbyIcons] No valid cache. Processing streams for '{item.Name}'.");
            var overlayData = ProcessMediaStreams(item, options);

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
                DateCached = System.DateTime.UtcNow
            };
            EmbyIconsEnhancer._episodeIconCache[item.Id] = newInfo;
            _enhancer.PruneEpisodeCache();

            return overlayData;
        }

        private OverlayData ProcessMediaStreams(BaseItem item, PluginOptions options)
        {
            var data = new OverlayData { CommunityRating = item.CommunityRating };
            var mainItemStreams = item.GetMediaStreams() ?? new List<MediaStream>();
            int maxChannels = 0;
            MediaStream? videoStream = null;

            foreach (var stream in mainItemStreams)
            {
                if (stream.Type == MediaStreamType.Audio)
                {
                    if (!string.IsNullOrEmpty(stream.Language)) data.AudioLanguages.Add(LanguageHelper.NormalizeLangCode(stream.Language));
                    if (stream.Channels.HasValue) maxChannels = System.Math.Max(maxChannels, stream.Channels.Value);
                    if (!string.IsNullOrEmpty(stream.Codec))
                    {
                        var codecIcon = MediaStreamHelper.GetAudioCodecIconName(stream.Codec);
                        if (codecIcon != null) data.AudioCodecs.Add(codecIcon);
                    }
                }
                else if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                {
                    data.SubtitleLanguages.Add(LanguageHelper.NormalizeLangCode(stream.Language));
                }
                else if (stream.Type == MediaStreamType.Video)
                {
                    if (videoStream == null) videoStream = stream;
                    if (!string.IsNullOrEmpty(stream.Codec))
                    {
                        var codecIcon = MediaStreamHelper.GetVideoCodecIconName(stream.Codec);
                        if (codecIcon != null) data.VideoCodecs.Add(codecIcon);
                    }
                }
            }

            data.ChannelIconName = maxChannels > 0 ? MediaStreamHelper.GetChannelIconName(maxChannels) : null;
            data.VideoFormatIconName = MediaStreamHelper.HasDolbyVision(item, mainItemStreams) ? "dv" : MediaStreamHelper.HasHdr10Plus(item, mainItemStreams) ? "hdr10plus" : MediaStreamHelper.HasHdr(item, mainItemStreams) ? "hdr" : null;
            data.ResolutionIconName = videoStream != null ? MediaStreamHelper.GetResolutionIconNameFromStream(videoStream) : null;

            if (item.Tags != null && item.Tags.Length > 0)
            {
                data.Tags = new HashSet<string>(item.Tags.Length);
                foreach (var tag in item.Tags)
                {
                    data.Tags.Add(string.Intern(tag));
                }
            }

            return data;
        }
    }
}