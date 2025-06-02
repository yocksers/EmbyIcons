using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
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

            const int maxFileOpRetries = 5;
            int fileOpRetries = 0;
            int fileOpDelayMs = 100;

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

            // --- Detect channel count ---
            int maxChannels = 0;
            var streamsForDetection = item.GetMediaStreams() ?? new List<MediaStream>();
            foreach (var stream in streamsForDetection)
            {
                if (stream.Type == MediaStreamType.Audio)
                {
                    if (stream.Channels.HasValue)
                        maxChannels = Math.Max(maxChannels, stream.Channels.Value);
                }
            }

            // Map maxChannels to icon name
            string? channelIconName = null;
            if (options.ShowAudioChannelIcons && maxChannels > 0)
            {
                if (maxChannels == 1) channelIconName = "mono";
                else if (maxChannels == 2) channelIconName = "stereo";
                else if (maxChannels == 6) channelIconName = "5.1";
                else if (maxChannels == 8) channelIconName = "7.1";
                else channelIconName = $"{maxChannels}ch"; // fallback for unusual counts
            }

            if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null)
            {
                _logger.Debug($"[EmbyIcons] No overlays for item: {item?.Name} ({item?.Id}). Copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            SKBitmap? surfBmp = null;
            while (true)
            {
                try
                {
                    surfBmp = SKBitmap.Decode(inputFile);
                    if (surfBmp == null)
                    {
                        _logger.Error($"[EmbyIcons] SKBitmap.Decode failed for input file: '{inputFile}'. Item: {item?.Name} ({item?.Id}). Copying original instead.");
                        await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                        return;
                    }
                    break;
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

            var filterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None;
            using var paint = new SKPaint { FilterQuality = filterQuality };

            // --- Build icon lists for each corner, stacking all icons that want that corner ---
            var topLeftIcons = new List<SKImage>();
            var topRightIcons = new List<SKImage>();
            var bottomLeftIcons = new List<SKImage>();
            var bottomRightIcons = new List<SKImage>();

            // Helper for getting list by alignment
            List<SKImage> GetList(IconAlignment align)
            {
                return align switch
                {
                    IconAlignment.TopLeft => topLeftIcons,
                    IconAlignment.TopRight => topRightIcons,
                    IconAlignment.BottomLeft => bottomLeftIcons,
                    IconAlignment.BottomRight => bottomRightIcons,
                    _ => topLeftIcons,
                };
            }

            // AUDIO LANGUAGE ICONS
            if (audioLangsDetected.Count > 0)
            {
                var audioIcons = audioLangsDetected.OrderBy(l => l)
                    .Select(lang => _iconCacheManager.GetCachedIcon(lang, false))
                    .Where(i => i != null)
                    .ToList();
                GetList(options.AudioIconAlignment).AddRange(audioIcons!);
            }

            // CHANNEL ICON (using user-selected alignment)
            if (channelIconName != null)
            {
                var icon = _iconCacheManager.GetCachedIcon(channelIconName, false);
                if (icon != null)
                {
                    GetList(options.ChannelIconAlignment).Add(icon);
                }
            }

            // SUBTITLE ICONS
            if (subtitleLangsDetected.Count > 0)
            {
                var subIcons = subtitleLangsDetected.OrderBy(l => l)
                    .Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", true))
                    .Where(i => i != null)
                    .ToList();
                GetList(options.SubtitleIconAlignment).AddRange(subIcons!);
            }

            // Draw all icons for each corner, stacked horizontally (default)
            if (topLeftIcons.Count > 0)
                Helpers.IconDrawer.DrawIcons(canvas, topLeftIcons, iconSize, padding, width, height, IconAlignment.TopLeft, paint, 0);

            if (topRightIcons.Count > 0)
                Helpers.IconDrawer.DrawIcons(canvas, topRightIcons, iconSize, padding, width, height, IconAlignment.TopRight, paint, 0);

            if (bottomLeftIcons.Count > 0)
                Helpers.IconDrawer.DrawIcons(canvas, bottomLeftIcons, iconSize, padding, width, height, IconAlignment.BottomLeft, paint, 0);

            if (bottomRightIcons.Count > 0)
                Helpers.IconDrawer.DrawIcons(canvas, bottomRightIcons, iconSize, padding, width, height, IconAlignment.BottomRight, paint, 0);

            canvas.Flush();

            try
            {
                using var snapshot = surface.Snapshot();
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
