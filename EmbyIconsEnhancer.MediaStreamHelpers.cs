﻿using EmbyIcons.Helpers;
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

        private string? GetAudioCodecIconName(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            codec = codec.ToLowerInvariant();

            if (codec.Contains("aac")) return "aac";
            if (codec.Contains("pcm")) return "pcm";
            if (codec.Contains("flac")) return "flac";
            if (codec.Contains("mp3")) return "mp3";
            if (codec.Contains("ac-3") || codec == "ac3") return "ac3";
            if (codec.Contains("e-ac-3") || codec == "eac3") return "eac3";
            if (codec.Contains("dts") || codec == "dca") return "dts";
            if (codec.Contains("truehd")) return "truehd";

            return null;
        }

        private string? GetVideoCodecIconName(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            codec = codec.ToLowerInvariant();

            if (codec.Contains("av1")) return "av1";
            if (codec.Contains("avc")) return "avc";
            if (codec.Contains("h264")) return "h264";
            if (codec.Contains("hevc") || codec.Contains("h265")) return "h265";
            if (codec.Contains("mpeg4")) return "mp4";
            if (codec.Contains("vc1")) return "vc1";
            if (codec.Contains("vp9")) return "vp9";
            if (codec.Contains("vvc") || codec.Contains("h266")) return "h266";

            return null;
        }

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
                HashSet<string> audioCodecsDetected = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> videoCodecsDetected = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> tagsDetected = new();
                string? channelIconName = null;
                string? videoFormatIconName = null;
                string? resolutionIconName = null;

                if (item is Series seriesItem && options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
                {
                    if (_seriesAggregationCache.TryGetValue(seriesItem.Id, out var aggResult))
                    {
                        audioLangsDetected = aggResult.AudioLangs;
                        subtitleLangsDetected = aggResult.SubtitleLangs;
                        audioCodecsDetected = aggResult.AudioCodecs;
                        videoCodecsDetected = aggResult.VideoCodecs;
                        channelIconName = aggResult.ChannelTypes.FirstOrDefault();
                        videoFormatIconName = aggResult.VideoFormats.FirstOrDefault();
                        resolutionIconName = aggResult.Resolutions.FirstOrDefault();
                    }
                    else
                    {
                        // This block should ideally never be hit. If it is, it means something is wrong
                        // with the caching logic or call order. A fallback is good, but log a warning.
                        _logger.Warn($"[EmbyIcons] Series aggregation cache miss for '{item.Name}' ({item.Id}) during enhancement. Recalculating.");
                        var (AudioLangs, SubtitleLangs, ChannelTypes, VideoFormats, Resolutions, AudioCodecs, VideoCodecs) = await GetAggregatedDataForParentAsync(item, options, cancellationToken);
                        audioLangsDetected = AudioLangs;
                        subtitleLangsDetected = SubtitleLangs;
                        audioCodecsDetected = AudioCodecs;
                        videoCodecsDetected = VideoCodecs;
                        channelIconName = ChannelTypes.FirstOrDefault();
                        videoFormatIconName = VideoFormats.FirstOrDefault();
                        resolutionIconName = Resolutions.FirstOrDefault();
                    }
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
                                if (options.ShowAudioCodecIcons && !string.IsNullOrEmpty(stream.Codec))
                                {
                                    var codecIconName = GetAudioCodecIconName(stream.Codec);
                                    if (codecIconName != null) audioCodecsDetected.Add(codecIconName);
                                }
                            }
                            else if (options.ShowSubtitleIcons && stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                            {
                                subtitleLangsDetected.Add(LanguageHelper.NormalizeLangCode(stream.Language));
                            }
                            else if (stream.Type == MediaStreamType.Video)
                            {
                                // Only use the first video stream for resolution checks.
                                // Subsequent streams might be extras or previews.
                                if (videoStream == null)
                                {
                                    videoStream = stream;
                                }
                                if (options.ShowVideoCodecIcons && !string.IsNullOrEmpty(stream.Codec))
                                {
                                    var codecIconName = GetVideoCodecIconName(stream.Codec);
                                    if (codecIconName != null) videoCodecsDetected.Add(codecIconName);
                                }
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
                        else if (HasHdr10Plus(item, mainItemStreams)) videoFormatIconName = "hdr10plus";
                        else if (HasHdr(item, mainItemStreams)) videoFormatIconName = "hdr";
                    }

                    if (options.ShowResolutionIcons && videoStream != null)
                    {
                        resolutionIconName = GetResolutionIconNameFromStream(videoStream);
                    }
                }

                if (options.ShowTagIcons && !string.IsNullOrEmpty(options.TagsToShow) && item.Tags != null && item.Tags.Length > 0)
                {
                    var configuredTags = new HashSet<string>(options.TagsToShow.Split(','), StringComparer.OrdinalIgnoreCase);
                    foreach (var itemTag in item.Tags)
                    {
                        if (configuredTags.Contains(itemTag))
                        {
                            tagsDetected.Add(itemTag);
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (!options.ShowAudioIcons) audioLangsDetected.Clear();
                if (!options.ShowSubtitleIcons) subtitleLangsDetected.Clear();
                if (!options.ShowAudioCodecIcons) audioCodecsDetected.Clear();
                if (!options.ShowVideoCodecIcons) videoCodecsDetected.Clear();
                if (!options.ShowTagIcons) tagsDetected.Clear();

                float? displayRating = null;
                if (options.ShowCommunityScoreIcon && communityRating.HasValue)
                {
                    displayRating = communityRating;
                }
                bool hasCommunityScore = displayRating.HasValue && displayRating.Value > 0;

                if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null && videoFormatIconName == null && resolutionIconName == null && !hasCommunityScore && audioCodecsDetected.Count == 0 && videoCodecsDetected.Count == 0 && tagsDetected.Count == 0)
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
                int edgePadding = Math.Clamp(iconSize / 4, 2, 128);
                int interIconPadding = Math.Clamp(iconSize / 8, 1, 128);

                using var surface = SKSurface.Create(new SKImageInfo(width, height));
                var canvas = surface.Canvas;
                canvas.DrawBitmap(surfBmp, 0, 0);

                using var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };

                var imdbIcon = hasCommunityScore ? _iconCacheManager.GetCachedIcon("imdb", IconCacheManager.IconType.Audio) : null;
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
                if (videoCodecsDetected.Any())
                {
                    var videoCodecIcons = videoCodecsDetected.OrderBy(c => c).Select(codec => _iconCacheManager.GetCachedIcon(codec, IconCacheManager.IconType.VideoCodec)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (videoCodecIcons.Any()) overlays.Add(new OverlayInfo(options.VideoCodecIconAlignment, 5, options.VideoCodecOverlayHorizontal, OverlayType.SimpleIcons, Icons: videoCodecIcons));
                }
                if (tagsDetected.Any())
                {
                    var tagIcons = tagsDetected.OrderBy(t => t).Select(tag => _iconCacheManager.GetCachedIcon(tag, IconCacheManager.IconType.Tag)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (tagIcons.Any()) overlays.Add(new OverlayInfo(options.TagIconAlignment, 6, options.TagOverlayHorizontal, OverlayType.SimpleIcons, Icons: tagIcons));
                }
                if (channelIconName != null && _iconCacheManager.GetCachedIcon(channelIconName, IconCacheManager.IconType.Channel) is { } chIcon)
                {
                    overlays.Add(new OverlayInfo(options.ChannelIconAlignment, 7, options.ChannelOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { chIcon }));
                }
                if (audioCodecsDetected.Any())
                {
                    var audioCodecIcons = audioCodecsDetected.OrderBy(c => c).Select(codec => _iconCacheManager.GetCachedIcon(codec, IconCacheManager.IconType.AudioCodec)).Where(i => i != null).Cast<SKImage>().ToList();
                    if (audioCodecIcons.Any()) overlays.Add(new OverlayInfo(options.AudioCodecIconAlignment, 8, options.AudioCodecOverlayHorizontal, OverlayType.SimpleIcons, Icons: audioCodecIcons));
                }
                if (hasCommunityScore)
                {
                    overlays.Add(new OverlayInfo(options.CommunityScoreIconAlignment, 9, options.CommunityScoreOverlayHorizontal, OverlayType.CommunityRating, Score: displayRating!.Value));
                }

                int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(iconSize * ((float)i.Width / i.Height)) : iconSize;

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
                            IconDrawer.DrawIcons(canvas, overlay.Icons, iconSize, interIconPadding, edgePadding, width, height, overlay.Alignment, paint, true, currentHorizontalOffset, currentVerticalOffset);
                            int consumedWidth = overlay.Icons.Sum(GetIconWidth) + (overlay.Icons.Count > 1 ? (overlay.Icons.Count - 1) * interIconPadding : 0);
                            consumedSize = new SKSize(consumedWidth, iconSize);
                        }
                        else
                        {
                            consumedSize = DrawCommunityRatingOverlay(canvas, overlay.Score, iconSize, edgePadding, width, height, options, paint, imdbIcon, overlay.Alignment, currentVerticalOffset, currentHorizontalOffset);
                        }

                        currentHorizontalOffset += (int)consumedSize.Width + interIconPadding;
                        maxHeightOfHorizontalRow = Math.Max(maxHeightOfHorizontalRow, (int)consumedSize.Height);
                    }

                    if (horizontalGroups.Any())
                    {
                        currentVerticalOffset += maxHeightOfHorizontalRow + interIconPadding;
                    }

                    currentHorizontalOffset = 0;

                    foreach (var overlay in verticalGroups)
                    {
                        SKSize consumedSize;
                        if (overlay.Type == OverlayType.SimpleIcons && overlay.Icons != null)
                        {
                            IconDrawer.DrawIcons(canvas, overlay.Icons, iconSize, interIconPadding, edgePadding, width, height, overlay.Alignment, paint, false, currentHorizontalOffset, currentVerticalOffset);
                            int maxIconWidth = overlay.Icons.Select(GetIconWidth).DefaultIfEmpty(0).Max();
                            int totalIconHeight = (overlay.Icons.Count * iconSize) + (overlay.Icons.Count > 1 ? (overlay.Icons.Count - 1) * interIconPadding : 0);
                            consumedSize = new SKSize(maxIconWidth, totalIconHeight);
                        }
                        else
                        {
                            consumedSize = DrawCommunityRatingOverlay(canvas, overlay.Score, iconSize, edgePadding, width, height, options, paint, imdbIcon, overlay.Alignment, currentVerticalOffset, currentHorizontalOffset);
                        }

                        currentVerticalOffset += (int)consumedSize.Height + interIconPadding;
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

        private string? GetResolutionFromDisplayTitle(string? displayTitle)
        {
            if (string.IsNullOrEmpty(displayTitle)) return null;

            string lowerTitle = displayTitle.ToLowerInvariant();
            if (lowerTitle.Contains("4k") || lowerTitle.Contains("2160p")) return "4k";
            if (lowerTitle.Contains("1080p")) return "1080p";
            if (lowerTitle.Contains("720p")) return "720p";
            if (lowerTitle.Contains("576p")) return "576p";
            if (lowerTitle.Contains("480p")) return "480p";

            return null;
        }

        internal string? GetResolutionIconNameFromStream(MediaStream? videoStream)
        {
            if (videoStream == null) return null;

            return GetResolutionFromDisplayTitle(videoStream.DisplayTitle);
        }

        private bool HasDolbyVision(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video && ((!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.Contains("dolby", StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(s.DisplayTitle) && s.DisplayTitle.Contains("dolby", StringComparison.OrdinalIgnoreCase))));
        }

        private bool HasHdr10Plus(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                var fileName = Path.GetFileName(item.Path);
                if (fileName != null && (fileName.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) || fileName.Contains("HDR10Plus", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.Contains("hdr10plus", StringComparison.OrdinalIgnoreCase));
        }

        private bool HasHdr(BaseItem item, IReadOnlyList<MediaStream>? streams)
        {
            if (streams == null) return false;
            return streams.Any(s => s.Type == MediaStreamType.Video && ((!string.IsNullOrEmpty(s.VideoRange) && s.VideoRange.Contains("hdr", StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(s.DisplayTitle) && s.DisplayTitle.Contains("hdr", StringComparison.OrdinalIgnoreCase))));
        }

        private SKSize DrawCommunityRatingOverlay(SKCanvas canvas, float score, int iconSize, int padding, int canvasWidth, int canvasHeight, PluginOptions options, SKPaint basePaint, SKImage? imdbIcon, IconAlignment alignment, int verticalOffset, int horizontalOffset)
        {
            var scoreText = score.ToString("F1");
            var typeface = FontHelper.GetDefaultBold(_logger);
            if (typeface == null) return SKSize.Empty;

            using var textPaint = new SKPaint { IsAntialias = options.EnableImageSmoothing, Color = SKColors.White, TextSize = iconSize * 0.65f, Typeface = typeface };
            using var textStrokePaint = new SKPaint { IsAntialias = options.EnableImageSmoothing, Color = SKColors.Black, TextSize = textPaint.TextSize, Typeface = typeface, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = Math.Max(1f, textPaint.TextSize / 20f) };

            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);

            int iconPadding = Math.Max(1, iconSize / 16);

            int iconDisplayWidth = 0;
            if (imdbIcon != null)
            {
                iconDisplayWidth = imdbIcon.Height > 0 ? (int)Math.Round(iconSize * ((float)imdbIcon.Width / imdbIcon.Height)) : iconSize;
            }

            int totalWidth = (imdbIcon != null) ? iconDisplayWidth + iconPadding + (int)Math.Ceiling(textBounds.Width) : (int)Math.Ceiling(textBounds.Width);
            int totalHeight = iconSize;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? canvasWidth - totalWidth - padding - horizontalOffset : padding + horizontalOffset;
            float startY = isBottom ? canvasHeight - totalHeight - padding - verticalOffset : padding + verticalOffset;

            float currentX = startX;

            if (imdbIcon != null)
            {
                var iconRect = new SKRect(currentX, startY, currentX + iconDisplayWidth, startY + iconSize);
                canvas.DrawImage(imdbIcon, iconRect, basePaint);
                currentX += iconDisplayWidth + iconPadding;
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

            var audioCodecs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Codec))
                .Select(s => GetAudioCodecIconName(s.Codec))
                .Where(c => c != null)
                .Select(c => c!)
                .Distinct()
                .OrderBy(c => c);
            sb.Append(string.Join(",", audioCodecs)).Append(';');

            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var videoCodec = videoStream != null ? GetVideoCodecIconName(videoStream.Codec) : null;
            sb.Append(videoCodec ?? "none").Append(';');

            int maxChannels = streams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max();
            sb.Append(GetChannelIconName(maxChannels) ?? "none").Append(';');
            if (HasDolbyVision(item, streams)) sb.Append("dv");
            else if (HasHdr10Plus(item, streams)) sb.Append("hdr10plus");
            else if (HasHdr(item, streams)) sb.Append("hdr");
            else sb.Append("none");
            sb.Append(';');

            sb.Append(videoStream != null ? (GetResolutionIconNameFromStream(videoStream) ?? "none") : "none");

            using var md5 = MD5.Create();
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 8);
        }
    }
}