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
            var paint = new SKPaint { FilterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None };

            var overlays = new List<(IconAlignment Alignment, int Priority, List<SKImage> Icons, bool Horizontal, bool UseActualSize)>();
            var disposableImages = new List<SKImage>();

            // 1. Audio Language
            if (options.ShowAudioIcons && iconCache.GetFirstAvailableIcon(IconCacheManager.IconType.Audio) is { } audioIcon)
            {
                var audioIcons = new List<SKImage> { audioIcon };
                overlays.Add((options.AudioIconAlignment, 1, audioIcons, options.AudioOverlayHorizontal, false));
            }
            // 2. Subtitle Language
            if (options.ShowSubtitleIcons && iconCache.GetFirstAvailableIcon(IconCacheManager.IconType.Subtitle) is { } subIcon)
            {
                var subIcons = new List<SKImage> { subIcon };
                overlays.Add((options.SubtitleIconAlignment, 2, subIcons, options.SubtitleOverlayHorizontal, false));
            }
            // 3. Resolution
            if (options.ShowResolutionIcons && iconCache.GetCachedIcon("4k", IconCacheManager.IconType.Resolution) is { } resIcon)
            {
                overlays.Add((options.ResolutionIconAlignment, 3, new List<SKImage> { resIcon }, options.ResolutionOverlayHorizontal, false));
            }
            // 4. Video Format
            if (options.ShowVideoFormatIcons && (iconCache.GetCachedIcon("dv", IconCacheManager.IconType.VideoFormat) ?? iconCache.GetCachedIcon("hdr", IconCacheManager.IconType.VideoFormat)) is { } formatIcon)
            {
                overlays.Add((options.VideoFormatIconAlignment, 4, new List<SKImage> { formatIcon }, options.VideoFormatOverlayHorizontal, false));
            }
            // 5. Audio Channel
            if (options.ShowAudioChannelIcons && iconCache.GetCachedIcon("5.1", IconCacheManager.IconType.Channel) is { } channelIcon)
            {
                overlays.Add((options.ChannelIconAlignment, 5, new List<SKImage> { channelIcon }, options.ChannelOverlayHorizontal, false));
            }
            // 6. Community Rating
            if (options.ShowCommunityScoreIcon)
            {
                var finalRatingIcon = CreateCommunityRatingOverlay(iconCache, 6.9f, iconSize, options.EnableImageSmoothing);
                if (finalRatingIcon != null)
                {
                    overlays.Add((options.CommunityScoreIconAlignment, 6, new List<SKImage> { finalRatingIcon }, options.CommunityScoreOverlayHorizontal, true));
                    disposableImages.Add(finalRatingIcon);
                }
            }

            foreach (var alignmentGroup in overlays.GroupBy(x => x.Alignment))
            {
                var horizontalGroups = alignmentGroup.Where(g => g.Horizontal).OrderBy(g => g.Priority).ToList();
                var verticalGroups = alignmentGroup.Where(g => !g.Horizontal).OrderBy(g => g.Priority).ToList();

                int currentHorizontalOffset = 0;
                int currentVerticalOffset = 0;
                int maxHeightOfHorizontalRow = 0;

                // 1. Draw all horizontal groups in a single row
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

                // 2. Draw all vertical groups in a single column, starting below the horizontal row
                if (horizontalGroups.Any())
                {
                    currentVerticalOffset += maxHeightOfHorizontalRow + padding;
                }

                currentHorizontalOffset = 0; // Reset horizontal offset for the vertical column

                foreach (var (alignment, _, icons, horizontal, useActualSize) in verticalGroups)
                {
                    if (!icons.Any()) continue;

                    IconDrawer.DrawIcons(canvas, icons, iconSize, padding, width, height, alignment, paint, currentVerticalOffset, false, currentHorizontalOffset, useActualSize);

                    int GetIconHeight(SKImage icon) => useActualSize ? icon.Height : iconSize;

                    int groupHeight = icons.Sum(GetIconHeight) + (icons.Count - 1) * padding;
                    currentVerticalOffset += groupHeight + padding;
                }
            }

            foreach (var imageToDispose in disposableImages)
            {
                imageToDispose.Dispose();
            }
        }

        private SKImage? CreateCommunityRatingOverlay(IconCacheManager iconCache, float score, int iconSize, bool enableSmoothing)
        {
            var baseIcon = iconCache.GetCachedIcon("imdb", IconCacheManager.IconType.Audio);
            var scoreText = score.ToString("F1");
            var typeface = FontHelper.GetDefaultBold(_logger);

            if (typeface == null)
            {
                _logger.Error("[EmbyIcons] Preview: Could not load any typeface for drawing score. Aborting rating overlay.");
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
    }
}
