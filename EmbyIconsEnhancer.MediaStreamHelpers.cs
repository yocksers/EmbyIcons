using EmbyIcons.Helpers;
using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
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
        private enum OverlayType { SimpleIcons, CommunityRating }
        private record OverlayInfo(
            IconAlignment Alignment,
            int Priority,
            bool HorizontalLayout,
            OverlayType Type,
            List<SKImage>? Icons = null,
            float Score = 0
        );

        internal Task EnhanceImageInternalAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, float? communityRating, CancellationToken cancellationToken)
        {
            try
            {
                var options = Plugin.Instance?.GetConfiguredOptions();
                if (options == null)
                {
                    throw new InvalidOperationException("Plugin options not initialized");
                }

                if (!IconDrawer.ShouldDrawAnyOverlays(item, options))
                {
                    return FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                }

                return EnhanceImageInternalWithOverlaysAsync(item, inputFile, outputFile, communityRating, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Unhandled error preparing for enhancement for {item?.Name}.", ex);
                return Task.CompletedTask;
            }
        }

        private async Task EnhanceImageInternalWithOverlaysAsync(BaseItem item, string inputFile, string outputFile, float? communityRating, CancellationToken cancellationToken)
        {
            try
            {
                if (item is Series series && series.Id == Guid.Empty && series.InternalId > 0)
                {
                    _logger.Debug($"[EmbyIcons] Enhancer: Detected lightweight Series object for {series.Name}. Fetching full item by InternalId {series.InternalId}.");
                    var fullSeriesItem = _libraryManager.GetItemById(series.InternalId);
                    if (fullSeriesItem != null)
                    {
                        item = fullSeriesItem;
                    }
                }

                var options = Plugin.Instance?.GetConfiguredOptions();
                if (options == null)
                {
                    throw new InvalidOperationException("Plugin options not initialized");
                }

                _logger.Debug($"[EmbyIcons] Enhancing image for {item.Name} ({item.Id})");

                if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
                {
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                    return;
                }

                if (new FileInfo(inputFile).Length < 100)
                {
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
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
                    var mainItemStreams = item.GetMediaStreams();
                    if (mainItemStreams != null)
                    {
                        foreach (var stream in mainItemStreams)
                        {
                            if (stream.Type == MediaStreamType.Audio)
                            {
                                if (options.ShowAudioIcons && !string.IsNullOrEmpty(stream.Language))
                                {
                                    audioLangsDetected.Add(LanguageHelper.NormalizeLangCode(stream.Language));
                                }
                                if (options.ShowAudioChannelIcons && stream.Channels.HasValue)
                                {
                                    maxChannels = Math.Max(maxChannels, stream.Channels.Value);
                                }
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
                    if (options.ShowAudioChannelIcons && maxChannels > 0)
                    {
                        channelIconName = GetChannelIconName(maxChannels);
                    }

                    if (options.ShowVideoFormatIcons)
                    {
                        if (HasDolbyVision(item, mainItemStreams)) videoFormatIconName = "dv";
                        else if (HasHdr10Plus(item)) videoFormatIconName = "hdr10plus";
                        else if (HasHdr(item, mainItemStreams)) videoFormatIconName = "hdr";
                    }

                    if (options.ShowResolutionIcons && videoStream != null)
                    {
                        resolutionIconName = GetResolutionIconName(videoStream.Width, videoStream.Height);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!options.ShowAudioIcons) audioLangsDetected.Clear();
                if (!options.ShowSubtitleIcons) subtitleLangsDetected.Clear();

                float? displayRating = null;
                if (options.ShowCommunityScoreIcon && communityRating.HasValue)
                {
                    displayRating = communityRating;
                }
                bool hasCommunityScore = displayRating.HasValue && displayRating.Value > 0;

                if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null && videoFormatIconName == null && resolutionIconName == null && !hasCommunityScore)
                {
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                    return;
                }

                using var surfBmp = SKBitmap.Decode(inputFile);
                if (surfBmp == null)
                {
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                    return;
                }

                await _iconCacheManager.InitializeAsync(options.IconsFolder, cancellationToken);

                int width = surfBmp.Width, height = surfBmp.Height;
                int shortSide = Math.Min(width, height);
                int iconSize = Math.Clamp((shortSide * options.IconSize) / 100, 8, 512);
                int padding = Math.Clamp(iconSize / 4, 2, 128);

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                var canvas = surface.Canvas;
                canvas.DrawBitmap(surfBmp, 0, 0);

                using var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };

                var overlays = new List<OverlayInfo>();

                if (audioLangsDetected.Any())
                {
                    var audioIcons = audioLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon(lang, IconCacheManager.IconType.Audio)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (audioIcons.Any()) overlays.Add(new OverlayInfo(options.AudioIconAlignment, 1, options.AudioOverlayHorizontal, OverlayType.SimpleIcons, Icons: audioIcons));
                }
                if (subtitleLangsDetected.Any())
                {
                    var subIcons = subtitleLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", IconCacheManager.IconType.Subtitle)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (subIcons.Any()) overlays.Add(new OverlayInfo(options.SubtitleIconAlignment, 2, options.SubtitleOverlayHorizontal, OverlayType.SimpleIcons, Icons: subIcons));
                }
                if (resolutionIconName != null && _iconCacheManager.GetCachedIcon(resolutionIconName, IconCacheManager.IconType.Resolution) is { } resIcon)
                {
                    overlays.Add(new OverlayInfo(options.ResolutionIconAlignment, 3, options.ResolutionOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { resIcon }));
                }
                if (videoFormatIconName != null && _iconCacheManager.GetCachedIcon(videoFormatIconName, IconCacheManager.IconType.VideoFormat) is { } vfIcon)
                {
                    overlays.Add(new OverlayInfo(options.VideoFormatIconAlignment, 4, options.VideoFormatOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { vfIcon }));
                }
                if (channelIconName != null && _iconCacheManager.GetCachedIcon(channelIconName, IconCacheManager.IconType.Channel) is { } chIcon)
                {
                    overlays.Add(new OverlayInfo(options.ChannelIconAlignment, 5, options.ChannelOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { chIcon }));
                }
                if (hasCommunityScore)
                {
                    overlays.Add(new OverlayInfo(options.CommunityScoreIconAlignment, 6, options.CommunityScoreOverlayHorizontal, OverlayType.CommunityRating, Score: displayRating!.Value));
                }

                foreach (var alignmentGroup in overlays.GroupBy(x => x.Alignment))
                {
                    var horizontalGroups = alignmentGroup.Where(g => g.HorizontalLayout).OrderBy(g => g.Priority).ToList();
                    var verticalGroups = alignmentGroup.Where(g => !g.HorizontalLayout).OrderBy(g => g.Priority).ToList();

                    int currentHorizontalOffset = 0;
                    int currentVerticalOffset = 0;
                    int maxHeightOfHorizontalRow = 0;

                    foreach (var overlay in horizontalGroups)
                    {
                        SKSize consumedSize;
                        if (overlay.Type == OverlayType.SimpleIcons && overlay.Icons != null)
                        {
                            IconDrawer.DrawIcons(canvas, overlay.Icons, iconSize, padding, width, height, overlay.Alignment, paint, currentVerticalOffset, true, currentHorizontalOffset);
                            consumedSize = new SKSize(overlay.Icons.Count * iconSize + (overlay.Icons.Count - 1) * padding, iconSize);
                        }
                        else
                        {
                            consumedSize = DrawCommunityRatingOverlay(canvas, overlay.Score, iconSize, padding, width, height, options, paint, overlay.Alignment, currentVerticalOffset, currentHorizontalOffset);
                        }

                        currentHorizontalOffset += (int)consumedSize.Width + padding;
                        maxHeightOfHorizontalRow = Math.Max(maxHeightOfHorizontalRow, (int)consumedSize.Height);
                    }

                    if (horizontalGroups.Any())
                    {
                        currentVerticalOffset += maxHeightOfHorizontalRow + padding;
                    }

                    currentHorizontalOffset = 0;

                    foreach (var overlay in verticalGroups)
                    {
                        SKSize consumedSize;
                        if (overlay.Type == OverlayType.SimpleIcons && overlay.Icons != null)
                        {
                            IconDrawer.DrawIcons(canvas, overlay.Icons, iconSize, padding, width, height, overlay.Alignment, paint, currentVerticalOffset, false, currentHorizontalOffset);
                            consumedSize = new SKSize(iconSize, overlay.Icons.Count * iconSize + (overlay.Icons.Count - 1) * padding);
                        }
                        else
                        {
                            consumedSize = DrawCommunityRatingOverlay(canvas, overlay.Score, iconSize, padding, width, height, options, paint, overlay.Alignment, currentVerticalOffset, currentHorizontalOffset);
                        }

                        currentVerticalOffset += (int)consumedSize.Height + padding;
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

        private string? GetResolutionIconName(int? width, int? height)
        {
            if (width == null || height == null) return null;
            if (width >= 3200 || height >= 1500) return "4k";
            if (width >= 1800 || height >= 780) return "1080p";
            if (width >= 1200 || height >= 650) return "720p";
            if (height >= 540) return "576p";
            if (height >= 400) return "480p";
            return null;
        }

        private bool HasDolbyVision(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video && ((!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.Contains("dolby", StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(s.DisplayTitle) && s.DisplayTitle.Contains("dolby", StringComparison.OrdinalIgnoreCase))));
        }

        private bool HasHdr10Plus(BaseItem item)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                var fileName = Path.GetFileName(item.Path);
                if (fileName != null && (fileName.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) || fileName.Contains("HDR10Plus", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            var streams = item.GetMediaStreams();
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.Contains("hdr10plus", StringComparison.OrdinalIgnoreCase));
        }

        private bool HasHdr(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video && ((!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.Contains("hdr", StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(s.DisplayTitle) && s.DisplayTitle.Contains("hdr", StringComparison.OrdinalIgnoreCase))));
        }

        private SKSize DrawCommunityRatingOverlay(SKCanvas canvas, float score, int iconSize, int padding, int canvasWidth, int canvasHeight, PluginOptions options, SKPaint basePaint, IconAlignment alignment, int verticalOffset, int horizontalOffset)
        {
            var imdbIcon = _iconCacheManager.GetCachedIcon("imdb", IconCacheManager.IconType.Audio);
            var scoreText = score.ToString("F1");
            var typeface = FontHelper.GetDefaultBold(_logger);
            if (typeface == null) return SKSize.Empty;

            using var textPaint = new SKPaint { IsAntialias = options.EnableImageSmoothing, Color = SKColors.White, TextSize = iconSize * 0.65f, Typeface = typeface };
            using var textStrokePaint = new SKPaint { IsAntialias = options.EnableImageSmoothing, Color = SKColors.Black, TextSize = textPaint.TextSize, Typeface = typeface, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = Math.Max(1f, textPaint.TextSize / 20f) };

            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);

            int iconPadding = Math.Max(2, iconSize / 8);
            int totalWidth = (imdbIcon != null) ? iconSize + iconPadding + (int)Math.Ceiling(textBounds.Width) : (int)Math.Ceiling(textBounds.Width);
            int totalHeight = iconSize;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? canvasWidth - totalWidth - padding - horizontalOffset : padding + horizontalOffset;
            float startY = isBottom ? canvasHeight - totalHeight - padding - verticalOffset : padding + verticalOffset;

            float currentX = startX;

            if (imdbIcon != null)
            {
                var iconRect = new SKRect(currentX, startY, currentX + iconSize, startY + iconSize);
                canvas.DrawImage(imdbIcon, iconRect, basePaint);
                currentX += iconSize + iconPadding;
            }

            float textY = startY + (totalHeight - textBounds.Height) / 2 - textBounds.Top;

            canvas.DrawText(scoreText, currentX, textY, textStrokePaint);
            canvas.DrawText(scoreText, currentX, textY, textPaint);

            return new SKSize(totalWidth, totalHeight);
        }

        private string GetItemMediaStreamHash(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            streams ??= new List<MediaStream>();
            var sb = new StringBuilder();
            sb.Append(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l))).Append(';');
            sb.Append(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l))).Append(';');
            int maxChannels = streams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max();
            sb.Append(GetChannelIconName(maxChannels) ?? "none").Append(';');
            if (HasDolbyVision(item, streams)) sb.Append("dv");
            else if (HasHdr10Plus(item)) sb.Append("hdr10plus");
            else if (HasHdr(item, streams)) sb.Append("hdr");
            else sb.Append("none");
            sb.Append(';');
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            sb.Append(videoStream != null ? (GetResolutionIconName(videoStream.Width, videoStream.Height) ?? "none") : "none");

            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 8);
        }
    }
}