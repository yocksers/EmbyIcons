using EmbyIcons.Configuration;
using MediaBrowser.Model.Plugins;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EmbyIcons.Configuration
{
    public class LibraryMapping
    {
        public string LibraryId { get; set; } = string.Empty;
        public Guid ProfileId { get; set; }
    }

    public enum OutputFormat
    {
        Jpeg,
        Png,
        Auto
    }

    public class FilenameIconMapping
    {
        public string Keyword { get; set; } = string.Empty;
        public string IconName { get; set; } = string.Empty;
    }

    public class PluginOptions : BasePluginConfiguration
    {
        public string PersistedVersion { get; set; } = "1.0.0";
        public string IconsFolder { get; set; } = GetDefaultIconsFolder();

        private static string GetDefaultIconsFolder()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return @"C:\";
            else
                return "/";
        }

        public IconLoadingMode IconLoadingMode { get; set; } = IconLoadingMode.Hybrid;
        public bool EnableDebugLogging { get; set; } = false;
        public bool EnableCollectionProfileLookup { get; set; } = true;
        public bool EnableLazyIconLoading { get; set; } = true;
        public bool EnableIconTemplateCaching { get; set; } = true;

        public OutputFormat OutputFormat { get; set; } = OutputFormat.Auto;

        public int JpegQuality { get; set; } = 75;
        public bool EnableImageSmoothing { get; set; } = false;

        public List<IconProfile> Profiles { get; set; } = new List<IconProfile>();
        public List<LibraryMapping> LibraryProfileMappings { get; set; } = new List<LibraryMapping>();

        #region Advanced Settings
        public int MaxEpisodeCacheSize { get; set; } = 2000;
        public int MaxSeriesCacheSize { get; set; } = 500;
        public int MaxItemToProfileCacheSize { get; set; } = 20000;
        public int MaxCollectionToProfileCacheSize { get; set; } = 5000;
        public int EpisodeCacheSlidingExpirationHours { get; set; } = 6;
        public int CachePruningIntervalHours { get; set; } = 6;
        public int CacheMaintenanceIntervalHours { get; set; } = 1;
        public double GlobalConcurrencyMultiplier { get; set; } = 0.75; // Multiplied by processor count
        public bool ForceDisableSkiaSharp { get; set; } = false;
        
        #endregion

        #region Obsolete properties for migration
        [Obsolete]
        public string SelectedLibraries { get; set; } = string.Empty;
        [Obsolete]
        public bool ShowAudioIcons { get; set; } = true;
        [Obsolete]
        public bool ShowSubtitleIcons { get; set; } = true;
        [Obsolete]
        public bool ShowOverlaysForEpisodes { get; set; } = true;
        [Obsolete]
        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; } = true;
        [Obsolete]
        public bool ShowAudioChannelIcons { get; set; } = false;
        [Obsolete]
        public bool ShowAudioCodecIcons { get; set; } = false;
        [Obsolete]
        public bool ShowVideoFormatIcons { get; set; } = false;
        [Obsolete]
        public bool ShowVideoCodecIcons { get; set; } = false;
        [Obsolete]
        public bool ShowTagIcons { get; set; } = false;
        [Obsolete]
        public bool ShowResolutionIcons { get; set; } = false;
        [Obsolete]
        public bool ShowCommunityScoreIcon { get; set; } = false;
        [Obsolete]
        public bool ShowAspectRatioIcons { get; set; } = false;
        [Obsolete]
        public IconAlignment AudioIconAlignment { get; set; } = IconAlignment.TopLeft;
        [Obsolete]
        public bool AudioOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment SubtitleIconAlignment { get; set; } = IconAlignment.BottomLeft;
        [Obsolete]
        public bool SubtitleOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment ChannelIconAlignment { get; set; } = IconAlignment.TopLeft;
        [Obsolete]
        public bool ChannelOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment AudioCodecIconAlignment { get; set; } = IconAlignment.TopLeft;
        [Obsolete]
        public bool AudioCodecOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment VideoFormatIconAlignment { get; set; } = IconAlignment.TopRight;
        [Obsolete]
        public bool VideoFormatOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment VideoCodecIconAlignment { get; set; } = IconAlignment.TopRight;
        [Obsolete]
        public bool VideoCodecOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment TagIconAlignment { get; set; } = IconAlignment.BottomLeft;
        [Obsolete]
        public bool TagOverlayHorizontal { get; set; } = false;
        [Obsolete]
        public IconAlignment ResolutionIconAlignment { get; set; } = IconAlignment.BottomRight;
        [Obsolete]
        public bool ResolutionOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment CommunityScoreIconAlignment { get; set; } = IconAlignment.TopRight;
        [Obsolete]
        public bool CommunityScoreOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public IconAlignment AspectRatioIconAlignment { get; set; } = IconAlignment.BottomRight;
        [Obsolete]
        public bool AspectRatioOverlayHorizontal { get; set; } = true;
        [Obsolete]
        public ScoreBackgroundShape CommunityScoreBackgroundShape { get; set; } = ScoreBackgroundShape.None;
        [Obsolete]
        public string CommunityScoreBackgroundColor { get; set; } = "#404040";
        [Obsolete]
        public int CommunityScoreBackgroundOpacity { get; set; } = 80;
        [Obsolete]
        public int IconSize { get; set; } = 10;
        [Obsolete]
        public bool UseSeriesLiteMode { get; set; } = true;
        #endregion
    }

    public class IconProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public ProfileSettings Settings { get; set; }

        public IconProfile()
        {
            Id = Guid.NewGuid();
            Name = "New Profile";
            Settings = new ProfileSettings();
        }
    }

    public class ProfileSettings
    {
        public bool EnableForPosters { get; set; } = true;
        public bool EnableForThumbs { get; set; } = false;
        public bool EnableForBanners { get; set; } = false;

        public bool ShowOverlaysForEpisodes { get; set; } = true;
        public bool ShowOverlaysForSeasons { get; set; } = false;
        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; } = true;
        public bool ExcludeSpecialsFromSeriesAggregation { get; set; } = false;
        public bool ShowCollectionIconsIfAllChildrenHaveLanguage { get; set; } = true;
        public bool UseCollectionLiteMode { get; set; } = true;

        public IconAlignment AudioIconAlignment { get; set; } = IconAlignment.TopLeft;
        public bool AudioOverlayHorizontal { get; set; } = true;
        public int AudioIconPriority { get; set; } = 1;

        public IconAlignment SubtitleIconAlignment { get; set; } = IconAlignment.BottomLeft;
        public bool SubtitleOverlayHorizontal { get; set; } = true;
        public int SubtitleIconPriority { get; set; } = 2;

        public IconAlignment ChannelIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool ChannelOverlayHorizontal { get; set; } = true;
        public int ChannelIconPriority { get; set; } = 7;

        public IconAlignment AudioCodecIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool AudioCodecOverlayHorizontal { get; set; } = true;
        public int AudioCodecIconPriority { get; set; } = 8;

        public IconAlignment VideoFormatIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool VideoFormatOverlayHorizontal { get; set; } = true;
        public int VideoFormatIconPriority { get; set; } = 4;

        public IconAlignment VideoCodecIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool VideoCodecOverlayHorizontal { get; set; } = true;
        public int VideoCodecIconPriority { get; set; } = 5;

        public IconAlignment TagIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool TagOverlayHorizontal { get; set; } = false;
        public int TagIconPriority { get; set; } = 6;

        public IconAlignment ResolutionIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool ResolutionOverlayHorizontal { get; set; } = true;
        public int ResolutionIconPriority { get; set; } = 3;

        public IconAlignment CommunityScoreIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool CommunityScoreOverlayHorizontal { get; set; } = true;
        public int CommunityScoreIconPriority { get; set; } = 9;
        public IconAlignment RottenTomatoesScoreIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool RottenTomatoesScoreOverlayHorizontal { get; set; } = true;
        public int RottenTomatoesScoreIconPriority { get; set; } = 9;
        public ScoreBackgroundShape RottenTomatoesScoreBackgroundShape { get; set; } = ScoreBackgroundShape.None;
        public string RottenTomatoesScoreBackgroundColor { get; set; } = "#404040";
        public int RottenTomatoesScoreBackgroundOpacity { get; set; } = 80;

        public IconAlignment AspectRatioIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool AspectRatioOverlayHorizontal { get; set; } = true;
        public int AspectRatioIconPriority { get; set; } = 10;
        public bool SnapAspectRatioToCommon { get; set; } = true;

        public IconAlignment ParentalRatingIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool ParentalRatingOverlayHorizontal { get; set; } = true;
        public int ParentalRatingIconPriority { get; set; } = 11;

        public IconAlignment SourceIconAlignment { get; set; } = IconAlignment.Disabled;
        public bool SourceOverlayHorizontal { get; set; } = true;
        public int SourceIconPriority { get; set; } = 12;

        public ScoreBackgroundShape CommunityScoreBackgroundShape { get; set; } = ScoreBackgroundShape.None;
        public string CommunityScoreBackgroundColor { get; set; } = "#404040";
        public int CommunityScoreBackgroundOpacity { get; set; } = 80;

        public int IconSize { get; set; } = 10;
        public float RatingFontSizeMultiplier { get; set; } = 0.75f;
        public string RatingPercentageSuffix { get; set; } = "%";
        
        public bool UseSeriesLiteMode { get; set; } = true;
        public List<FilenameIconMapping> FilenameBasedIcons { get; set; } = new List<FilenameIconMapping>();
    }

    public enum IconLoadingMode
    {
        CustomOnly,
        Hybrid,
        BuiltInOnly
    }

    public enum IconAlignment
    {
        Disabled,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public enum ScoreBackgroundShape
    {
        None,
        Circle,
        Square
    }
}