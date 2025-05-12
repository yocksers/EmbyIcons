using System;
using System.ComponentModel;
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
        private string _iconsFolder = @"D:\icons";
        private string _logFolder = @"C:\temp";

        public PluginOptions()
        {
            _iconsFolder = Environment.ExpandEnvironmentVariables(_iconsFolder);
            _logFolder = Environment.ExpandEnvironmentVariables(_logFolder);

            IconSize = 10;

            AudioIconAlignment = IconAlignment.TopLeft;
            SubtitleIconAlignment = IconAlignment.BottomLeft;

            EnableLogging = true;

            AudioLanguages = "eng,dan,fre,ger,spa,pol,jpn";
            SubtitleLanguages = "eng,dan,fre,ger,spa,pol,jpn";

            ShowAudioIcons = true;
            ShowSubtitleIcons = true;

            SelectedLibraries = string.Empty; // comma-separated library names
        }

        public override string EditorTitle => "EmbyIcons Settings";

        public override string EditorDescription =>
            "**Please reset server after changing settings for changes to take effect!**\n\n" +
            "Configure icon folder, icon size, alignments, logging options, languages to detect, and restrict to specific libraries by name.";

        [DisplayName("Icons Folder Path")]
        [Description("Full path containing language icon files.")]
        public string IconsFolder
        {
            get => _iconsFolder;
            set => _iconsFolder = Environment.ExpandEnvironmentVariables(value ?? @"D:\icons");
        }

        [DisplayName("Icon Size (% of shorter side)")]
        [Description("Size of icons overlaid on posters as % of the poster's shorter dimension.")]
        public int IconSize { get; set; }

        [DisplayName("Audio Icon Alignment")]
        [Description("Corner of the poster where audio icons are overlayed.")]
        public IconAlignment AudioIconAlignment { get; set; }

        [DisplayName("Subtitle Icon Alignment")]
        [Description("Corner of the poster where subtitle icons are overlayed.")]
        public IconAlignment SubtitleIconAlignment { get; set; }

        [DisplayName("Audio Languages to Detect")]
        [Description("Comma-separated audio language codes (e.g., eng,dan,jpn). Only these audio languages will have icons overlaid.")]
        public string AudioLanguages { get; set; }

        [DisplayName("Subtitle Languages to Detect")]
        [Description("Comma-separated subtitle language codes (e.g., eng,dan,jpn). Only these subtitle languages will have icons overlaid.")]
        public string SubtitleLanguages { get; set; }

        [DisplayName("Show Audio Icons")]
        [Description("Enable or disable overlaying audio language icons.")]
        public bool ShowAudioIcons { get; set; }

        [DisplayName("Show Subtitle Icons")]
        [Description("Enable or disable overlaying subtitle language icons.")]
        public bool ShowSubtitleIcons { get; set; }

        [DisplayName("Restrict to Libraries (comma separated names)")]
        [Description("Comma-separated list of library names to restrict plugin operation. Leave empty to process all libraries.")]
        public string SelectedLibraries { get; set; }

        [DisplayName("Enable Logging")]
        [Description("Enable or disable plugin logging.")]
        public bool EnableLogging { get; set; }

        [DisplayName("Log Folder Path")]
        [Description("Folder path where plugin logs will be saved.")]
        public string LogFolder
        {
            get => _logFolder;
            set => _logFolder = Environment.ExpandEnvironmentVariables(value ?? @"C:\temp");
        }
    }
}
