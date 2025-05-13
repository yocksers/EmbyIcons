using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Providers;

// Optimized: lazy decoding, smart icon caching, batching, and unnecessary copy avoidance
namespace EmbyIcons
{
    public class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private readonly ILibraryManager _libraryManager;

        private static readonly string[] DefaultSupportedExtensions = { ".mkv", ".mp4", ".avi", ".mov" };
        private const int MinIconSize = 16;
        private const int MinPadding = 4;

        private readonly ConcurrentDictionary<string, string?> _audioIconCache = new();
        private readonly ConcurrentDictionary<string, string?> _subtitleIconCache = new();

        private readonly ConcurrentDictionary<string, SKBitmap> _audioIconBitmapCache = new();
        private readonly ConcurrentDictionary<string, SKBitmap> _subtitleIconBitmapCache = new();

        private DateTime _lastCacheRefreshTime = DateTime.MinValue;
        private string? _lastIconsFolder;

        private static readonly Dictionary<string, string> LanguageFallbackMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"eng", "en"},
            {"fre", "fr"},
            {"ger", "de"},
            {"jpn", "jp"}
            // Add more as needed
        };

        public EmbyIconsEnhancer(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary)
                return false;

            if (item is Person)
                return false;

            return item is Movie || item is Episode || item is Series || item is Season || item is BoxSet || item is MusicVideo;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();
            var libsKey = (options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", "");
            return $"embyicons_{item.Id}_{imageType}_scale{options.IconSize}_libs{libsKey}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            new() { RequiresTransparency = true };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            originalSize;

        // Explicit interface implementation — required signature
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            // Calls internal method with CancellationToken.None
            return EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);
        }

        // Internal method with CancellationToken support
        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                           ImageType imageType, int imageIndex,
                                           CancellationToken cancellationToken)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();

            var allowedLibraryIds = FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                LoggingHelper.Log(options.EnableLogging,
                    $"Input missing or invalid for '{item.Name}', copying original");
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            var libraryId = FileUtils.GetLibraryIdForItem(_libraryManager, item);

            if (allowedLibraryIds.Count > 0 && (libraryId == null || !allowedLibraryIds.Contains(libraryId)))
            {
                LoggingHelper.Log(options.EnableLogging,
                    $"Skipping item '{item.Name}' due to library restriction.");
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            var audioLangsAllowed = LanguageHelper.ParseLanguageList(options.AudioLanguages);
            var subtitleLangsAllowed = LanguageHelper.ParseLanguageList(options.SubtitleLanguages);

            var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var supportedExtensions =
                options.SupportedExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant())
                ?? DefaultSupportedExtensions;

            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) &&
                supportedExtensions.Contains(Path.GetExtension(item.Path).ToLowerInvariant()))
            {
                await MediaInfoDetector.DetectLanguagesFromMediaAsync(item.Path!, audioLangs,
                    subtitleLangs,
                    options.EnableLogging);
                cancellationToken.ThrowIfCancellationRequested();
            }

            SubtitleScanner.ScanExternalSubtitles(item.Path ?? inputFile!, subtitleLangs,
                options.EnableLogging);

            cancellationToken.ThrowIfCancellationRequested();

            audioLangs.IntersectWith(audioLangsAllowed);
            subtitleLangs.IntersectWith(subtitleLangsAllowed);

            if (!options.ShowAudioIcons || audioLangs.Count == 0)
                audioLangs.Clear();

            if (!options.ShowSubtitleIcons || subtitleLangs.Count == 0)
                subtitleLangs.Clear();

            if (audioLangs.Count == 0 && subtitleLangs.Count == 0)
            {
                LoggingHelper.Log(options.EnableLogging,
                    $"No icons to draw for '{item.Name}', copying original");
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                LoggingHelper.Log(true,
                    $"Failed to decode original image for '{item.Name}', copying original");
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;

            if (_lastIconsFolder != options.IconsFolder ||
                (now - _lastCacheRefreshTime).TotalMinutes > options.IconCacheDebounceMinutes)
            {
                await RefreshIconCachesAsync(options.IconsFolder!, cancellationToken);
                _lastIconsFolder = options.IconsFolder!;
                _lastCacheRefreshTime = now;
            }

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);
            int iconSize = Math.Max(MinIconSize, (shortSide * options.IconSize) / 100);
            int padding = Math.Max(MinPadding, iconSize / 4);

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(surfBmp, 0, 0);

            // Get cached SKBitmap icons for audio languages
            var audioIcons =
                audioLangs.OrderBy(l => l)
                          .Select(lang => GetCachedIconBitmap(lang, isSubtitle: false, options.IconsFolder!))
                          .Where(bmp => bmp != null)
                          .Cast<SKBitmap>()
                          .ToList();

            if (audioIcons.Count > 0)
                IconDrawer.DrawIcons(canvas,
                                     audioIcons,
                                     iconSize,
                                     padding,
                                     width,
                                     height,
                                     options.AudioIconAlignment);

            // Get cached SKBitmap icons for subtitle languages
            var subtitleIcons =
                subtitleLangs.OrderBy(l => l)
                             .Select(lang => GetCachedIconBitmap($"srt.{lang}", isSubtitle: true, options.IconsFolder!))
                             .Where(bmp => bmp != null)
                             .Cast<SKBitmap>()
                             .ToList();

            if (subtitleIcons.Count > 0)
                IconDrawer.DrawIcons(canvas,
                                     subtitleIcons,
                                     iconSize,
                                     padding,
                                     width,
                                     height,
                                     options.SubtitleIconAlignment);

            canvas.Flush();

            using var image = surface.Snapshot();

            using var encodedImg = image.Encode(SKEncodedImageFormat.Png, 100);

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

            string tempOutput = outputFile + ".tmp";

            using (var fsOut = File.OpenWrite(tempOutput))
            {
                await encodedImg.AsStream().CopyToAsync(fsOut, cancellationToken);
                await fsOut.FlushAsync(cancellationToken);
            }

#pragma warning disable CA2000 // Dispose objects before losing scope - handled here

#pragma warning restore CA2000

#pragma warning disable CS4014 // Because this call is not awaited - we await below

#pragma warning restore CS4014

            // Atomically replace target file
            File.Move(tempOutput, outputFile, overwrite: true);

            LoggingHelper.Log(options.EnableLogging,
                $"Saved enhanced image to '{outputFile}'");
        }

        private async Task RefreshIconCachesAsync(string iconsFolderPath,
                                                  CancellationToken cancellationToken)
        {
            try
            {
                var pngFiles = Directory.GetFiles(iconsFolderPath, "*.png");

                // Clear path caches
                _audioIconCache.Clear();
                _subtitleIconCache.Clear();

                // Dispose cached bitmaps and clear bitmap caches
                foreach (var bmp in _audioIconBitmapCache.Values)
                {
                    bmp.Dispose();
                }
                _audioIconBitmapCache.Clear();

                foreach (var bmp in _subtitleIconBitmapCache.Values)
                {
                    bmp.Dispose();
                }
                _subtitleIconBitmapCache.Clear();

                await Task.Run(() =>
                {
                    foreach (var file in pngFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                        if (fileName.StartsWith("srt."))
                        {
                            _subtitleIconCache[fileName] = file;
                        }
                        else
                        {
                            _audioIconCache[fileName] = file;
                        }
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingHelper.Log(true, $"Error refreshing icon caches: {ex}");
            }
        }

        /// <summary>
        /// Returns a cached SKBitmap for the given language code key.
        /// If not cached, decodes the icon from disk and caches it.
        /// </summary>
        /// <param name="langCodeKey">Language code key or icon name</param>
        /// <param name="isSubtitle">True if subtitle icon; false if audio icon</param>
        /// <param name="iconsFolder">Base icons folder path</param>
        /// <returns>Cached SKBitmap or null if not found</returns>
        private SKBitmap? GetCachedIconBitmap(string langCodeKey, bool isSubtitle, string iconsFolder)
        {
            langCodeKey = langCodeKey.ToLowerInvariant();

            var pathCache = isSubtitle ? _subtitleIconCache : _audioIconCache;
            var bitmapCache = isSubtitle ? _subtitleIconBitmapCache : _audioIconBitmapCache;

            // Resolve path with fallback as before
            string? iconPath = ResolveIconPathWithFallback(langCodeKey, iconsFolder, pathCache);
            if (iconPath == null || !File.Exists(iconPath))
                return null;

            // Try get from bitmap cache
            if (bitmapCache.TryGetValue(iconPath, out var cachedBitmap))
            {
                return cachedBitmap;
            }

            // Load and cache
            var bmp = SKBitmap.Decode(iconPath);
            if (bmp != null)
            {
                bitmapCache[iconPath] = bmp;
            }

            return bmp;
        }

        /// <summary>
        /// Resolves icon file path by language code key with fallback support.
        /// Caches lookup results in the provided cache dictionary.
        /// </summary>
        private static string? ResolveIconPathWithFallback(string langCodeKey, string iconsFolderPath,
                                                         ConcurrentDictionary<string, string?> cache)
        {
            langCodeKey = langCodeKey.ToLowerInvariant();

            if (cache.TryGetValue(langCodeKey, out var path) && !string.IsNullOrEmpty(path))
            {
                return path!;
            }

            if (LanguageFallbackMap.TryGetValue(langCodeKey, out var fallbackCode))
            {
                if (cache.TryGetValue(fallbackCode, out var fallbackPath) && !string.IsNullOrEmpty(fallbackPath))
                {
                    return fallbackPath!;
                }
                else
                {
                    var fbPath = Path.Combine(iconsFolderPath, $"{fallbackCode}.png");
                    if (File.Exists(fbPath))
                    {
                        cache[fallbackCode] = fbPath;
                        return fbPath;
                    }
                }
            }

            var candidate = Path.Combine(iconsFolderPath, $"{langCodeKey}.png");
            if (File.Exists(candidate))
            {
                cache[langCodeKey] = candidate;
                return candidate;
            }

            cache[langCodeKey] = null!;
            return null;
        }

        /// <summary>
        /// Dispose cached bitmaps when this enhancer is disposed.
        /// </summary>
        public void Dispose()
        {
            foreach (var bmp in _audioIconBitmapCache.Values)
                bmp.Dispose();

            _audioIconBitmapCache.Clear();

            foreach (var bmp in _subtitleIconBitmapCache.Values)
                bmp.Dispose();

            _subtitleIconBitmapCache.Clear();
        }
    }
}