using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;  // For ImageType
using MediaBrowser.Model.Drawing;
using SkiaSharp;
using EmbyIcons.Helpers; // <-- Added for WithCancellation extension method and Helpers

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        // Cache media path -> detected languages (audio, subtitles)
        private static readonly ConcurrentDictionary<string, (HashSet<string> AudioLangs, HashSet<string> SubtitleLangs)> _mediaLanguageCache
            = new();

        internal async Task<(HashSet<string> AudioLangs, HashSet<string> SubtitleLangs)> GetLanguagesForMediaAsync(string mediaPath, PluginOptions options, CancellationToken cancellationToken)
        {
            if (_mediaLanguageCache.TryGetValue(mediaPath, out var cached))
                return cached;

            var audioLangsDetected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLangsDetected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await DetectLanguagesWithTimeoutAsync(mediaPath, audioLangsDetected, subtitleLangsDetected, options.EnableLogging, cancellationToken);

            var subtitleExtensions = options.SubtitleFileExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? new[] { ".srt" };
            // Use cached subtitle files per folder
            string folder = Path.GetDirectoryName(mediaPath) ?? "";
            var knownSubtitleFiles = SubtitleFolderCacheHelper.GetSubtitleFilesForFolder(folder, subtitleExtensions);

            SubtitleScanner.ScanExternalSubtitlesWithKnownFiles(mediaPath, subtitleLangsDetected, options.EnableLogging, knownSubtitleFiles);

            _mediaLanguageCache[mediaPath] = (audioLangsDetected, subtitleLangsDetected);
            return (audioLangsDetected, subtitleLangsDetected);
        }

        private async Task DetectLanguagesWithTimeoutAsync(string mediaPath, HashSet<string> audioLangs, HashSet<string> subtitleLangs,
                                                          bool enableLogging, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30)); // Timeout after 30 seconds

            try
            {
                await Helpers.MediaInfoDetector.DetectLanguagesFromMediaAsync(mediaPath, audioLangs, subtitleLangs, enableLogging)
                    .WithCancellation(cts.Token);
            }
            catch (OperationCanceledException)
            {
                LoggingHelper.Log(enableLogging, $"Language detection timed out or cancelled for: {mediaPath}");
                // Optionally continue with partial results or empty sets
            }
        }

        internal async Task EnhanceImageInternalAsync(MediaBrowser.Controller.Entities.BaseItem item,
                                                     string inputFile,
                                                     string outputFile,
                                                     ImageType imageType,
                                                     int imageIndex,
                                                     CancellationToken cancellationToken)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();

            LoggingHelper.Log(options.EnableLogging, $"Starting EnhanceImageInternalAsync for {item.Name} ({item.Id})");

            // Check library restrictions
            var allowedLibs = Helpers.FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);
            var libraryId = Helpers.FileUtils.GetLibraryIdForItem(_libraryManager, item);
            if (allowedLibs.Count > 0 && (libraryId == null || !allowedLibs.Contains(libraryId)))
            {
                LoggingHelper.Log(options.EnableLogging, $"Skipping {item.Name} due to library restrictions.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                LoggingHelper.Log(options.EnableLogging, $"Input file missing or empty for {item.Name}: {inputFile}");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            HashSet<string> audioLangsDetected = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> subtitleLangsDetected = new(StringComparer.OrdinalIgnoreCase);

            if (item is MediaBrowser.Controller.Entities.TV.Series series && options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
            {
                LoggingHelper.Log(options.EnableLogging, $"Aggregating languages for series '{series.Name}'");
                (audioLangsDetected, subtitleLangsDetected) =
                    await GetAggregatedLanguagesForSeriesAsync(series, options, cancellationToken);
            }
            else
            {
                var supportedExtensions = options.SupportedExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant())
                    ?? new[] { ".mkv", ".mp4", ".avi", ".mov" };

                if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) &&
                    supportedExtensions.Contains(Path.GetExtension(item.Path).ToLowerInvariant()))
                {
                    (audioLangsDetected, subtitleLangsDetected) = await GetLanguagesForMediaAsync(item.Path!, options, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                else
                {
                    // Fallback: scan subtitles without caching if no media path or unsupported extension
                    var subtitleExtensions = options.SubtitleFileExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? new[] { ".srt" };
                    SubtitleScanner.ScanExternalSubtitles(item.Path ?? inputFile!, subtitleLangsDetected,
                        options.EnableLogging, subtitleExtensions);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            var audioLangsAllowed = Helpers.LanguageHelper.ParseLanguageList(options.AudioLanguages).Select(Helpers.LanguageHelper.NormalizeLangCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var subtitleLangsAllowed = Helpers.LanguageHelper.ParseLanguageList(options.SubtitleLanguages).Select(Helpers.LanguageHelper.NormalizeLangCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

            audioLangsDetected.IntersectWith(audioLangsAllowed);
            subtitleLangsDetected.IntersectWith(subtitleLangsAllowed);

            if (!options.ShowAudioIcons || audioLangsDetected.Count == 0)
                audioLangsDetected.Clear();

            if (!options.ShowSubtitleIcons || subtitleLangsDetected.Count == 0)
                subtitleLangsDetected.Clear();

            if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0)
            {
                LoggingHelper.Log(options.EnableLogging, $"No languages detected for '{item.Name}', copying original image.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                LoggingHelper.Log(options.EnableLogging, $"Failed to decode image '{inputFile}' for '{item.Name}', copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

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

            // Cache icons before drawing to optimize performance
            var audioIconsToDraw = audioLangsDetected
                .OrderBy(l => l)
                .Select(lang => _iconCacheManager.GetCachedIcon(lang, false))
                .Where(icon => icon != null)
                .ToList();

            var subtitleIconsToDraw = subtitleLangsDetected
                .OrderBy(l => l)
                .Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", true))
                .Where(icon => icon != null)
                .ToList();

            if (audioIconsToDraw.Count > 0)
                Helpers.IconDrawer.DrawIcons(canvas, audioIconsToDraw!, iconSize, padding,
                                             width, height,
                                             options.AudioIconAlignment,
                                             new SKPaint { FilterQuality = SKFilterQuality.High });

            if (subtitleIconsToDraw.Count > 0)
                Helpers.IconDrawer.DrawIcons(canvas, subtitleIconsToDraw!, iconSize, padding,
                                             width, height,
                                             options.SubtitleIconAlignment,
                                             new SKPaint { FilterQuality = SKFilterQuality.High });

            canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Png, 100);

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

            string tempOutput = outputFile + ".tmp";

            using (var fsOut = File.OpenWrite(tempOutput))
            {
                await encodedImg.AsStream().CopyToAsync(fsOut, cancellationToken);
                await fsOut.FlushAsync(cancellationToken);
            }

            File.Move(tempOutput, outputFile, overwrite: true);

            LoggingHelper.Log(options.EnableLogging, $"Finished EnhanceImageInternalAsync for '{item.Name}'.");
        }
    }
}