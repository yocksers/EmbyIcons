using System.Collections.Generic;

namespace EmbyIcons.Models
{
    internal class OverlayData
    {
        public HashSet<string> AudioLanguages { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SubtitleLanguages { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AudioCodecs { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VideoCodecs { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Tags { get; set; } = new();
        public HashSet<string> SourceIcons { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public string? ChannelIconName { get; set; }
        public string? VideoFormatIconName { get; set; }
        public string? ResolutionIconName { get; set; }
        public float? CommunityRating { get; set; }
        public float? RottenTomatoesRating { get; set; }
        public string? AspectRatioIconName { get; set; }
        public string? ParentalRatingIconName { get; set; }
        public int? FavoriteCount { get; set; }
        public string? FrameRateIconName { get; set; }
        public string? OriginalLanguageIconName { get; set; }
    }
}