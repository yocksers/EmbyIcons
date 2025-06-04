using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Emby.Web.GenericEdit;

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
        private string _iconsFolder = @"D:\icons"; // Default value remains as per original, but user is warned if not found
        public PluginOptions()
        {
            IconSize = 10;

            AudioIconAlignment = IconAlignment.TopLeft;
            SubtitleIconAlignment = IconAlignment.BottomLeft;
            ChannelIconAlignment = IconAlignment.TopLeft;

            ShowAudioIcons = true;
            ShowSubtitleIcons = true;
            SelectedLibraries = string.Empty;
            ShowSeriesIconsIfAllEpisodesHaveLanguage = true;
            ShowOverlaysForEpisodes = true;
            AudioIconVerticalOffset = 0;
            SubtitleIconVerticalOffset = 0;
            ChannelIconVerticalOffset = 0; // Initialize new property

            JpegQuality = 75;
            EnableImageSmoothing = false;

            ShowAudioChannelIcons = false;
            ShowChannelIconsIfAllEpisodesHaveChannelInfo = false; // Initialize new property
        }

        public override string EditorTitle => "EmbyIcons Settings";

        public override string EditorDescription =>
           "<h3 style='color:darkred;'>Tip:</h3>" +
           "<ul>" +
           "<li>If you replace an icon image with a new one of the same name, you may need to refresh metadata or restart the server to see the change.</li>" +
           "</ul>";

        [DisplayName("Icons Folder Path")]
        [Description("Folder containing your icon images. For audio icons use language codes like 'eng.png', 'fre.png', etc. For subtitle icons, use 'srt.eng.png', 'srt.jpn.png', etc.")]
        [Required(ErrorMessage = "Icons folder path is required.")]
        public string IconsFolder
        {
            get => _iconsFolder;
            set => _iconsFolder = value ?? @"D:\icons";
        }

        [DisplayName("Refresh Icon Folder")]
        [Description("Check this box and save settings to force a reload of all icons from the Icons Folder. The box will automatically uncheck itself after the refresh.")]
        public bool RefreshIconCacheNow { get; set; }

        [DisplayName("Allowed Libraries")]
        [Description("A comma-separated list of libraries that should show overlays. Leave blank to allow all libraries.")]
        public string SelectedLibraries { get; set; } = string.Empty;

        [DisplayName("Icon Size (% of shorter side)")]
        [Description("Size of icons relative to the shorter side of the poster (height or width). For example, 10 means the icon will be 10% of that dimension.")]
        public int IconSize { get; set; }

        [DisplayName("Audio Icon Alignment")]
        [Description("Which corner of the poster to place the audio icons (e.g. top-left, bottom-right, etc.).")]
        public IconAlignment AudioIconAlignment { get; set; }

        [DisplayName("Subtitle Icon Alignment")]
        [Description("Which corner of the poster to place the subtitle icons (e.g. top-left, bottom-right, etc.).")]
        public IconAlignment SubtitleIconAlignment { get; set; }

        [DisplayName("Audio Channel Icon Alignment")]
        [Description("Which corner of the poster to place the audio channel icons (mono, stereo, 5.1, 7.1, etc.).")]
        public IconAlignment ChannelIconAlignment { get; set; } = IconAlignment.TopLeft;

        [DisplayName("Audio Icon Vertical Offset (%)")]
        [Description("Move audio icons up or down. Positive values move icons down, negative values move them up. Offset is relative to poster height.")]
        public int AudioIconVerticalOffset { get; set; }

        [DisplayName("Subtitle Icon Vertical Offset (%)")]
        [Description("Move subtitle icons up or down. Positive values move icons down, negative values move them up. Offset is relative to poster height.")]
        public int SubtitleIconVerticalOffset { get; set; }

        [DisplayName("Audio Channel Icon Vertical Offset (%)")]
        [Description("Move audio channel icons up or down. Positive values move icons down, negative values move them up. Offset is relative to poster height.")]
        public int ChannelIconVerticalOffset { get; set; } // New setting for channel icon offset

        [DisplayName("Show Audio Icons")]
        [Description("Enable or disable drawing audio language icons on posters.")]
        public bool ShowAudioIcons { get; set; }

        [DisplayName("Show Subtitle Icons")]
        [Description("Enable or disable drawing subtitle language icons on posters.")]
        public bool ShowSubtitleIcons { get; set; }

        [DisplayName("Show Overlays For Episodes")]
        [Description("Show language overlays on episode posters. If disabled, overlays are only shown on movies, series, etc.")]
        public bool ShowOverlaysForEpisodes { get; set; } = true;

        [DisplayName("Show Series Icons If All Episodes Have Language")]
        [Description("Only show icons on series or season posters if every episode contains the specified audio/subtitle languages.")]
        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; }

        [DisplayName("Show Audio Channel Overlay")]
        [Description("Overlay an icon for the highest audio channel count (mono, stereo, 5.1, 7.1, etc.) on posters.")]
        public bool ShowAudioChannelIcons { get; set; } = false;

        [DisplayName("Show Audio Channel Overlay If All Episodes Have Channel Info")]
        [Description("Only show audio channel icons on series or season posters if every episode contains the specified audio channel configuration.")]
        public bool ShowChannelIconsIfAllEpisodesHaveChannelInfo { get; set; } = false; // New setting for channel aggregation

        [DisplayName("JPEG Quality")]
        [Description("Set the output JPEG quality (1–100). Lower values increase speed and reduce file size, but may reduce image quality. Default: 75")]
        [Range(1, 100)]
        public int JpegQuality { get; set; } = 75;

        [DisplayName("Enable Image Smoothing (Anti-Aliasing)")]
        [Description("Enables smoothing/anti-aliasing for overlays. Disabling makes drawing slightly faster. Default: false")]
        public bool EnableImageSmoothing { get; set; }
    }
}