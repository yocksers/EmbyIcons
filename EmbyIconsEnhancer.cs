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

namespace EmbyIcons
{
    public class EmbyIconsEnhancer : IImageEnhancer
    {
        private readonly PluginOptions _options;
        private readonly ILibraryManager _libraryManager;

        private static readonly string[] DefaultSupportedExtensions = { ".mkv", ".mp4", ".avi", ".mov" };
        private const int MinIconSize = 16;
        private const int MinPadding = 4;

        private readonly ConcurrentDictionary<string, string?> _audioIconCache = new();
        private readonly ConcurrentDictionary<string, string?> _subtitleIconCache = new();

        private DateTime _lastCacheRefreshTime = DateTime.MinValue;
        private string? _lastIconsFolder;

        private static readonly Dictionary<string, string> LanguageFallbackMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"eng", "en"},
            {"fre", "fr"},
            {"ger", "de"},
            {"jpn", "jp"}
        };

        public EmbyIconsEnhancer(ILibraryManager libraryManager, PluginOptions options)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _options = options ?? throw new ArgumentNullException(nameof(options));
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
            var optionsKey = string.Join("_", new[]
            {
                _options.IconSize.ToString(),
                _options.ShowAudioIcons.ToString(),
                _options.ShowSubtitleIcons.ToString(),
                _options.AudioLanguages ?? "",
                _options.SubtitleLanguages ?? "",
                _options.AudioIconAlignment.ToString(),
                _options.SubtitleIconAlignment.ToString(),
                (_options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", "")
            });

            return $"embyicons_{item.Id}_{imageType}_{optionsKey}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            new() { RequiresTransparency = true };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            originalSize;

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            return EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);
        }

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                            ImageType imageType, int imageIndex,
                                            CancellationToken cancellationToken)
        {
            var allowedLibraryIds = FileUtils.GetAllowedLibraryIds(_libraryManager, _options.SelectedLibraries);

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                LoggingHelper.Log(_options.EnableLogging,
                    $"Input missing or invalid for '{item.Name}', copying original");
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            var libraryId = FileUtils.GetLibraryIdForItem(_libraryManager, item);

            if (allowedLibraryIds.Count > 0 && (libraryId == null || !allowedLibraryIds.Contains(libraryId)))
            {
                LoggingHelper.Log(_options.EnableLogging,
                    $"Skipping item '{item.Name}' due to library restriction.");
                await FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            var audioLangsAllowed = LanguageHelper.ParseLanguageList(_options.AudioLanguages);
            var subtitleLangsAllowed = LanguageHelper.ParseLanguageList(_options.SubtitleLanguages);

            var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var supportedExtensions =
                _options.SupportedExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant())
                ?? DefaultSupportedExtensions;

            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) &&
                supportedExtensions.Contains(Path.GetExtension(item.Path).ToLowerInvariant()))
            {
                await MediaInfoDetector.DetectLanguagesFromMediaAsync(item.Path!, audioLangs,
                    subtitleLangs,
                    _options.EnableLogging);
                cancellationToken.ThrowIfCancellationRequested();
            }

            SubtitleScanner.ScanExternalSubtitles(item.Path ?? inputFile!, subtitleLangs,
                _options.EnableLogging);

            cancellationToken.ThrowIfCancellationRequested();

            audioLangs.IntersectWith(audioLangsAllowed);
            subtitleLangs.IntersectWith(subtitleLangsAllowed);

            if (!_options.ShowAudioIcons || audioLangs.Count == 0)
                audioLangs.Clear();

            if (!_options.ShowSubtitleIcons || subtitleLangs.Count == 0)
                subtitleLangs.Clear();

            if (audioLangs.Count == 0 && subtitleLangs.Count == 0)
            {
                LoggingHelper.Log(_options.EnableLogging,
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

            if (_lastIconsFolder != _options.IconsFolder ||
                (now - _lastCacheRefreshTime).TotalMinutes > _options.IconCacheDebounceMinutes)
            {
                await RefreshIconCachesAsync(_options.IconsFolder!, cancellationToken);
                _lastIconsFolder = _options.IconsFolder!;
                _lastCacheRefreshTime = now;
            }

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);
            int iconSize = Math.Max(MinIconSize, (shortSide * _options.IconSize) / 100);
            int padding = Math.Max(MinPadding, iconSize / 4);

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(surfBmp, 0, 0);

            var audioIconPaths =
                audioLangs.OrderBy(l => l).Select(lang =>
                    ResolveIconPathWithFallback(lang, _options.IconsFolder!, _audioIconCache))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Cast<string>()
                    .ToList();

            if (audioIconPaths.Count > 0)
                IconDrawer.DrawIcons(canvas,
                    audioIconPaths,
                    iconSize,
                    padding,
                    width,
                    height,
                    _options.AudioIconAlignment);

            var subtitleIconPaths =
                subtitleLangs.OrderBy(l => l).Select(lang =>
                    ResolveIconPathWithFallback($"srt.{lang}", _options.IconsFolder!, _subtitleIconCache))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Cast<string>()
                    .ToList();

            if (subtitleIconPaths.Count > 0)
                IconDrawer.DrawIcons(canvas,
                    subtitleIconPaths,
                    iconSize,
                    padding,
                    width,
                    height,
                    _options.SubtitleIconAlignment);

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

            File.Move(tempOutput, outputFile, overwrite: true);

            LoggingHelper.Log(_options.EnableLogging,
                $"Saved enhanced image to '{outputFile}'");
        }

        private async Task RefreshIconCachesAsync(string iconsFolderPath, CancellationToken cancellationToken)
        {
            try
            {
                var pngFiles = Directory.GetFiles(iconsFolderPath, "*.png");

                _audioIconCache.Clear();
                _subtitleIconCache.Clear();

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
    }
}
