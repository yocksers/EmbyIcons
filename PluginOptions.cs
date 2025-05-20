using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
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

            SelectedLibraries = string.Empty;

            SupportedExtensions = ".mkv,.mp4,.avi,.mov";

            SubtitleFileExtensions = ".srt,.ass,.vtt";

            ShowSeriesIconsIfAllEpisodesHaveLanguage = true;

            AudioIconVerticalOffset = 0; // New property default
            SubtitleIconVerticalOffset = 0; // New property default
        }

        public override string EditorTitle => "EmbyIcons Settings";

        public override string EditorDescription =>
           "<h2 style='color:red; font-weight:bold;'>Refreshing metadata or server reset might be needed when chaging an icon for one with the same name!</h2><br/>" +
           "Best to test your settings with one video at a time but not required.";

        [DisplayName("Icons Folder Path")]
        [Description("Full path containing language icon files.")]
        [Required(ErrorMessage = "Icons folder path is required.")]
        [CustomValidation(typeof(PluginOptions), nameof(ValidateIconsFolder))]
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

        [DisplayName("Audio Icon Vertical Offset (px)")]
        [Description("Vertical offset in pixels from the default audio icon position. Positive moves down, negative moves up.")]
        public int AudioIconVerticalOffset { get; set; }

        [DisplayName("Subtitle Icon Vertical Offset (px)")]
        [Description("Vertical offset in pixels from the default subtitle icon position. Positive moves down, negative moves up.")]
        public int SubtitleIconVerticalOffset { get; set; }

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

        [DisplayName("Supported Media Extensions")]
        [Description("Comma-separated list of supported media file extensions for language detection.")]
        public string SupportedExtensions { get; set; }

        [DisplayName("External Subtitle File Extensions")]
        [Description("Comma-separated list of external subtitle file extensions to scan (e.g., .srt,.ass,.vtt).")]
        public string SubtitleFileExtensions { get; set; }

        [DisplayName("Show Series Icons If All Episodes Have Language")]
        [Description("Show icons on series posters if all episodes have the specified audio/subtitle languages.")]
        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; }

        [DisplayName("Enable Logging")]
        [Description("Enable or disable plugin logging.")]
        [Display(Order = 1000)]
        public bool EnableLogging { get; set; }

        [DisplayName("Log Folder Path")]
        [Description("Folder path where plugin logs will be saved.")]
        [Display(Order = 1001)]
        public string LogFolder
        {
            get => _logFolder;
            set => _logFolder = Environment.ExpandEnvironmentVariables(value ?? @"C:\temp");
        }

        public static ValidationResult? ValidateIconsFolder(string? folderPath, ValidationContext context)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return new ValidationResult("Icons folder path cannot be empty.");

            if (!Directory.Exists(folderPath))
                return new ValidationResult($"Icons folder path '{folderPath}' does not exist.");

            try
            {
                var pngFiles = Directory.GetFiles(folderPath, "*.png");
                if (pngFiles.Length == 0)
                    return new ValidationResult($"No PNG icon files found in '{folderPath}'.");
            }
            catch (Exception ex)
            {
                return new ValidationResult($"Error accessing icons folder: {ex.Message}");
            }

            return ValidationResult.Success;
        }
    }
}
