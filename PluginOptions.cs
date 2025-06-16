using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace EmbyIcons
{
    public enum IconAlignment
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public class PluginOptions : EditableOptionsBase
    {
        [DisplayName("Icons Folder Path")]
        [Description("Folder containing your icon images. For audio icons use language codes like 'eng.png', 'fre.png', etc. For subtitle icons, use 'srt.eng.png', 'srt.jpn.png', etc. For audio channel icons, use 'mono.png', 'stereo.png', '5.1.png', '7.1.png', etc. For HDR and Dolby Vision, use 'hdr.png' and 'dv.png'. If both HDR and DV are detected, 'dv.png' will be prioritized. For resolution icons, use '480p.png', '576p.png', '720p.png', '1080p.png', or '4k.png'. Supports all common image formats (PNG, JPG, WebP, etc.).")]
        [EditFolderPicker]
        public string IconsFolder { get; set; } = GetDefaultIconsFolder();

        private static string GetDefaultIconsFolder()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return @"D:\icons";
            else
                return "/var/lib/emby/plugins/EmbyIcons/icons";
        }

        [DisplayName("Refresh Icon Folder")]
        [Description("Check this box and save settings to force a reload of all icons from the Icons Folder.")]
        public bool RefreshIconCacheNow { get; set; }

        [DisplayName("Allowed Libraries")]
        [Description("A comma-separated list of libraries that should show overlays like for example Movies, TV shows. Leave blank to allow all libraries.")]
        public string SelectedLibraries { get; set; } = string.Empty;

        [DisplayName("Show Audio Icons")]
        [Description("Enable or disable drawing audio language icons on posters.")]
        public bool ShowAudioIcons { get; set; } = true;

        [DisplayName("Show Subtitle Icons")]
        [Description("Enable or disable drawing subtitle language icons on posters.")]
        public bool ShowSubtitleIcons { get; set; } = true;

        [DisplayName("Show Overlays For Episodes")]
        [Description("Show overlays on episode posters. If disabled, overlays are only shown on movies, TV shows, etc.")]
        public bool ShowOverlaysForEpisodes { get; set; } = true;

        // FIX #3: Renamed property and updated description for clarity
        [DisplayName("Aggregate Series Properties")]
        [Description("For TV Shows, only show an icon on the series poster if the corresponding property (e.g., language, resolution, HDR) is consistent across all episodes. This ensures the series poster accurately reflects its entire contents. (Uses first episode in Lite Mode).")]
        public bool AggregateSeriesProperties { get; set; } = true;

        [DisplayName("Show Audio Channel Overlay")]
        [Description("Overlay an icon for the highest audio channel count (mono, stereo, 5.1, 7.1, etc.) on posters.")]
        public bool ShowAudioChannelIcons { get; set; } = false;

        [DisplayName("Show Video Format Overlay")]
        [Description("Overlay an icon for video formats (HDR or Dolby Vision). Dolby Vision takes precedence if both are detected.")]
        public bool ShowVideoFormatIcons { get; set; } = false;

        [DisplayName("Show Resolution Overlay")]
        [Description("Overlay an icon for the video resolution (e.g., 480p, 720p, 1080p, 4k).")]
        public bool ShowResolutionIcons { get; set; } = false;

        [DisplayName("Audio Icon Alignment")]
        [Description("Which corner of the poster to place the audio icons.")]
        public IconAlignment AudioIconAlignment { get; set; } = IconAlignment.TopLeft;

        [DisplayName("Audio Overlay Horizontal")]
        [Description("Show audio language icons horizontally (side by side) if more than one in the same corner. If disabled, they stack vertically.")]
        public bool AudioOverlayHorizontal { get; set; } = true;

        [DisplayName("Subtitle Icon Alignment")]
        [Description("Which corner of the poster to place the subtitle icons.")]
        public IconAlignment SubtitleIconAlignment { get; set; } = IconAlignment.BottomLeft;

        [DisplayName("Subtitle Overlay Horizontal")]
        [Description("Show subtitle language icons horizontally (side by side) if more than one in the same corner. If disabled, they stack vertically.")]
        public bool SubtitleOverlayHorizontal { get; set; } = true;

        [DisplayName("Audio Channel Icon Alignment")]
        [Description("Which corner of the poster to place the audio channel icons (mono, stereo, 5.1, 7.1, etc.).")]
        public IconAlignment ChannelIconAlignment { get; set; } = IconAlignment.TopLeft;

        [DisplayName("Channel Overlay Horizontal")]
        [Description("Show audio channel icons horizontally if multiple overlays appear in the same corner.")]
        public bool ChannelOverlayHorizontal { get; set; } = true;

        [DisplayName("Video Format Icon Alignment")]
        [Description("Which corner of the poster to place the video format icon (HDR or Dolby Vision). Dolby Vision takes precedence if both are detected.")]
        public IconAlignment VideoFormatIconAlignment { get; set; } = IconAlignment.TopRight;

        [DisplayName("Video Format Overlay Horizontal")]
        [Description("Show video format overlays (HDR/DV) horizontally if multiple overlays appear in the same corner.")]
        public bool VideoFormatOverlayHorizontal { get; set; } = true;

        [DisplayName("Resolution Icon Alignment")]
        [Description("Which corner of the poster to place the resolution icons (e.g., 480p, 720p, 1080p, 4k).")]
        public IconAlignment ResolutionIconAlignment { get; set; } = IconAlignment.BottomRight;

        [DisplayName("Resolution Overlay Horizontal")]
        [Description("Show resolution overlays horizontally if multiple overlays appear in the same corner.")]
        public bool ResolutionOverlayHorizontal { get; set; } = true;

        [DisplayName("Icon Size (% of shorter side)")]
        [Description("Size of icons relative to the shorter side of the poster (height or width). For example, 10 means the icon will be 10% of that dimension.")]
        public int IconSize { get; set; } = 10;

        [DisplayName("JPEG Quality")]
        [Description("Set the output JPEG quality for overlays (10-100). Default is 75.")]
        public int JpegQuality { get; set; } = 75;

        [DisplayName("Enable Image Smoothing")]
        [Description("Enable smoothing/antialiasing for overlays (may help on low-res posters).")]
        public bool EnableImageSmoothing { get; set; } = false;

        [DisplayName("Use Lite Mode for TV Shows")]
        [Description("When enabled, only the first episode of a TV show is checked for overlay properties. This is much faster but less accurate than the full scan. Recommended for low-power devices.")]
        public bool UseSeriesLiteMode { get; set; } = false;

        public override string EditorTitle => "EmbyIcons Settings";

        public override string EditorDescription =>
             "<h3 style='color:darkred;'>Tip:</h3>" +
             "<ul>" +
             "<li>" +
             "<a href='https://buymeacoffee.com/yockser' target='_blank' style='color: #00a4dc;'>If you like the plugin donate a coffee to the developer</a>" +
             "</li>" +
             "</ul>" +
             "<div style='margin-top:10px;'>" +
             "<strong>Many thanks to Neminem for his help with testing!!</strong><br/>" +
             "For more features like the ones in this plugin, please take a look at the plugins <b>CoverArt</b> and <b>Iconic</b>!" +
             "</div>";
    }
}