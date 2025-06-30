using EmbyIcons;
using MediaBrowser.Model.Plugins;
using System;

namespace EmbyIcons
{
    public class PluginOptions : BasePluginConfiguration
    {
        public string IconsFolder { get; set; } = GetDefaultIconsFolder();

        private static string GetDefaultIconsFolder()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return @"D:\icons";
            else
                return "/var/lib/emby/plugins/EmbyIcons/icons";
        }

        public bool RefreshIconCacheNow { get; set; }

        public string SelectedLibraries { get; set; } = string.Empty;

        public bool ShowAudioIcons { get; set; } = true;

        public bool ShowSubtitleIcons { get; set; } = true;

        public bool ShowOverlaysForEpisodes { get; set; } = true;

        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; } = true;

        public bool ShowAudioChannelIcons { get; set; } = false;

        public bool ShowVideoFormatIcons { get; set; } = false;

        public bool ShowResolutionIcons { get; set; } = false;

        public bool ShowCommunityScoreIcon { get; set; } = false;

        public IconAlignment AudioIconAlignment { get; set; } = IconAlignment.TopLeft;

        public bool AudioOverlayHorizontal { get; set; } = true;

        public IconAlignment SubtitleIconAlignment { get; set; } = IconAlignment.BottomLeft;

        public bool SubtitleOverlayHorizontal { get; set; } = true;

        public IconAlignment ChannelIconAlignment { get; set; } = IconAlignment.TopLeft;

        public bool ChannelOverlayHorizontal { get; set; } = true;

        public IconAlignment VideoFormatIconAlignment { get; set; } = IconAlignment.TopRight;

        public bool VideoFormatOverlayHorizontal { get; set; } = true;

        public IconAlignment ResolutionIconAlignment { get; set; } = IconAlignment.BottomRight;

        public bool ResolutionOverlayHorizontal { get; set; } = true;

        public IconAlignment CommunityScoreIconAlignment { get; set; } = IconAlignment.TopRight;

        public bool CommunityScoreOverlayHorizontal { get; set; } = true;

        public int IconSize { get; set; } = 10;

        public int JpegQuality { get; set; } = 75;

        public bool EnableImageSmoothing { get; set; } = false;

        public bool UseSeriesLiteMode { get; set; } = true;
    }

    public enum IconAlignment
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}