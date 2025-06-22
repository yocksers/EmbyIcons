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
using static System.Net.Mime.MediaTypeNames;

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

                _logger.Debug($"[EmbyIcons] Enhancing image for {item.Name} ({item.Id})");

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

                _logger.Debug($"[EmbyIcons] Options Check - ShowCommunityScoreIcon: {options.ShowCommunityScoreIcon}");
                _logger.Debug($"[EmbyIcons] Item Check - CommunityRating: {item.CommunityRating?.ToString() ?? "null"}");
                bool hasCommunityScore = options.ShowCommunityScoreIcon && item.CommunityRating.HasValue && item.CommunityRating.Value > 0;
                _logger.Debug($"[EmbyIcons] hasCommunityScore evaluation result: {hasCommunityScore}");


                if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null && videoFormatIconName == null && resolutionIconName == null && !hasCommunityScore)
                {
                    _logger.Debug($"[EmbyIcons] No overlays to draw for {item.Name}. Copying original image.");
                    await FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                    return;
                }

                _logger.Debug($"[EmbyIcons] At least one overlay will be drawn for {item.Name}.");

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

                using SKImage? ratingOverlayImage = hasCommunityScore && item.CommunityRating.HasValue
                    ? CreateCommunityRatingOverlay(item.CommunityRating.Value, iconSize, options.EnableImageSmoothing)
                    : null;

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
                    _logger.Debug($"[EmbyIcons] Community Rating overlay created successfully for {item.Name}.");
                    overlays.Add((options.CommunityScoreIconAlignment, 6, new List<SKImage> { ratingOverlayImage }, options.CommunityScoreOverlayHorizontal, true));
                }
                else if (hasCommunityScore)
                {
                    _logger.Warn($"[EmbyIcons] Community score was present for {item.Name}, but overlay creation failed.");
                }


                foreach (var alignmentGroup in overlays.GroupBy(x => x.Alignment))
                {
                    int cumulativeHorizontalOffset = 0;
                    int cumulativeVerticalOffset = 0;
                    foreach (var (alignment, _, icons, horizontal, useActualSize) in alignmentGroup.OrderBy(x => x.Priority))
                    {
                        if (!icons.Any()) continue;

                        int GetIconWidth(SKImage icon) => useActualSize ? icon.Width : iconSize;
                        int GetIconHeight(SKImage icon) => useActualSize ? icon.Height : iconSize;

                        if (horizontal)
                        {
                            IconDrawer.DrawIcons(canvas, icons, iconSize, padding, width, height, alignment, paint, 0, true, cumulativeHorizontalOffset, useActualSize);
                            int groupWidth = icons.Sum(GetIconWidth) + (icons.Count - 1) * padding;
                            cumulativeHorizontalOffset += groupWidth + padding;
                        }
                        else // vertical
                        {
                            IconDrawer.DrawIcons(canvas, icons, iconSize, padding, width, height, alignment, paint, cumulativeVerticalOffset, false, 0, useActualSize);
                            int groupHeight = icons.Sum(GetIconHeight) + (icons.Count - 1) * padding;
                            cumulativeVerticalOffset += groupHeight + padding;
                        }
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

        private SKImage? CreateCommunityRatingOverlay(float score, int iconSize, bool enableSmoothing)
        {
            var starIcon = GetStarIcon();
            if (starIcon == null)
            {
                _logger.Warn("[EmbyIcons] Cannot create rating overlay because star.png icon failed to load.");
                return null;
            }

            var scoreText = score.ToString("F1");
            var typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
            if (typeface == null)
            {
                _logger.Warn("[EmbyIcons] 'sans-serif' bold font not found, falling back to default.");
                typeface = SKTypeface.Default ?? SKTypeface.CreateDefault();
            }

            if (typeface == null)
            {
                _logger.Error("[EmbyIcons] Could not load any typeface for drawing score. Aborting rating overlay.");
                return null;
            }

            using var textPaint = new SKPaint
            {
                IsAntialias = enableSmoothing,
                Color = SKColors.White,
                TextSize = iconSize * 0.8f,
                Typeface = typeface,
                TextAlign = SKTextAlign.Left
            };

            using var textStrokePaint = new SKPaint
            {
                IsAntialias = enableSmoothing,
                Color = SKColors.Black,
                TextSize = textPaint.TextSize,
                Typeface = typeface,
                Style = SKPaintStyle.StrokeAndFill,
                StrokeWidth = Math.Max(1f, iconSize / 16f),
                TextAlign = SKTextAlign.Left
            };

            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);

            int padding = Math.Max(2, iconSize / 8);
            int totalWidth = iconSize + padding + (int)Math.Ceiling(textBounds.Width);
            int totalHeight = iconSize;

            var imageInfo = new SKImageInfo(totalWidth, totalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(imageInfo);
            if (surface == null) return null;

            var canvas = surface.Canvas;
            using var paint = new SKPaint { FilterQuality = enableSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };
            canvas.DrawImage(starIcon, new SKRect(0, 0, iconSize, iconSize), paint);

            float y = (totalHeight - textBounds.Height) / 2 - textBounds.Top;
            float x = iconSize + padding;

            canvas.DrawText(scoreText, x, y, textStrokePaint);
            canvas.DrawText(scoreText, x, y, textPaint);

            return surface.Snapshot();
        }

        private string? GetChannelIconName(int channels) => channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => null };

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

            var extType = videoStream.AsVideoStream()?.ExtendedVideoType;
            if (!extType.HasValue) return false;

            return extType.Value == (ExtendedVideoTypes)100 || extType.Value == (ExtendedVideoTypes)110;
        }

        private bool HasDolbyVision(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            var videoStream = (streams ?? item.GetMediaStreams() ?? new List<MediaStream>()).FirstOrDefault(s => s?.Type == MediaStreamType.Video);
            if (videoStream == null) return false;

            var extType = videoStream.AsVideoStream()?.ExtendedVideoType;
            if (!extType.HasValue) return false;

            return extType.Value == (ExtendedVideoTypes)130;
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