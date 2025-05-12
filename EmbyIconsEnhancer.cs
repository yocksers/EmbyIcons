using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using SkiaSharp;
using MediaBrowser.Controller.Library;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace EmbyIcons
{
    public class EmbyIconsEnhancer : IImageEnhancer
    {
        private readonly ILibraryManager _libraryManager;

        public EmbyIconsEnhancer(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary)
                return false;

            if (item is Person)
                return false;

            return item is Movie || item is Episode || item is Series || item is Season || item is BoxSet || item is MusicVideo;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var options = Plugin.Instance.GetConfiguredOptions();
            var libsKey = (options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", "");
            return $"embyicons_{item.Id}_{imageType}_scale{options.IconSize}_libs{libsKey}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            new() { RequiresTransparency = true };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            originalSize;

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            var options = Plugin.Instance.GetConfiguredOptions();
            var allowedLibraryIds = FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                LoggingHelper.Log(options.EnableLogging, $"Input missing or invalid for '{item.Name}', copying original");
                FileUtils.SafeCopy(inputFile, outputFile);
                return;
            }

            var libraryId = FileUtils.GetLibraryIdForItem(_libraryManager, item);

            if (allowedLibraryIds.Count > 0 && (libraryId == null || !allowedLibraryIds.Contains(libraryId)))
            {
                LoggingHelper.Log(options.EnableLogging, $"Skipping item '{item.Name}' due to library restriction.");
                FileUtils.SafeCopy(inputFile, outputFile);
                return;
            }

            var audioLangsAllowed = LanguageHelper.ParseLanguageList(options.AudioLanguages);
            var subtitleLangsAllowed = LanguageHelper.ParseLanguageList(options.SubtitleLanguages);

            var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) &&
                new[] { ".mkv", ".mp4", ".avi", ".mov" }.Contains(Path.GetExtension(item.Path).ToLowerInvariant()))
            {
                await MediaInfoDetector.DetectLanguagesFromMediaAsync(item.Path, audioLangs, subtitleLangs, options.EnableLogging);
            }

            SubtitleScanner.ScanExternalSubtitles(item.Path ?? inputFile, subtitleLangs, options.EnableLogging);

            audioLangs.IntersectWith(audioLangsAllowed);
            subtitleLangs.IntersectWith(subtitleLangsAllowed);

            if (!options.ShowAudioIcons || audioLangs.Count == 0)
                audioLangs.Clear();

            if (!options.ShowSubtitleIcons || subtitleLangs.Count == 0)
                subtitleLangs.Clear();

            if (audioLangs.Count == 0 && subtitleLangs.Count == 0)
            {
                LoggingHelper.Log(options.EnableLogging, $"No icons to draw for '{item.Name}', copying original");
                FileUtils.SafeCopy(inputFile, outputFile);
                return;
            }

            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                LoggingHelper.Log(true, $"Failed to decode original image for '{item.Name}', copying original");
                FileUtils.SafeCopy(inputFile, outputFile);
                return;
            }

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);
            int iconSize = Math.Max(16, (shortSide * options.IconSize) / 100);
            int padding = Math.Max(4, iconSize / 4);

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(surfBmp, 0, 0);

            if (audioLangs.Count > 0)
                IconDrawer.DrawIcons(canvas,
                          audioLangs.OrderBy(l => l).Select(l => Path.Combine(options.IconsFolder ?? @"D:\icons", $"{l}.png")).Where(File.Exists).ToList(),
                          iconSize,
                          padding,
                          width,
                          height,
                          options.AudioIconAlignment);

            if (subtitleLangs.Count > 0)
                IconDrawer.DrawIcons(canvas,
                          subtitleLangs.OrderBy(l => l).Select(l => Path.Combine(options.IconsFolder ?? @"D:\icons", $"srt.{l}.png")).Where(File.Exists).ToList(),
                          iconSize,
                          padding,
                          width,
                          height,
                          options.SubtitleIconAlignment);

            canvas.Flush();

            using var image = surface.Snapshot();
            using var encodedImg = image.Encode(SKEncodedImageFormat.Png, 100);

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

            // Write to temp file first
            string tempOutput = outputFile + ".tmp";

            using (var fsOut = File.OpenWrite(tempOutput))
            {
                await encodedImg.AsStream().CopyToAsync(fsOut);
                await fsOut.FlushAsync();
            }

            // Atomically replace target file
            File.Move(tempOutput, outputFile, overwrite: true);

            LoggingHelper.Log(options.EnableLogging, $"Saved enhanced image to '{outputFile}'");
        }
    }
}