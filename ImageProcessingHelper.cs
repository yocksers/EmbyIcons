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

            // ======= Optimization: Skip processing if cachekey matches and output file is valid =======
            string cacheKey = GetConfigurationCacheKey(item, imageType);
            string cacheKeyPath = outputFile + ".cachekey";
            if (File.Exists(outputFile) && File.Exists(cacheKeyPath))
            {
                try
                {
                    string lastCacheKey = File.ReadAllText(cacheKeyPath);
                    if (lastCacheKey == cacheKey)
                    {
                        // Also check output file is newer than input
                        var outInfo = new FileInfo(outputFile);
                        var inInfo = new FileInfo(inputFile);
                        if (outInfo.LastWriteTimeUtc > inInfo.LastWriteTimeUtc)
                        {
                            // Optionally: Also check if icon files in cachekey are not newer, or always trust the cachekey
                            return; // Overlay is unchanged; skip processing!
                        }
                    }
                }
                catch
                {
                    // Ignore errors; fall through to normal processing
                }
            }
            // =========================================================================================

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
                        if (options.EnableLogging)
                            LoggingHelper.Log(true, $"[Embedded] Audio stream: {stream.Language} => {norm}");
                    }

                    if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = LanguageHelper.NormalizeLangCode(stream.Language);
                        subtitleLangsDetected.Add(norm);
                        if (options.EnableLogging)
                            LoggingHelper.Log(true, $"[Embedded] Subtitle stream: {stream.Language} => {norm}");
                    }
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
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                // Also remove cachekey file as overlay is not present
                try { if (File.Exists(cacheKeyPath)) File.Delete(cacheKeyPath); } catch { }
                return;
            }

            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                try { if (File.Exists(cacheKeyPath)) File.Delete(cacheKeyPath); } catch { }
                return;
            }

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
                // SkiaSharp only accepts int for PNG encoding in this version.
                using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Png, 100);

                Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

                string tempOutput = outputFile + "." + Guid.NewGuid() + ".tmp";

                using (var fsOut = File.Create(tempOutput))
                {
                    await encodedImg.AsStream().CopyToAsync(fsOut);
                    await fsOut.FlushAsync();
                }

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
                        delayMs = Math.Min(1000, delayMs * 2); // Exponential backoff
                    }
                }
                // ======= Write/Update cache key sidecar file =======
                try
                {
                    File.WriteAllText(cacheKeyPath, cacheKey);
                }
                catch { /* ignore errors */ }
                // ===================================================

            }
            catch
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                try { if (File.Exists(cacheKeyPath)) File.Delete(cacheKeyPath); } catch { }
            }
        }
    }
}