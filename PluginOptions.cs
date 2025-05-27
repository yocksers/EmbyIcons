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
        }

        public override string EditorTitle => "EmbyIcons Settings";

        public override string EditorDescription =>
           "<h3 style='color:darkred;'>Tip:</h3>" +
           "<ul>" +
           "<li>If you replace an icon image with a new one of the same name, you may need to refresh metadata or restart the server to see the change.</li>" +
           "</ul>";

        [DisplayName("Icons Folder Path")]
        [Description("Folder containing your PNG icon images. For audio icons, use language codes like 'eng.png', 'fre.png', etc. For subtitle icons, use 'srt.eng.png', 'srt.jpn.png', etc.")]
        [Required(ErrorMessage = "Icons folder path is required.")]
        [CustomValidation(typeof(PluginOptions), nameof(ValidateIconsFolder))]
        public string IconsFolder
        {
            get => _iconsFolder;
            set => _iconsFolder = Environment.ExpandEnvironmentVariables(value ?? @"D:\icons");
        }

        [DisplayName("Icon Size (% of shorter side)")]
        [Description("Size of icons relative to the shorter side of the poster (height or width). For example, 10 means the icon will be 10% of that dimension.")]
        public int IconSize { get; set; }

        [DisplayName("Audio Icon Alignment")]
        [Description("Which corner of the poster to place the audio icons (e.g. top-left, bottom-right, etc.).")]
        public IconAlignment AudioIconAlignment { get; set; }

        [DisplayName("Subtitle Icon Alignment")]
        [Description("Which corner of the poster to place the subtitle icons (e.g. top-left, bottom-right, etc.).")]
        public IconAlignment SubtitleIconAlignment { get; set; }

        [DisplayName("Audio Icon Vertical Offset (%)")]
        [Description("Move audio icons up or down. Positive values move icons down, negative values move them up. Offset is relative to poster height.")]
        public int AudioIconVerticalOffset { get; set; }

        [DisplayName("Subtitle Icon Vertical Offset (%)")]
        [Description("Move subtitle icons up or down. Positive values move icons down, negative values move them up. Offset is relative to poster height.")]
        public int SubtitleIconVerticalOffset { get; set; }

        [DisplayName("Audio Languages to Detect")]
        [Description("List of language codes (e.g., eng,fre,jpn). Only these languages will trigger audio icon overlays. Separate multiple codes with commas.")]
        public string AudioLanguages { get; set; }

        [DisplayName("Subtitle Languages to Detect")]
        [Description("List of subtitle language codes (e.g., eng,fre,jpn). Only these languages will trigger subtitle icon overlays. Separate multiple codes with commas.")]
        public string SubtitleLanguages { get; set; }

        [DisplayName("Show Audio Icons")]
        [Description("Enable or disable drawing audio language icons on posters.")]
        public bool ShowAudioIcons { get; set; }

        [DisplayName("Show Subtitle Icons")]
        [Description("Enable or disable drawing subtitle language icons on posters.")]
        public bool ShowSubtitleIcons { get; set; }

        [DisplayName("Restrict to Libraries (comma separated names)")]
        [Description("Optional: limit icon overlays to specific Emby libraries. Use the library names shown in the dashboard, separated by commas. Leave blank to apply everywhere.")]
        public string SelectedLibraries { get; set; }

        [DisplayName("Show Series Icons If All Episodes Have Language")]
        [Description("Only show icons on series or season posters if every episode contains the specified audio/subtitle languages.")]
        public bool ShowSeriesIconsIfAllEpisodesHaveLanguage { get; set; }

        [DisplayName("Enable Overlay Logging")]
        [Description("Enable verbose overlay logging to help debug language detection. May affect performance on large libraries. Recommended only when troubleshooting.")]
        public bool EnableOverlayLogging { get; set; } = false;

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
