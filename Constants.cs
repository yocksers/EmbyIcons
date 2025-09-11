using EmbyIcons.Helpers;
using System.Collections.Generic;

namespace EmbyIcons
{
    internal static class Constants
    {
        public const string Episode = "Episode";

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
            { IconCacheManager.IconType.Source, "source" }
        };
    }
}