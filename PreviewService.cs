using EmbyIcons.Helpers;
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

            using var iconCache = new IconCacheManager(TimeSpan.FromMinutes(1), _logger);
            await iconCache.InitializeAsync(options.IconsFolder, default);

            using var surface = SKSurface.Create(new SKImageInfo(originalBitmap.Width, originalBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(originalBitmap, 0, 0);

            DrawPreviewOverlays(canvas, options, originalBitmap.Width, originalBitmap.Height, iconCache);

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
            var padding = Math.Clamp(iconSize / 4, 2, 64);
            using var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };

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
            if (options.ShowAudioChannelIcons && iconCache.GetCachedIcon("5.1", IconCacheManager.IconType.Channel) is { } channelIcon)
            {
                overlays.Add(new OverlayInfo(options.ChannelIconAlignment, 5, options.ChannelOverlayHorizontal, OverlayType.SimpleIcons, Icons: new List<SKImage> { channelIcon }));
            }
            if (options.ShowCommunityScoreIcon)
            {
                overlays.Add(new OverlayInfo(options.CommunityScoreIconAlignment, 6, options.CommunityScoreOverlayHorizontal, OverlayType.CommunityRating, Score: 6.9f));
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
                        consumedSize = DrawCommunityRatingOverlay(canvas, overlay.Score, iconSize, padding, width, height, options, paint, iconCache, overlay.Alignment, currentVerticalOffset, currentHorizontalOffset);
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
                        consumedSize = DrawCommunityRatingOverlay(canvas, overlay.Score, iconSize, padding, width, height, options, paint, iconCache, overlay.Alignment, currentVerticalOffset, currentHorizontalOffset);
                    }

                    currentVerticalOffset += (int)consumedSize.Height + padding;
                }
            }
        }

        private SKSize DrawCommunityRatingOverlay(SKCanvas canvas, float score, int iconSize, int padding, int canvasWidth, int canvasHeight, PluginOptions options, SKPaint basePaint, IconCacheManager iconCache, IconAlignment alignment, int verticalOffset, int horizontalOffset)
        {
            var imdbIcon = iconCache.GetCachedIcon("imdb", IconCacheManager.IconType.Audio);
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
    }
}