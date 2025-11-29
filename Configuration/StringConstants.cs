namespace EmbyIcons.Configuration
{
    /// <summary>
    /// Centralized string constants used throughout the plugin
    /// </summary>
    internal static class StringConstants
    {
        // Video format detection keywords
        public const string DolbyVision = "dolby vision";
        public const string DolbyVisionCompact = "dolbyvision";
        public const string DolbyShort = "dolby";
        public const string DVShort = "dv";
        public const string HDR10Plus = "hdr10+";
        public const string HDR10PlusCompact = "hdr10plus";
        public const string HDR = "hdr";

        // Provider IDs
        public const string RottenTomatoesProvider = "rotten";
        public const string RTShort = "rt";
        public const string ImdbProvider = "Imdb";
        public const string TmdbProvider = "Tmdb";

        // Rotten Tomatoes icons
        public const string TomatoIcon = "t.tomato";
        public const string SplatIcon = "t.splat";
        public const string ImdbIcon = "imdb";

        // Rating property names for reflection
        public const string RatingPropertyName = "Rating";
        public const string RatingsPropertyName = "Ratings";
        public const string ExternalPropertyName = "External";
        public const string SourcePropertyName = "Source";
        public const string NamePropertyName = "Name";
        public const string KeyPropertyName = "Key";
        public const string ValuePropertyName = "Value";
        public const string ScorePropertyName = "Score";

        // Episode type
        public const string EpisodeType = "Episode";
        public const string MovieType = "Movie";

        // Log prefixes
        public const string LogPrefix = "[EmbyIcons]";

        // Common format strings
        public const string PercentFormat = "F1";
    }
}
