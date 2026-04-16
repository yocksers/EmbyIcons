using System.Collections.Generic;
using EmbyIcons.Configuration;

namespace EmbyIcons.Models
{
    internal class OverlayData
    {
        public HashSet<string> AudioLanguages { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SubtitleLanguages { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AudioCodecs { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VideoCodecs { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Tags { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SourceIcons { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public List<FilenameBasedIconData> FilenameBasedIcons { get; set; } = new();
        public string? ChannelIconName { get; set; }
        public string? VideoFormatIconName { get; set; }
        public string? ResolutionIconName { get; set; }
        public float? CommunityRating { get; set; }
        public float? RottenTomatoesRating { get; set; }
        public float? PopcornRating { get; set; }
        public int? PopcornVotes { get; set; }
        public float? MyAnimeListRating { get; set; }
        public string? AspectRatioIconName { get; set; }
        public string? ParentalRatingIconName { get; set; }
        public int? FavoriteCount { get; set; }
        public string? FrameRateIconName { get; set; }
        public string? OriginalLanguageIconName { get; set; }
        public string? SeriesStatusIconName { get; set; }
    }

    internal class FilenameBasedIconData
    {
        public string IconName { get; set; } = string.Empty;
        public IconAlignment Alignment { get; set; }
        public int Priority { get; set; }
        public bool HorizontalLayout { get; set; }
    }
}