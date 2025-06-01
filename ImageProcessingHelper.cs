using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
            var options = Plugin.Instance!.GetConfiguredOptions()
                ?? throw new InvalidOperationException("Plugin options not initialized");

            // Define retry parameters for file operations
            const int maxFileOpRetries = 5;
            int fileOpRetries = 0;
            int fileOpDelayMs = 100;

            // --- First level checks and early exits ---
            if (!Helpers.IconDrawer.ShouldDrawAnyOverlays(item, options))
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                _logger.Warn($"[EmbyIcons] Input file for image enhancement is invalid or missing: '{inputFile}'. Item: {item?.Name} ({item?.Id}). Copying original instead.");
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
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();

                foreach (var stream in streams)
                {
                    if (stream.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = LanguageHelper.NormalizeLangCode(stream.Language);
                        audioLangsDetected.Add(norm);
                    }

                    if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = LanguageHelper.NormalizeLangCode(stream.Language);
                        subtitleLangsDetected.Add(norm);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!options.ShowAudioIcons)
                audioLangsDetected.Clear();

            if (!options.ShowSubtitleIcons)
                subtitleLangsDetected.Clear();

            if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0)
            {
                _logger.Debug($"[EmbyIcons] No relevant audio or subtitle languages detected for overlays on item: {item?.Name} ({item?.Id}). Copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            SKBitmap? surfBmp = null;
            while (true) // Retry loop for decoding/opening input file
            {
                try
                {
                    surfBmp = SKBitmap.Decode(inputFile);
                    if (surfBmp == null)
                    {
                        _logger.Error($"[EmbyIcons] SKBitmap.Decode failed for input file: '{inputFile}'. Item: {item?.Name} ({item?.Id}). This usually means the image is corrupted or not a valid format. Copying original instead.");
                        await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                        return;
                    }
                    break; // Success, exit retry loop
                }
                catch (IOException ioEx)
                {
                    fileOpRetries++;
                    if (fileOpRetries >= maxFileOpRetries)
                    {
                        _logger.ErrorException($"[EmbyIcons] Failed to decode input file '{inputFile}' due to IO error after {maxFileOpRetries} retries. Item: {item?.Name} ({item?.Id}). Copying original instead.", ioEx);
                        await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                        return;
                    }
                    _logger.Warn($"[EmbyIcons] Retrying SKBitmap.Decode for '{inputFile}' due to IO error. Retry {fileOpRetries}/{maxFileOpRetries}. Error: {ioEx.Message}");
                    await Task.Delay(fileOpDelayMs, cancellationToken);
                    fileOpDelayMs = Math.Min(5000, fileOpDelayMs * 2);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[EmbyIcons] Unexpected exception during SKBitmap.Decode for input file: '{inputFile}'. Item: {item?.Name} ({item?.Id}). Copying original instead.", ex);
                    if (surfBmp != null) surfBmp.Dispose();
                    await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                    return;
                }
            }

            fileOpRetries = 0;
            fileOpDelayMs = 100;

            await _iconCacheManager.InitializeAsync(options.IconsFolder!, cancellationToken);

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);
            int iconSize = Math.Max(16, (shortSide * options.IconSize) / 100);
            int padding = Math.Max(4, iconSize / 4);

            int audioVerticalOffsetPx = (shortSide * options.AudioIconVerticalOffset) / 100;
            int subtitleVerticalOffsetPx = (shortSide * options.SubtitleIconVerticalOffset) / 100;

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(surfBmp, 0, 0);
            surfBmp.Dispose();

            // --- USE THE NEW SMOOTHING OPTION ---
            var filterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None;
            using var paint = new SKPaint { FilterQuality = filterQuality };

            var audioIconsToDraw =
                audioLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon(lang, false)).Where(i => i != null).ToList();

            if (audioIconsToDraw.Count > 0)
            {
                _logger.Debug($"[EmbyIcons] Drawing {audioIconsToDraw.Count} audio icons for item: {item?.Name} ({item?.Id})");
                Helpers.IconDrawer.DrawIcons(canvas, audioIconsToDraw!, iconSize, padding,
                                             width, height,
                                             options.AudioIconAlignment,
                                             paint,
                                             audioVerticalOffsetPx);
            }

            var subtitleIconsToDraw =
                subtitleLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", true)).Where(i => i != null).ToList();

            if (subtitleIconsToDraw.Count > 0)
            {
                _logger.Debug($"[EmbyIcons] Drawing {subtitleIconsToDraw.Count} subtitle icons for item: {item?.Name} ({item?.Id})");
                Helpers.IconDrawer.DrawIcons(canvas, subtitleIconsToDraw!, iconSize, padding,
                                             width, height,
                                             options.SubtitleIconAlignment,
                                             paint,
                                             subtitleVerticalOffsetPx);
            }

            canvas.Flush();

            try
            {
                using var snapshot = surface.Snapshot();
                // --- USE THE NEW JPEG QUALITY OPTION ---
                using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality);

                while (true)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

                        string tempOutput = outputFile + "." + Guid.NewGuid() + ".tmp";

                        using (var fsOut = File.Create(tempOutput))
                        {
                            await encodedImg.AsStream().CopyToAsync(fsOut);
                            await fsOut.FlushAsync();
                        }

                        File.Move(tempOutput, outputFile, overwrite: true);
                        break;
                    }
                    catch (IOException ioEx)
                    {
                        fileOpRetries++;
                        if (fileOpRetries >= maxFileOpRetries)
                        {
                            _logger.ErrorException($"[EmbyIcons] Failed to save output image '{outputFile}' due to IO error after {maxFileOpRetries} retries. Item: {item?.Name} ({item?.Id}). Copying original as fallback.", ioEx);
                            await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                            return;
                        }
                        _logger.Warn($"[EmbyIcons] Retrying image save for '{outputFile}' due to IO error. Retry {fileOpRetries}/{maxFileOpRetries}. Error: {ioEx.Message}");
                        await Task.Delay(fileOpDelayMs, cancellationToken);
                        fileOpDelayMs = Math.Min(5000, fileOpDelayMs * 2);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"[EmbyIcons] Unexpected critical error during image encoding or file saving for item: {item?.Name} ({item?.Id}). Copying original as fallback.", ex);
                        await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Unhandled critical error during image enhancement for item: {item?.Name} ({item?.Id}). Copying original as fallback.", ex);
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
            }
        }
    }
}
