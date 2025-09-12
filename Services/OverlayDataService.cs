using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace EmbyIcons.Services
{
    internal class OverlayDataService
    {
        private readonly EmbyIconsEnhancer _enhancer;
        private readonly ILibraryManager _libraryManager;

        public OverlayDataService(EmbyIconsEnhancer enhancer, ILibraryManager libraryManager)
        {
            _enhancer = enhancer;
            _libraryManager = libraryManager;
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            var t = tag.Trim().ToLowerInvariant();
            t = System.Text.RegularExpressions.Regex.Replace(t, "\\s+", "-");
            return t;
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

        public OverlayData GetOverlayData(BaseItem item, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (item is Series seriesItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(seriesItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, seriesItem);
            }

            if (item is Season seasonItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(seasonItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, seasonItem);
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

            if (EmbyIconsEnhancer._episodeIconCache.TryGetValue(item.Id, out EmbyIconsEnhancer.EpisodeIconInfo cachedInfo) && cachedInfo.DateModifiedTicks == item.DateModified.Ticks)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) Plugin.Instance?.Logger.Debug($"[EmbyIcons] Using cached icon info for '{item.Name}'.");
                return new OverlayData
                {
                    AudioLanguages = cachedInfo.AudioLangs,
                    SubtitleLanguages = cachedInfo.SubtitleLangs,
                    AudioCodecs = cachedInfo.AudioCodecs,
                    VideoCodecs = cachedInfo.VideoCodecs,
                    Tags = cachedInfo.Tags,
                    SourceIcons = cachedInfo.SourceIcons,
                    ChannelIconName = cachedInfo.ChannelIconName,
                    VideoFormatIconName = cachedInfo.VideoFormatIconName,
                    ResolutionIconName = cachedInfo.ResolutionIconName,
                    CommunityRating = item.CommunityRating,
                    AspectRatioIconName = cachedInfo.AspectRatioIconName,
                    ParentalRatingIconName = cachedInfo.ParentalRatingIconName
                };
            }

            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) Plugin.Instance?.Logger.Debug($"[EmbyIcons] No valid cache. Processing streams for '{item.Name}'.");
            var overlayData = ProcessMediaStreams(item, profileOptions, globalOptions);

            var newInfo = new EmbyIconsEnhancer.EpisodeIconInfo
            {
                AudioLangs = overlayData.AudioLanguages,
                SubtitleLangs = overlayData.SubtitleLanguages,
                AudioCodecs = overlayData.AudioCodecs,
                VideoCodecs = overlayData.VideoCodecs,
                Tags = overlayData.Tags,
                SourceIcons = overlayData.SourceIcons,
                ChannelIconName = overlayData.ChannelIconName,
                VideoFormatIconName = overlayData.VideoFormatIconName,
                ResolutionIconName = overlayData.ResolutionIconName,
                DateModifiedTicks = item.DateModified.Ticks,
                AspectRatioIconName = overlayData.AspectRatioIconName,
                ParentalRatingIconName = overlayData.ParentalRatingIconName
            };

            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetSlidingExpiration(TimeSpan.FromHours(6));

            EmbyIconsEnhancer._episodeIconCache.Set(item.Id, newInfo, cacheEntryOptions);

            return overlayData;
        }

        private OverlayData ProcessMediaStreams(BaseItem item, ProfileSettings profileOptions, PluginOptions options)
        {
            var data = new OverlayData
            {
                CommunityRating = item.CommunityRating,
                ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating)
            };

            if (item is Movie movieItem && profileOptions.FilenameBasedIcons.Any())
            {
                var allPaths = new List<string>();
                var allVersions = new List<Movie> { movieItem };

                var providerIdKey = movieItem.ProviderIds.Keys.FirstOrDefault(k => k.Equals("Imdb", StringComparison.OrdinalIgnoreCase) || k.Equals("Tmdb", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(providerIdKey) && movieItem.ProviderIds.TryGetValue(providerIdKey, out var providerIdValue) && !string.IsNullOrEmpty(providerIdValue))
                {
                    var query = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Movie" },
                        Recursive = true
                    };

                    var itemsInLibrary = _libraryManager.GetItemList(query).OfType<Movie>();

                    foreach (var libraryMovie in itemsInLibrary)
                    {
                        if (libraryMovie.ProviderIds.TryGetValue(providerIdKey, out var value) && value == providerIdValue)
                        {
                            allVersions.Add(libraryMovie);
                        }
                    }
                }

                foreach (var version in allVersions.Distinct(new BaseItemComparer()))
                {
                    if (!string.IsNullOrEmpty(version.Path))
                    {
                        allPaths.Add(version.Path.ToLowerInvariant());
                    }
                }

                foreach (var path in allPaths.Distinct())
                {
                    foreach (var mapping in profileOptions.FilenameBasedIcons)
                    {
                        if (!string.IsNullOrWhiteSpace(mapping.Keyword) &&
                            !string.IsNullOrWhiteSpace(mapping.IconName) &&
                            path.Contains(mapping.Keyword.ToLowerInvariant()))
                        {
                            data.SourceIcons.Add(mapping.IconName.ToLowerInvariant());
                        }
                    }
                }
            }

            if (item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    var nt = NormalizeTag(tag);
                    if (!string.IsNullOrEmpty(nt)) data.Tags.Add(nt);
                }
            }

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
            data.AspectRatioIconName = MediaStreamHelper.GetAspectRatioIconName(primaryVideoStream, profileOptions.SnapAspectRatioToCommon);

            return data;
        }
    }

    class BaseItemComparer : IEqualityComparer<BaseItem>
    {
        public bool Equals(BaseItem? x, BaseItem? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.Id == y.Id;
        }

        public int GetHashCode([DisallowNull] BaseItem obj)
        {
            return obj.Id.GetHashCode();
        }
    }
}