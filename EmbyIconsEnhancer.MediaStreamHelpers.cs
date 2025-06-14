using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal Task EnhanceImageInternalAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, CancellationToken cancellationToken)
        {
            try
            {
                var options = Plugin.Instance?.GetConfiguredOptions();
                if (options == null) throw new InvalidOperationException("Plugin options not initialized");

                if (!IconDrawer.ShouldDrawAnyOverlays(item, options))
                {
                    return FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                }

                return EnhanceImageInternalWithOverlaysAsync(item, inputFile, outputFile, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Unhandled error preparing for enhancement for {item?.Name}.", ex);
                return Task.CompletedTask;
            }
        }

        private async Task EnhanceImageInternalWithOverlaysAsync(BaseItem item, string inputFile, string outputFile, CancellationToken cancellationToken)
        {
            try
            {
                var options = Plugin.Instance?.GetConfiguredOptions();
                if (options == null) throw new InvalidOperationException("Plugin options not initialized");

                if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
                {
                    _logger.Warn($"[EmbyIcons] Input file for image enhancement is invalid: '{inputFile}'. Item: {item?.Name}. Copying original.");
                    await FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                    return;
                }

                var inputInfo = new FileInfo(inputFile);
                if (inputInfo.Length < 100)
                {
                    _logger.Warn($"[EmbyIcons] Input file is too small: '{inputFile}'. Skipping overlays.");
                    await FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                    return;
                }

                HashSet<string> audioLangsDetected = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> subtitleLangsDetected = new(StringComparer.OrdinalIgnoreCase);
                string? channelIconName = null;
                string? videoFormatIconName = null;
                string? resolutionIconName = null;

                if (item is Series && options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
                {
                    var (AudioLangs, SubtitleLangs, ChannelTypes, VideoFormats, Resolutions) = await GetAggregatedDataForParentAsync(item, options, cancellationToken);
                    audioLangsDetected = AudioLangs;
                    subtitleLangsDetected = SubtitleLangs;
                    channelIconName = ChannelTypes.FirstOrDefault();
                    videoFormatIconName = VideoFormats.FirstOrDefault();
                    resolutionIconName = Resolutions.FirstOrDefault();
                }
                else
                {
                    int maxChannels = 0;
                    MediaStream? videoStream = null;
                    IReadOnlyList<MediaStream>? mainItemStreams = item.GetMediaStreams();
                    if (mainItemStreams != null)
                    {
                        foreach (var stream in mainItemStreams)
                        {
                            if (stream.Type == MediaStreamType.Audio)
                            {
                                if (options.ShowAudioIcons && !string.IsNullOrEmpty(stream.Language)) audioLangsDetected.Add(LanguageHelper.NormalizeLangCode(stream.Language));
                                if (options.ShowAudioChannelIcons && stream.Channels.HasValue) maxChannels = Math.Max(maxChannels, stream.Channels.Value);
                            }
                            else if (options.ShowSubtitleIcons && stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                            {
                                subtitleLangsDetected.Add(LanguageHelper.NormalizeLangCode(stream.Language));
                            }
                            else if (stream.Type == MediaStreamType.Video)
                            {
                                videoStream = stream;
                            }
                        }
                    }
                    if (options.ShowAudioChannelIcons && maxChannels > 0) channelIconName = GetChannelIconName(maxChannels);
                    if (options.ShowVideoFormatIcons)
                    {
                        if (HasDolbyVision(item, mainItemStreams)) videoFormatIconName = "dv";
                        else if (HasHdr(item, mainItemStreams)) videoFormatIconName = "hdr";
                    }
                    if (options.ShowResolutionIcons && videoStream != null) resolutionIconName = GetResolutionIconName(videoStream.Width, videoStream.Height);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!options.ShowAudioIcons) audioLangsDetected.Clear();
                if (!options.ShowSubtitleIcons) subtitleLangsDetected.Clear();

                if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null && videoFormatIconName == null && resolutionIconName == null)
                {
                    await FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                    return;
                }

                using var surfBmp = SKBitmap.Decode(inputFile);
                if (surfBmp == null)
                {
                    _logger.Error($"[EmbyIcons] SKBitmap.Decode failed for '{inputFile}'. Copying original.");
                    await FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                    return;
                }

                await _iconCacheManager.InitializeAsync(options.IconsFolder!, cancellationToken);

                int width = surfBmp.Width, height = surfBmp.Height;
                int shortSide = Math.Min(width, height);
                int iconSize = Math.Clamp((shortSide * options.IconSize) / 100, 8, 512);
                int padding = Math.Clamp(iconSize / 4, 2, 128);

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                var canvas = surface.Canvas;
                canvas.DrawBitmap(surfBmp, 0, 0);

                var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };
                var overlays = new List<(IconAlignment Alignment, int Priority, List<SKImage> Icons, bool Horizontal)>();

                if (audioLangsDetected.Any())
                {
                    var audioIcons = audioLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon(lang, IconCacheManager.IconType.Audio)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (audioIcons.Any()) overlays.Add((options.AudioIconAlignment, 1, audioIcons, options.AudioOverlayHorizontal));
                }
                if (subtitleLangsDetected.Any())
                {
                    var subIcons = subtitleLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", IconCacheManager.IconType.Subtitle)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (subIcons.Any()) overlays.Add((options.SubtitleIconAlignment, 2, subIcons, options.SubtitleOverlayHorizontal));
                }
                if (resolutionIconName != null && _iconCacheManager.GetCachedIcon(resolutionIconName, IconCacheManager.IconType.Resolution) is { } resIcon)
                {
                    overlays.Add((options.ResolutionIconAlignment, 3, new List<SKImage> { resIcon }, options.ResolutionOverlayHorizontal));
                }
                if (videoFormatIconName != null && _iconCacheManager.GetCachedIcon(videoFormatIconName, IconCacheManager.IconType.VideoFormat) is { } vfIcon)
                {
                    overlays.Add((options.VideoFormatIconAlignment, 4, new List<SKImage> { vfIcon }, options.VideoFormatOverlayHorizontal));
                }
                if (channelIconName != null && _iconCacheManager.GetCachedIcon(channelIconName, IconCacheManager.IconType.Channel) is { } chIcon)
                {
                    overlays.Add((options.ChannelIconAlignment, 5, new List<SKImage> { chIcon }, options.ChannelOverlayHorizontal));
                }

                foreach (var alignmentGroup in overlays.GroupBy(x => x.Alignment))
                {
                    int cumulativeOffset = 0;
                    foreach (var (_, _, icons, horizontal) in alignmentGroup.OrderBy(x => x.Priority))
                    {
                        int totalHeight = horizontal ? iconSize : icons.Count * iconSize + (icons.Count - 1) * padding;
                        int actualVerticalOffset = alignmentGroup.Key is IconAlignment.TopLeft or IconAlignment.TopRight ? cumulativeOffset : -cumulativeOffset;
                        IconDrawer.DrawIcons(canvas, icons, iconSize, padding, width, height, alignmentGroup.Key, paint, actualVerticalOffset, horizontal);
                        cumulativeOffset += totalHeight + padding;
                    }
                }

                canvas.Flush();
                using var snapshot = surface.Snapshot();
                using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(options.JpegQuality, 10, 100));

                string tempOutput = outputFile + "." + Guid.NewGuid() + ".tmp";
                await using (var fsOut = File.Create(tempOutput))
                {
                    await encodedImg.AsStream().CopyToAsync(fsOut, cancellationToken);
                }
                File.Move(tempOutput, outputFile, true);
            }
            catch (OperationCanceledException)
            {
                _logger.Debug($"[EmbyIcons] Image enhancement task cancelled for item: {item?.Name} ({item?.Id}).");
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Critical error during enhancement for {item?.Name}. Copying original.", ex);
                try { await FileUtils.SafeCopyAsync(inputFile!, outputFile, CancellationToken.None); }
                catch (Exception fallbackEx) { _logger.ErrorException($"[EmbyIcons] CRITICAL: Fallback copy failed for {item?.Name}.", fallbackEx); }
            }
        }

        private string? GetChannelIconName(int channels) => channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => null };

        private string GetItemMediaStreamHash(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            streams ??= new List<MediaStream>();
            var audioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l);
            var subtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l);
            int maxChannels = streams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max();
            string combinedString = $"{string.Join(",", audioLangs)};{string.Join(",", subtitleLangs)};{GetChannelIconName(maxChannels) ?? "none"};{(HasDolbyVision(item, streams) ? "dv" : HasHdr(item, streams) ? "hdr" : "none")};{GetResolutionIconName(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Width, streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Height) ?? "none"}";
            if (string.IsNullOrEmpty(combinedString) || combinedString == ";;none;none;none") return "no_streams";
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(combinedString);
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 8);
        }

        private bool HasHdr(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            var videoStream = (streams ?? item.GetMediaStreams() ?? new List<MediaStream>()).FirstOrDefault(s => s?.Type == MediaStreamType.Video);
            if (videoStream == null) return false;
            return (videoStream.DisplayTitle ?? "").Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.CodecTag ?? "").Contains("hvc", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.Profile ?? "").Contains("Main 10", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.ColorPrimaries ?? "").Contains("bt2020", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.ColorTransfer ?? "").Contains("smpte2084", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasDolbyVision(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            var videoStream = (streams ?? item.GetMediaStreams() ?? new List<MediaStream>()).FirstOrDefault(s => s?.Type == MediaStreamType.Video);
            if (videoStream == null) return false;
            return (videoStream.DisplayTitle ?? "").Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.DisplayTitle ?? "").Contains("DV", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.CodecTag ?? "").Contains("dvh", StringComparison.OrdinalIgnoreCase) ||
                   (videoStream.Profile ?? "").Contains("dvhe", StringComparison.OrdinalIgnoreCase);
        }

        private string? GetResolutionIconName(int? width, int? height)
        {
            if (!width.HasValue || !height.HasValue) return null;
            if (height >= 1500) return "4k";
            if (height >= 1080) return "1080p";
            if (height >= 720) return "720p";
            if (height >= 576) return "576p";
            if (height >= 480) return "480p";
            return null;
        }
    }
}