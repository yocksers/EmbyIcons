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
        public PluginOptions()
        {
            _iconsFolder = Environment.ExpandEnvironmentVariables(_iconsFolder);
            IconSize = 10;

            AudioIconAlignment = IconAlignment.TopLeft;
            SubtitleIconAlignment = IconAlignment.BottomLeft;

            AudioLanguages = "eng,dan,fre,ger,spa,pol,jpn";
            SubtitleLanguages = "eng,dan,fre,ger,spa,pol,jpn";

            ShowAudioIcons = true;
            ShowSubtitleIcons = true;

            SelectedLibraries = string.Empty;
            ShowSeriesIconsIfAllEpisodesHaveLanguage = true;
            AudioIconVerticalOffset = 0;
            SubtitleIconVerticalOffset = 0;
            PosterUpdateDelaySeconds = 7; // Default value
            OverlayRefreshCounter = 0;    // Initialize hidden counter
        }

        public override string EditorTitle => "EmbyIcons Settings";

        public override string EditorDescription =>
           "<h2 style='color:red; font-weight:bold;'>Refreshing metadata or server reset might be needed when changing an icon for one with the same name!</h2><br/>" +
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

        [DisplayName("Audio Icon Vertical Offset (%)")]
        [Description("Vertical offset as % of the poster's shorter dimension. Positive moves down, negative moves up.")]
        public int AudioIconVerticalOffset { get; set; }

        [DisplayName("Subtitle Icon Vertical Offset (%)")]
        [Description("Vertical offset as % of the poster's shorter dimension. Positive moves down, negative moves up.")]
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

        [DisplayName("Show Series Icons If All Episodes Have Language")]
        [Description("Show icons on series posters if all episodes have the specified audio/subtitle languages.")]
        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; }

        [DisplayName("Poster Overlay Update Delay (seconds)")]
        [Description("How many seconds to wait after a change before refreshing a TV series poster overlay. Increase if your server is slow to scan new files.")]
        [Range(0, 120)]
        public int PosterUpdateDelaySeconds { get; set; } = 7;

        [DisplayName("Enable Overlay Logging")]
        [Description("Enable detailed plugin overlay logging for troubleshooting (resource-intensive on large libraries).")]
        public bool EnableOverlayLogging { get; set; } = false;

        // ---- HIDDEN COUNTER FOR FORCING OVERLAY REFRESH ----
        [Browsable(false)]
        public int OverlayRefreshCounter { get; set; } = 0;

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
