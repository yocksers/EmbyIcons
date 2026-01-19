using EmbyIcons.Api;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.ItemsByIcon, "GET", Summary = "Gets media items that use a specific icon")]
    public class GetItemsByIcon : IReturn<ItemsByIconResponse>
    {
        [ApiMember(Name = "IconType", Description = "The type of icon (Language, Subtitle, Channel, AudioCodec, VideoCodec, VideoFormat, Resolution, AspectRatio, Tag, ParentalRating, FrameRate)", IsRequired = true)]
        public string IconType { get; set; } = string.Empty;

        [ApiMember(Name = "IconName", Description = "The name of the icon (e.g., 'en', '5.1', 'hevc')", IsRequired = true)]
        public string IconName { get; set; } = string.Empty;

        [ApiMember(Name = "Limit", Description = "Maximum number of items to return", IsRequired = false)]
        public int? Limit { get; set; }
    }

    public class ItemsByIconResponse
    {
        public List<MediaItemInfo> Items { get; set; } = new List<MediaItemInfo>();
        public int TotalCount { get; set; }
    }

    public class MediaItemInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? SeriesName { get; set; }
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
    }

    public class ItemsByIconService : IService
    {
        private readonly ILibraryManager _libraryManager;

        public ItemsByIconService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetItemsByIcon request)
        {
            if (string.IsNullOrWhiteSpace(request.IconType) || string.IsNullOrWhiteSpace(request.IconName))
            {
                return new ItemsByIconResponse();
            }

            var iconType = request.IconType;
            var iconName = request.IconName.ToLowerInvariant();
            var limit = request.Limit ?? 100;

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", Constants.Episode },
                IsVirtualItem = false,
                Recursive = true,
                Limit = 5000
            };

            var allItems = _libraryManager.GetItemList(query);
            var matchingItems = new List<MediaItemInfo>();

            var options = Plugin.Instance?.GetConfiguredOptions();
            List<string> knownResolutions = new List<string>();
            if (options != null && iconType.Equals("Resolution", StringComparison.OrdinalIgnoreCase))
            {
                var iconCacheManager = Plugin.Instance?.Enhancer._iconCacheManager;
                if (iconCacheManager != null)
                {
                    var customIcons = iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder);
                    if (customIcons.TryGetValue(Caching.IconCacheManager.IconType.Resolution, out var resolutionKeys))
                    {
                        knownResolutions = resolutionKeys;
                    }
                }
            }

            foreach (var item in allItems)
            {
                if (matchingItems.Count >= limit) break;

                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                if (!streams.Any() && !iconType.Equals("Tag", StringComparison.OrdinalIgnoreCase) && !iconType.Equals("ParentalRating", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool matches = false;

                switch (iconType)
                {
                    case "Language":
                        matches = streams.Any(s => s.Type == MediaStreamType.Audio && 
                            !string.IsNullOrEmpty(s.DisplayLanguage) && 
                            LanguageHelper.NormalizeLangCode(s.DisplayLanguage).Equals(iconName, StringComparison.OrdinalIgnoreCase));
                        break;

                    case "Subtitle":
                        matches = streams.Any(s => s.Type == MediaStreamType.Subtitle && 
                            !string.IsNullOrEmpty(s.DisplayLanguage) && 
                            LanguageHelper.NormalizeLangCode(s.DisplayLanguage).Equals(iconName, StringComparison.OrdinalIgnoreCase));
                        break;

                    case "Channel":
                        var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
                        if (primaryAudio != null)
                        {
                            var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                            matches = ch != null && ch.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        }
                        break;

                    case "AudioCodec":
                        matches = streams.Any(s => s.Type == MediaStreamType.Audio)
                            && streams.Where(s => s.Type == MediaStreamType.Audio)
                                .Select(MediaStreamHelper.GetAudioCodecIconName)
                                .Any(codec => codec != null && codec.Equals(iconName, StringComparison.OrdinalIgnoreCase));
                        break;

                    case "VideoCodec":
                        matches = streams.Any(s => s.Type == MediaStreamType.Video)
                            && streams.Where(s => s.Type == MediaStreamType.Video)
                                .Select(MediaStreamHelper.GetVideoCodecIconName)
                                .Any(codec => codec != null && codec.Equals(iconName, StringComparison.OrdinalIgnoreCase));
                        break;

                    case "VideoFormat":
                        var format = MediaStreamHelper.GetVideoFormatIconName(item, streams);
                        matches = format != null && format.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        break;

                    case "Resolution":
                        var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                        if (videoStream != null)
                        {
                            var res = MediaStreamHelper.GetResolutionIconNameFromStream(videoStream, knownResolutions);
                            matches = res != null && res.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        }
                        break;

                    case "AspectRatio":
                        var videoStreamAR = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                        if (videoStreamAR != null)
                        {
                            var ar = MediaStreamHelper.GetAspectRatioIconName(videoStreamAR, true);
                            matches = ar != null && ar.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        }
                        break;

                    case "FrameRate":
                        var videoStreamFPS = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                        if (videoStreamFPS != null)
                        {
                            var fps = MediaStreamHelper.GetFrameRateIconName(videoStreamFPS, true);
                            matches = fps != null && fps.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        }
                        break;

                    case "Tag":
                        matches = item.Tags != null && item.Tags.Any(tag => tag.Equals(iconName, StringComparison.OrdinalIgnoreCase));
                        break;

                    case "ParentalRating":
                        var rating = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating);
                        matches = rating != null && rating.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        break;

                    case "OriginalLanguage":
                        var originalLang = GetOriginalLanguageFromItem(item);
                        if (!string.IsNullOrEmpty(originalLang))
                        {
                            var normalizedOriginalLang = LanguageHelper.NormalizeLangCode(originalLang);
                            matches = normalizedOriginalLang.Equals(iconName, StringComparison.OrdinalIgnoreCase);
                        }
                        break;
                }

                if (matches)
                {
                    var itemInfo = new MediaItemInfo
                    {
                        Id = item.Id.ToString(),
                        Name = item.Name,
                        Type = item.GetType().Name,
                        Path = item.Path ?? ""
                    };

                    if (item is Episode episode)
                    {
                        itemInfo.SeriesName = episode.SeriesName;
                        itemInfo.SeasonNumber = episode.ParentIndexNumber;
                        itemInfo.EpisodeNumber = episode.IndexNumber;
                    }

                    matchingItems.Add(itemInfo);
                }
            }

            return new ItemsByIconResponse
            {
                Items = matchingItems,
                TotalCount = matchingItems.Count
            };
        }

        private static string? GetOriginalLanguageFromItem(BaseItem item)
        {
            if (item == null) return null;

            try
            {
                var itemType = item.GetType();
                var originalLangProp = itemType.GetProperty("OriginalLanguage");
                if (originalLangProp != null)
                {
                    var value = originalLangProp.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                var productionLocationsProp = itemType.GetProperty("ProductionLocations");
                if (productionLocationsProp != null)
                {
                    var locations = productionLocationsProp.GetValue(item) as string[];
                    if (locations != null && locations.Length > 0 && !string.IsNullOrWhiteSpace(locations[0]))
                    {
                        return locations[0];
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
