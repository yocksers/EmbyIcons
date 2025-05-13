using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    public class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IconCacheManager _iconCacheManager;

        public EmbyIconsEnhancer(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30));
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary) return false;
            if (item is Person) return false;

            return item is Movie || item is Episode || item is Series ||
                   item is Season || item is BoxSet || item is MusicVideo;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();
            var libsKey = (options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", "");
            return $"embyicons_{item.Id}_{imageType}_scale{options.IconSize}_libs{libsKey}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
           => new() { RequiresTransparency = true };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize)
           => originalSize;

        // Explicit interface implementation for no-cancellation token method required by IImageEnhancer
        Task IImageEnhancer.EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                              ImageType imageType, int imageIndex)
        {
            // Forward call with CancellationToken.None
            return EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);
        }

        // Your main method supporting cancellation token
        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                            ImageType imageType, int imageIndex,
                                            CancellationToken cancellationToken)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();

            // Check library restrictions
            var allowedLibs = FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);
            var libraryId = FileUtils.GetLibraryIdForItem(_libraryManager, item);
            if (allowedLibs.Count > 0 && (libraryId == null || !allowedLibs.Contains(libraryId)))
            {
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            // Validate input file exists
            if (string.IsNullOrEmpty(inputFile) || !System.IO.File.Exists(inputFile))
            {
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            // Detect languages in media & subtitles
            var audioLangsAllowed = LanguageHelper.ParseLanguageList(options.AudioLanguages).Select(LanguageHelper.NormalizeLangCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subtitleLangsAllowed = LanguageHelper.ParseLanguageList(options.SubtitleLanguages).Select(LanguageHelper.NormalizeLangCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var audioLangsDetected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLangsDetected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Check supported extensions
            var supportedExtensions =
                options.SupportedExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant())
                ?? new[] { ".mkv", ".mp4", ".avi", ".mov" };

            if (!string.IsNullOrEmpty(item.Path) && System.IO.File.Exists(item.Path) &&
                supportedExtensions.Contains(System.IO.Path.GetExtension(item.Path).ToLowerInvariant()))
            {
                await MediaInfoDetector.DetectLanguagesFromMediaAsync(item.Path!, audioLangsDetected,
                    subtitleLangsDetected,
                    options.EnableLogging);
                cancellationToken.ThrowIfCancellationRequested();
            }

            SubtitleScanner.ScanExternalSubtitles(item.Path ?? inputFile!, subtitleLangsDetected,
                options.EnableLogging);

            cancellationToken.ThrowIfCancellationRequested();

            audioLangsDetected.IntersectWith(audioLangsAllowed);
            subtitleLangsDetected.IntersectWith(subtitleLangsAllowed);

            if (!options.ShowAudioIcons || audioLangsDetected.Count == 0)
                audioLangsDetected.Clear();

            if (!options.ShowSubtitleIcons || subtitleLangsDetected.Count == 0)
                subtitleLangsDetected.Clear();

            if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0)
            {
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            // Decode original image
            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            // Initialize / refresh icon cache manager
            await _iconCacheManager.InitializeAsync(options.IconsFolder!, cancellationToken);

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);
            int iconSize = Math.Max(16, (shortSide * options.IconSize) / 100);
            int padding = Math.Max(4, iconSize / 4);

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(surfBmp, 0, 0);

            // Draw audio icons
            var audioIconsToDraw =
                audioLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon(lang, false)).Where(i => i != null).ToList();
            if (audioIconsToDraw.Count > 0)
                IconDrawer.DrawIcons(canvas, audioIconsToDraw!, iconSize, padding,
                                     width, height,
                                     options.AudioIconAlignment,
                                     new SKPaint { FilterQuality = SKFilterQuality.High });

            // Draw subtitle icons
            var subtitleIconsToDraw =
                subtitleLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", true)).Where(i => i != null).ToList();
            if (subtitleIconsToDraw.Count > 0)
                IconDrawer.DrawIcons(canvas, subtitleIconsToDraw!, iconSize, padding,
                                     width, height,
                                     options.SubtitleIconAlignment,
                                     new SKPaint { FilterQuality = SKFilterQuality.High });

            canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Png, 100);

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

            string tempOutput = outputFile + ".tmp";

            using (var fsOut = System.IO.File.OpenWrite(tempOutput))
            {
                await encodedImg.AsStream().CopyToAsync(fsOut, cancellationToken);
                await fsOut.FlushAsync(cancellationToken);
            }

            System.IO.File.Move(tempOutput, outputFile, overwrite: true);
        }

        public void Dispose()
        {
            _iconCacheManager.Dispose();
        }
    }
}