using EmbyIcons.Caching;
using System.Collections.Generic;

namespace EmbyIcons.Configuration
{
    internal static class Constants
    {
        public const string Episode = StringConstants.EpisodeType;

        public const int DefaultProviderPathCacheSize = 5000;
        public const double CacheCompactionPercentage = 0.1;
        public const double MinMaintenanceIntervalHours = 0.5;

        public static readonly Dictionary<IconCacheManager.IconType, string> PrefixMap = new()
        {
            { IconCacheManager.IconType.Language, "lang" },
            { IconCacheManager.IconType.Subtitle, "sub" },
            { IconCacheManager.IconType.Channel, "ch" },
            { IconCacheManager.IconType.VideoFormat, "hdr" },
            { IconCacheManager.IconType.Resolution, "res" },
            { IconCacheManager.IconType.AudioCodec, "ac" },
            { IconCacheManager.IconType.VideoCodec, "vc" },
            { IconCacheManager.IconType.Tag, "tag" },
            { IconCacheManager.IconType.CommunityRating, "rating" },
            { IconCacheManager.IconType.AspectRatio, "ar" },
            { IconCacheManager.IconType.ParentalRating, "pr" },
            { IconCacheManager.IconType.Source, "source" },
            { IconCacheManager.IconType.FrameRate, "fps" }
        };
    }
}