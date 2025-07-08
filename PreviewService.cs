﻿﻿using EmbyIcons.Helpers;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace EmbyIcons
{
    [Unauthenticated]
    [Route("/EmbyIcons/Preview", "GET", Summary = "Generates a live preview image based on current settings")]
    public class GetIconPreview : IReturn<Stream>
    {
        public string? OptionsJson { get; set; }
    }

    public class PreviewService : IService
    {
        private readonly ILogger _logger;
        private enum OverlayType { SimpleIcons, CommunityRating }
        private record OverlayInfo(
            IconAlignment Alignment,
            int Priority,
            bool HorizontalLayout,
            OverlayType Type,
            List<SKImage>? Icons = null,
            float Score = 0
        );


        public PreviewService(ILogManager logManager)
        {
            _logger = logManager.GetLogger(GetType().Name);
        }

        public async Task<object> Get(GetIconPreview request)
        {
            if (string.IsNullOrEmpty(request.OptionsJson))
            {
                return new MemoryStream();
            }

            var serializerOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            var options = JsonSerializer.Deserialize<PluginOptions>(request.OptionsJson, serializerOptions)
                ?? throw new ArgumentException("Could not deserialize options from JSON.");

            using var originalBitmap = SKBitmap.Decode(Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbyIcons.Images.preview.png"));
            if (originalBitmap == null) throw new InvalidOperationException("Failed to decode the preview background image.");

            var iconCache = Plugin.Instance?.Enhancer._iconCacheManager;

            using var surface = SKSurface.Create(new SKImageInfo(originalBitmap.Width, originalBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(originalBitmap, 0, 0);

            if (iconCache != null)
            {
                await iconCache.InitializeAsync(options.IconsFolder, default);
                DrawPreviewOverlays(canvas, options, originalBitmap.Width, originalBitmap.Height, iconCache);
            }
            else
            {
                _logger.Warn("[EmbyIcons] Plugin instance not ready, cannot generate preview with overlays.");
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            var memoryStream = new MemoryStream();
            await data.AsStream().CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            return memoryStream;
        }

        private void DrawPreviewOverlays(SKCanvas canvas, PluginOptions options, int width, int height, IconCacheManager iconCache)
        {
            var iconSize = Math.Clamp((Math.Min(width, height) * options.IconSize) / 100, 8, 512);
            var edgePadding = Math.Clamp(iconSize / 4, 2, 64);
            var interIconPadding = Math.Clamp(iconSize / 8, 1, 64);
            using var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };

            var imdbIcon = options.ShowCommunityScoreIcon ? iconCache.GetCachedIcon("imdb", IconCacheManager.IconType.Audio) : null;
            var overlays = new List<OverlayInfo>();

            if (options.ShowAudioIcons && iconCache.GetFirstAvailableIcon(IconCacheManager.IconType.Audio) is { } audioIcon)
            {
                overlays.Add(new OverlayInfo(options.AudioIconAlignment, 1, options.AudioOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { audioIcon }));
            }
            if (options.ShowSubtitleIcons && iconCache.GetFirstAvailableIcon(IconCacheManager.IconType.Subtitle) is { } subIcon)
            {
                overlays.Add(new OverlayInfo(options.SubtitleIconAlignment, 2, options.SubtitleOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { subIcon }));
            }
            if (options.ShowResolutionIcons && iconCache.GetCachedIcon("4k", IconCacheManager.IconType.Resolution) is { } resIcon)
            {
                overlays.Add(new OverlayInfo(options.ResolutionIconAlignment, 3, options.ResolutionOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { resIcon }));
            }
            if (options.ShowVideoFormatIcons && (iconCache.GetCachedIcon("dv", IconCacheManager.IconType.VideoFormat) ?? iconCache.GetCachedIcon("hdr", IconCacheManager.IconType.VideoFormat)) is { } formatIcon)
            {
                overlays.Add(new OverlayInfo(options.VideoFormatIconAlignment, 4, options.VideoFormatOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { formatIcon }));
            }
            if (options.ShowVideoCodecIcons && (iconCache.GetCachedIcon("h265", IconCacheManager.IconType.VideoCodec) ?? iconCache.GetFirstAvailableIcon(IconCacheManager.IconType.VideoCodec)) is { } videoCodecIcon)
            {
                overlays.Add(new OverlayInfo(options.VideoCodecIconAlignment, 5, options.VideoCodecOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { videoCodecIcon }));
            }
            if (options.ShowTagIcons && iconCache.GetCachedIcon("tag", IconCacheManager.IconType.Tag) is { } tagIcon)
            {
                overlays.Add(new OverlayInfo(options.TagIconAlignment, 6, options.TagOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { tagIcon }));
            }
            if (options.ShowAudioChannelIcons && iconCache.GetCachedIcon("5.1", IconCacheManager.IconType.Channel) is { } channelIcon)
            {
                overlays.Add(new OverlayInfo(options.ChannelIconAlignment, 7, options.ChannelOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { channelIcon }));
            }
            if (options.ShowAudioCodecIcons && (iconCache.GetCachedIcon("dts", IconCacheManager.IconType.AudioCodec) ?? iconCache.GetFirstAvailableIcon(IconCacheManager.IconType.AudioCodec)) is { } codecIcon)
            {
                overlays.Add(new OverlayInfo(options.AudioCodecIconAlignment, 8, options.AudioCodecOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { codecIcon }));
            }
            if (options.ShowCommunityScoreIcon)
            {
                overlays.Add(new OverlayInfo(options.CommunityScoreIconAlignment, 9, options.CommunityScoreOverlayHorizontal, OverlayType.CommunityRating, Score: 6.9f));
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
    }
}