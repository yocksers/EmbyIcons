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
            SKImage? ratingOverlayImage = null;
            var disposableImages = new List<SKImage>();

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
                string? audioChannelsIconName = null;

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
                        audioChannelsIconName = maxChannels.ToString();
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

                if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null && videoFormatIconName == null && resolutionIconName == null && audioChannelsIconName == null && !hasCommunityScore)
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

                if (displayRating.HasValue && displayRating.Value > 0)
                {
                    ratingOverlayImage = CreateCommunityRatingOverlay(displayRating.Value, iconSize, options.EnableImageSmoothing);
                    if (ratingOverlayImage != null)
                    {
                        disposableImages.Add(ratingOverlayImage);
                    }
                }

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                var canvas = surface.Canvas;
                canvas.DrawBitmap(surfBmp, 0, 0);

                var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };

                var overlays = new List<(IconAlignment Alignment, int Priority, List<SKImage> Icons, bool Horizontal, bool UseActualSize)>();

                if (audioLangsDetected.Any())
                {
                    var audioIcons = audioLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon(lang, IconCacheManager.IconType.Audio)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (audioIcons.Any()) overlays.Add((options.AudioIconAlignment, 1, audioIcons, options.AudioOverlayHorizontal, false));
                }
                if (subtitleLangsDetected.Any())
                {
                    var subIcons = subtitleLangsDetected.OrderBy(l => l).Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", IconCacheManager.IconType.Subtitle)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (subIcons.Any()) overlays.Add((options.SubtitleIconAlignment, 2, subIcons, options.SubtitleOverlayHorizontal, false));
                }
                if (resolutionIconName != null && _iconCacheManager.GetCachedIcon(resolutionIconName, IconCacheManager.IconType.Resolution) is { } resIcon)
                {
                    overlays.Add((options.ResolutionIconAlignment, 3, new List<SKImage> { resIcon }, options.ResolutionOverlayHorizontal, false));
                }
                if (videoFormatIconName != null && _iconCacheManager.GetCachedIcon(videoFormatIconName, IconCacheManager.IconType.VideoFormat) is { } vfIcon)
                {
                    overlays.Add((options.VideoFormatIconAlignment, 4, new List<SKImage> { vfIcon }, options.VideoFormatOverlayHorizontal, false));
                }
                if (channelIconName != null && _iconCacheManager.GetCachedIcon(channelIconName, IconCacheManager.IconType.Channel) is { } chIcon)
                {
                    overlays.Add((options.ChannelIconAlignment, 5, new List<SKImage> { chIcon }, options.ChannelOverlayHorizontal, false));
                }
                if (ratingOverlayImage != null)
                {
                    overlays.Add((options.CommunityScoreIconAlignment, 6, new List<SKImage> { ratingOverlayImage }, options.CommunityScoreOverlayHorizontal, true));
                }

                foreach (var alignmentGroup in overlays.GroupBy(x => x.Alignment))
                {
                    var horizontalGroups = alignmentGroup.Where(g => g.Horizontal).OrderBy(g => g.Priority).ToList();
                    var verticalGroups = alignmentGroup.Where(g => !g.Horizontal).OrderBy(g => g.Priority).ToList();

                    int currentHorizontalOffset = 0;
                    int currentVerticalOffset = 0;
                    int maxHeightOfHorizontalRow = 0;

                    foreach (var (alignment, _, icons, horizontal, useActualSize) in horizontalGroups)
                    {
                        if (!icons.Any()) continue;

                        IconDrawer.DrawIcons(canvas, icons, iconSize, padding, width, height, alignment, paint, currentVerticalOffset, true, currentHorizontalOffset, useActualSize);

                        int GetIconWidth(SKImage icon) => useActualSize ? icon.Width : iconSize;
                        int GetIconHeight(SKImage icon) => useActualSize ? icon.Height : iconSize;

                        int groupWidth = icons.Sum(GetIconWidth) + (icons.Count - 1) * padding;
                        currentHorizontalOffset += groupWidth + padding;

                        int groupHeight = icons.Select(GetIconHeight).DefaultIfEmpty(0).Max();
                        maxHeightOfHorizontalRow = Math.Max(maxHeightOfHorizontalRow, groupHeight);
                    }

                    if (horizontalGroups.Any())
                    {
                        currentVerticalOffset += maxHeightOfHorizontalRow + padding;
                    }

                    currentHorizontalOffset = 0;

                    foreach (var (alignment, _, icons, horizontal, useActualSize) in verticalGroups)
                    {
                        if (!icons.Any()) continue;

                        IconDrawer.DrawIcons(canvas, icons, iconSize, padding, width, height, alignment, paint, currentVerticalOffset, false, currentHorizontalOffset, useActualSize);

                        int GetIconHeight(SKImage icon) => useActualSize ? icon.Height : iconSize;

                        int groupHeight = icons.Sum(GetIconHeight) + (icons.Count - 1) * padding;
                        currentVerticalOffset += groupHeight + padding;
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
            finally
            {
                foreach (var img in disposableImages)
                {
                    img.Dispose();
                }
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
            return streams.Any(s => s.Type == MediaStreamType.Video &&
                (
                    (!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.IndexOf("dolby", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(s.DisplayTitle) && s.DisplayTitle.IndexOf("dolby", StringComparison.OrdinalIgnoreCase) >= 0)
                ));
        }

        private bool HasHdr10Plus(BaseItem item)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                var fileName = Path.GetFileName(item.Path);
                if (fileName != null && (fileName.IndexOf("HDR10+", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf("HDR10Plus", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }

            var streams = item.GetMediaStreams();
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video &&
                (!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.IndexOf("hdr10plus", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private bool HasHdr(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video &&
                (
                    (!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.IndexOf("hdr", StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(s.DisplayTitle) && s.DisplayTitle.IndexOf("hdr", StringComparison.OrdinalIgnoreCase) >= 0)
                ));
        }

        private SKImage? CreateCommunityRatingOverlay(float score, int iconSize, bool enableSmoothing)
        {
            var baseIcon = _iconCacheManager.GetCachedIcon("imdb", IconCacheManager.IconType.Audio);
            var scoreText = score.ToString("F1");
            var typeface = FontHelper.GetDefaultBold(_logger);

            if (typeface == null)
            {
                _logger.Error("[EmbyIcons] Could not load any typeface for drawing score. Aborting rating overlay.");
                return null;
            }

            using var textPaint = new SKPaint
            {
                IsAntialias = enableSmoothing,
                Color = SKColors.White,
                TextSize = iconSize * 0.65f,
                Typeface = typeface
            };

            using var textStrokePaint = new SKPaint
            {
                IsAntialias = enableSmoothing,
                Color = SKColors.Black,
                TextSize = textPaint.TextSize,
                Typeface = typeface,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = Math.Max(1f, textPaint.TextSize / 20f)
            };

            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);

            if (baseIcon != null)
            {
                textPaint.TextAlign = SKTextAlign.Left;
                textStrokePaint.TextAlign = SKTextAlign.Left;

                int padding = Math.Max(2, iconSize / 8);
                int finalIconHeight = iconSize;
                int finalIconWidth = (int)((float)finalIconHeight / baseIcon.Height * baseIcon.Width);

                int totalWidth = finalIconWidth + padding + (int)Math.Ceiling(textBounds.Width);
                int totalHeight = finalIconHeight;

                using var surface = SKSurface.Create(new SKImageInfo(totalWidth, totalHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (surface == null) return null;

                var canvas = surface.Canvas;
                using var paint = new SKPaint { FilterQuality = enableSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };
                canvas.DrawImage(baseIcon, new SKRect(0, 0, finalIconWidth, finalIconHeight), paint);

                float y = (totalHeight - textBounds.Height) / 2 - textBounds.Top;
                float x = finalIconWidth + padding;
                canvas.DrawText(scoreText, x, y, textStrokePaint);
                canvas.DrawText(scoreText, x, y, textPaint);

                return surface.Snapshot();
            }
            else
            {
                textPaint.TextAlign = SKTextAlign.Center;
                textStrokePaint.TextAlign = SKTextAlign.Center;

                int padding = (int)(textPaint.TextSize * 0.2f);
                int totalWidth = (int)Math.Ceiling(textBounds.Width) + padding;
                int totalHeight = (int)Math.Ceiling(textBounds.Height) + padding;
                if (totalWidth <= padding || totalHeight <= padding) return null;

                using var surface = SKSurface.Create(new SKImageInfo(totalWidth, totalHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (surface == null) return null;

                var canvas = surface.Canvas;
                float x = totalWidth / 2f;
                float y = totalHeight / 2f - textBounds.MidY;

                canvas.DrawText(scoreText, x, y, textStrokePaint);
                canvas.DrawText(scoreText, x, y, textPaint);

                return surface.Snapshot();
            }
        }

        private string GetItemMediaStreamHash(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            streams ??= new List<MediaStream>();
            var audioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l);
            var subtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l);

            int maxChannels = 0;
            foreach (var stream in streams)
            {
                if (stream.Type == MediaStreamType.Audio && stream.Channels.HasValue)
                {
                    if (stream.Channels.Value > maxChannels)
                    {
                        maxChannels = stream.Channels.Value;
                    }
                }
            }

            string videoFormat;
            if (HasDolbyVision(item, streams)) videoFormat = "dv";
            else if (HasHdr10Plus(item)) videoFormat = "hdr10plus";
            else if (HasHdr(item, streams)) videoFormat = "hdr";
            else videoFormat = "none";

            string? resolution = null;
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            if (videoStream != null)
            {
                resolution = GetResolutionIconName(videoStream.Width, videoStream.Height);
            }

            var sb = new StringBuilder();
            sb.Append(string.Join(",", audioLangs)).Append(';')
              .Append(string.Join(",", subtitleLangs)).Append(';')
              .Append(GetChannelIconName(maxChannels) ?? "none").Append(';')
              .Append(videoFormat).Append(';')
              .Append(resolution ?? "none");

            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 8);
        }
    }
}