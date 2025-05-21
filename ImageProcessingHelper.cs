using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Drawing;
using SkiaSharp;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal async Task EnhanceImageInternalAsync(BaseItem item,
                                                     string inputFile,
                                                     string outputFile,
                                                     ImageType imageType,
                                                     int imageIndex,
                                                     CancellationToken cancellationToken)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();

            var allowedLibs = Helpers.FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);
            var libraryId = Helpers.FileUtils.GetLibraryIdForItem(_libraryManager, item);
            if (allowedLibs.Count > 0 && (libraryId == null || !allowedLibs.Contains(libraryId)))
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            HashSet<string> audioLangsDetected = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> subtitleLangsDetected = new(StringComparer.OrdinalIgnoreCase);

            if (item is MediaBrowser.Controller.Entities.TV.Series series && options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
            {
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
                    await Helpers.MediaInfoDetector.DetectLanguagesFromMediaAsync(item.Path!, audioLangsDetected,
                        subtitleLangsDetected,
                        options.EnableLogging);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var subtitleExtensions = options.SubtitleFileExtensions?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? new[] { ".srt" };

                Helpers.SubtitleScanner.ScanExternalSubtitles(item.Path ?? inputFile!, subtitleLangsDetected,
                    options.EnableLogging, subtitleExtensions);
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
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            using (var surfBmp = SKBitmap.Decode(inputFile))
            {
                if (surfBmp == null)
                {
                    await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                    return;
                }

                await _iconCacheManager.InitializeAsync(options.IconsFolder!, cancellationToken);

                int width = surfBmp.Width;
                int height = surfBmp.Height;
                int shortSide = Math.Min(width, height);
                int iconSize = Math.Max(16, (shortSide * options.IconSize) / 100);
                int padding = Math.Max(4, iconSize / 4);

                // Convert vertical offset % to pixels based on shorter side length
                int audioVerticalOffsetPx = (shortSide * options.AudioIconVerticalOffset) / 100;
                int subtitleVerticalOffsetPx = (shortSide * options.SubtitleIconVerticalOffset) / 100;

                using (var surface = SKSurface.Create(new SKImageInfo(width, height)))
                {
                    var canvas = surface.Canvas;

                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawBitmap(surfBmp, 0, 0);

                    var audioIconsToDraw =
                        audioLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon(lang, false)).Where(i => i != null).ToList();

                    if (audioIconsToDraw.Count > 0)
                        Helpers.IconDrawer.DrawIcons(canvas, audioIconsToDraw!, iconSize, padding,
                                                     width, height,
                                                     options.AudioIconAlignment,
                                                     new SKPaint { FilterQuality = SKFilterQuality.High },
                                                     audioVerticalOffsetPx);

                    var subtitleIconsToDraw =
                        subtitleLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", true)).Where(i => i != null).ToList();

                    if (subtitleIconsToDraw.Count > 0)
                        Helpers.IconDrawer.DrawIcons(canvas, subtitleIconsToDraw!, iconSize, padding,
                                                     width, height,
                                                     options.SubtitleIconAlignment,
                                                     new SKPaint { FilterQuality = SKFilterQuality.High },
                                                     subtitleVerticalOffsetPx);

                    canvas.Flush();

                    try
                    {
                        using var snapshot = surface.Snapshot();
                        using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Png, 100);

                        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

                        string tempOutput = outputFile + "." + Guid.NewGuid() + ".tmp";

                        using (var fsOut = File.Create(tempOutput))
                        {
                            await encodedImg.AsStream().CopyToAsync(fsOut);
                            await fsOut.FlushAsync();
                        }

                        // Retry loop for atomic move to avoid file lock conflicts
                        const int maxRetries = 5;
                        int retries = 0;
                        int delayMs = 100;

                        while (true)
                        {
                            try
                            {
                                File.Move(tempOutput, outputFile, overwrite: true);
                                break;
                            }
                            catch (IOException)
                            {
                                retries++;
                                if (retries >= maxRetries)
                                    throw;

                                await Task.Delay(delayMs);
                                delayMs = Math.Min(1000, delayMs * 2); // Exponential backoff capped at 1 sec
                            }
                        }
                    }
                    catch
                    {
                        // Fallback: copy original image to output to prevent corrupted file
                        await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                    }
                }
            }
        }
    }
}