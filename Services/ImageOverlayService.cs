using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Model.Logging;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    internal class ImageOverlayService
    {
        private readonly ILogger _logger;
        private readonly IconCacheManager _iconCache;

        private record OverlayGroupInfo(IconAlignment Alignment, int Priority, bool HorizontalLayout, List<SKImage> Icons) : IOverlayInfo;
        private record RatingOverlayInfo(IconAlignment Alignment, int Priority, bool HorizontalLayout, float Score, SKImage? Icon) : IOverlayInfo;

        public ImageOverlayService(ILogger logger, IconCacheManager iconCache)
        {
            _logger = logger;
            _iconCache = iconCache;
        }

        public async Task<Stream> ApplyOverlaysAsync(SKBitmap sourceBitmap, OverlayData data, PluginOptions options, CancellationToken cancellationToken)
        {
            await _iconCache.InitializeAsync(options.IconsFolder, cancellationToken);

            using var surface = SKSurface.Create(new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(sourceBitmap, 0, 0);

            var iconSize = System.Math.Clamp((System.Math.Min(sourceBitmap.Width, sourceBitmap.Height) * options.IconSize) / 100, 8, 512);
            var edgePadding = System.Math.Clamp(iconSize / 4, 2, 64);
            var interIconPadding = System.Math.Clamp(iconSize / 8, 1, 64);
            var paint = options.EnableImageSmoothing ? EmbyIconsEnhancer.ResamplingPaint : EmbyIconsEnhancer.AliasedPaint;

            var iconGroups = await CreateIconGroups(data, options, cancellationToken);
            var ratingInfo = await CreateRatingInfo(data, options, cancellationToken);

            var overlaysByCorner = new Dictionary<IconAlignment, List<IOverlayInfo>>();
            foreach (var group in iconGroups)
            {
                if (!overlaysByCorner.ContainsKey(group.Alignment)) overlaysByCorner[group.Alignment] = new List<IOverlayInfo>();
                overlaysByCorner[group.Alignment].Add(group);
            }
            if (ratingInfo != null)
            {
                if (!overlaysByCorner.ContainsKey(ratingInfo.Alignment)) overlaysByCorner[ratingInfo.Alignment] = new List<IOverlayInfo>();
                overlaysByCorner[ratingInfo.Alignment].Add(ratingInfo);
            }

            foreach (var corner in overlaysByCorner.Keys)
            {
                DrawCorner(canvas, overlaysByCorner[corner], corner, iconSize, edgePadding, interIconPadding, sourceBitmap.Width, sourceBitmap.Height, paint, options);
            }

            using var image = surface.Snapshot();
            using var encodedData = image.Encode(SKEncodedImageFormat.Jpeg, System.Math.Clamp(options.JpegQuality, 10, 100));

            var memoryStream = new MemoryStream();
            await encodedData.AsStream().CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;
            return memoryStream;
        }

        private void DrawCorner(SKCanvas canvas, List<IOverlayInfo> overlays, IconAlignment alignment, int iconSize, int edgePadding, int interIconPadding, int width, int height, SKPaint paint, PluginOptions options)
        {
            var horizontalOverlays = overlays.Where(o => o.HorizontalLayout).OrderBy(o => o.Priority).ToList();
            var verticalOverlays = overlays.Where(o => !o.HorizontalLayout).OrderBy(o => o.Priority).ToList();

            int currentHorizontalOffset = 0;
            int currentVerticalOffset = 0;
            int maxHeightOfHorizontalRow = 0;

            foreach (var overlay in horizontalOverlays)
            {
                var consumedSize = DrawOverlay(canvas, overlay, alignment, iconSize, edgePadding, interIconPadding, width, height, paint, currentVerticalOffset, currentHorizontalOffset, options);
                currentHorizontalOffset += (int)consumedSize.Width + interIconPadding;
                maxHeightOfHorizontalRow = System.Math.Max(maxHeightOfHorizontalRow, (int)consumedSize.Height);
            }

            if (horizontalOverlays.Any())
            {
                currentVerticalOffset += maxHeightOfHorizontalRow + interIconPadding;
            }

            currentHorizontalOffset = 0;

            foreach (var overlay in verticalOverlays)
            {
                var consumedSize = DrawOverlay(canvas, overlay, alignment, iconSize, edgePadding, interIconPadding, width, height, paint, currentVerticalOffset, currentHorizontalOffset, options);
                currentVerticalOffset += (int)consumedSize.Height + interIconPadding;
            }
        }

        private SKSize DrawOverlay(SKCanvas canvas, IOverlayInfo overlay, IconAlignment alignment, int iconSize, int edgePadding, int interIconPadding, int width, int height, SKPaint paint, int verticalOffset, int horizontalOffset, PluginOptions options)
        {
            if (overlay is OverlayGroupInfo iconGroup)
            {
                return DrawIconGroup(canvas, iconGroup, iconSize, edgePadding, interIconPadding, width, height, paint, verticalOffset, horizontalOffset);
            }
            if (overlay is RatingOverlayInfo ratingInfo)
            {
                return DrawCommunityRatingOverlay(canvas, ratingInfo, iconSize, edgePadding, width, height, paint, verticalOffset, horizontalOffset, options);
            }
            return SKSize.Empty;
        }

        private SKSize DrawIconGroup(SKCanvas canvas, OverlayGroupInfo group, int size, int edgePadding, int interIconPadding, int width, int height, SKPaint paint, int verticalOffset, int horizontalOffset)
        {
            bool isRight = group.Alignment == IconAlignment.TopRight || group.Alignment == IconAlignment.BottomRight;
            bool isBottom = group.Alignment == IconAlignment.BottomLeft || group.Alignment == IconAlignment.BottomRight;

            int GetIconWidth(SKImage i) => i.Height > 0 ? (int)System.Math.Round(size * ((float)i.Width / i.Height)) : size;

            int totalWidth = group.HorizontalLayout ? group.Icons.Sum(GetIconWidth) + (group.Icons.Count - 1) * interIconPadding : group.Icons.Select(GetIconWidth).DefaultIfEmpty(0).Max();
            int totalHeight = group.HorizontalLayout ? size : (group.Icons.Count * size) + (group.Icons.Count - 1) * interIconPadding;

            float startX = isRight ? width - totalWidth - edgePadding - horizontalOffset : edgePadding + horizontalOffset;
            float startY = isBottom ? height - totalHeight - edgePadding - verticalOffset : edgePadding + verticalOffset;

            float currentX = startX;
            float currentY = startY;

            foreach (var img in group.Icons)
            {
                var iconWidth = GetIconWidth(img);
                var iconHeight = size;

                if (group.HorizontalLayout)
                {
                    canvas.DrawImage(img, new SKRect(currentX, startY, currentX + iconWidth, iconHeight + startY), paint);
                    currentX += iconWidth + interIconPadding;
                }
                else
                {
                    canvas.DrawImage(img, new SKRect(startX, currentY, startX + iconWidth, iconHeight + currentY), paint);
                    currentY += iconHeight + interIconPadding;
                }
            }

            return new SKSize(totalWidth, totalHeight);
        }

        private SKSize DrawCommunityRatingOverlay(SKCanvas canvas, RatingOverlayInfo rating, int iconSize, int padding, int canvasWidth, int canvasHeight, SKPaint basePaint, int verticalOffset, int horizontalOffset, PluginOptions options)
        {
            var scoreText = rating.Score.ToString("F1");
            var typeface = FontHelper.GetDefaultBold(_logger);
            if (typeface == null) return SKSize.Empty;

            using var textPaint = EmbyIconsEnhancer.TextPaint.Clone();
            using var textStrokePaint = EmbyIconsEnhancer.TextStrokePaint.Clone();

            float fontSize = iconSize * 0.75f;
            textPaint.TextSize = fontSize;
            textPaint.Typeface = typeface;
            textPaint.IsAntialias = options.EnableImageSmoothing;

            textStrokePaint.TextSize = fontSize;
            textStrokePaint.Typeface = typeface;
            textStrokePaint.IsAntialias = options.EnableImageSmoothing;
            textStrokePaint.StrokeWidth = Math.Max(1f, fontSize / 12f);

            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);
            int textWidth = (int)Math.Ceiling(textBounds.Width);
            int textHeight = (int)Math.Ceiling(textBounds.Height);

            int iconPadding = Math.Max(1, iconSize / 10);
            int iconDisplayWidth = (rating.Icon != null && rating.Icon.Height > 0) ? (int)Math.Round(iconSize * ((float)rating.Icon.Width / rating.Icon.Height)) : 0;

            int scoreAreaWidth;
            int scoreAreaHeight = iconSize;

            if (options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None)
            {
                float bgHorizontalPadding = iconSize * 0.2f;
                scoreAreaWidth = textWidth + (int)(bgHorizontalPadding * 2);
            }
            else
            {
                scoreAreaWidth = textWidth;
            }

            int totalWidth = iconDisplayWidth + (iconDisplayWidth > 0 ? iconPadding : 0) + scoreAreaWidth;
            int totalHeight = iconSize;

            bool isRight = rating.Alignment == IconAlignment.TopRight || rating.Alignment == IconAlignment.BottomRight;
            bool isBottom = rating.Alignment == IconAlignment.BottomLeft || rating.Alignment == IconAlignment.BottomRight;

            float startX = isRight ? canvasWidth - totalWidth - padding - horizontalOffset : padding + horizontalOffset;
            float startY = isBottom ? canvasHeight - totalHeight - padding - verticalOffset : padding + verticalOffset;
            float currentX = startX;

            if (rating.Icon != null && iconDisplayWidth > 0)
            {
                canvas.DrawImage(rating.Icon, SKRect.Create(currentX, startY, iconDisplayWidth, totalHeight), basePaint);
                currentX += iconDisplayWidth + iconPadding;
            }

            float scoreAreaStartX = currentX;

            if (options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None)
            {
                if (SKColor.TryParse(options.CommunityScoreBackgroundColor, out var baseBgColor))
                {
                    byte alpha = (byte)Math.Clamp(options.CommunityScoreBackgroundOpacity * 2.55, 0, 255);
                    SKColor blendedColor = new SKColor(baseBgColor.Red, baseBgColor.Green, baseBgColor.Blue, alpha);
                    using var bgPaint = new SKPaint { Color = blendedColor, IsAntialias = true, Style = SKPaintStyle.Fill };

                    var bgRect = SKRect.Create(scoreAreaStartX, startY, scoreAreaWidth, totalHeight);

                    if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.Square)
                    {
                        canvas.DrawRect(bgRect, bgPaint);
                    }
                    else if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.Circle)
                    {
                        canvas.DrawCircle(bgRect.MidX, bgRect.MidY, Math.Min(bgRect.Width, bgRect.Height) / 2f, bgPaint);
                    }
                }
            }

            float textX = scoreAreaStartX + (scoreAreaWidth - textWidth) / 2f;
            float textY = startY + (totalHeight - textHeight) / 2f - textBounds.Top;

            if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.None || options.CommunityScoreBackgroundOpacity < 50)
            {
                canvas.DrawText(scoreText, textX, textY, textStrokePaint);
            }

            canvas.DrawText(scoreText, textX, textY, textPaint);

            return new SKSize(totalWidth, totalHeight);
        }

        #region Helper methods to create overlay info
        private async Task<List<OverlayGroupInfo>> CreateIconGroups(OverlayData data, PluginOptions options, CancellationToken cancellationToken)
        {
            var groups = new List<OverlayGroupInfo>(8); // Pre-size list

            if (options.ShowAudioIcons) await AddGroup(groups, data.AudioLanguages, IconCacheManager.IconType.Audio, options.AudioIconAlignment, 1, options.AudioOverlayHorizontal, cancellationToken);
            if (options.ShowSubtitleIcons) await AddGroup(groups, data.SubtitleLanguages, IconCacheManager.IconType.Subtitle, options.SubtitleIconAlignment, 2, options.SubtitleOverlayHorizontal, cancellationToken, "srt.");
            if (options.ShowResolutionIcons && data.ResolutionIconName != null) await AddGroup(groups, new List<string> { data.ResolutionIconName }, IconCacheManager.IconType.Resolution, options.ResolutionIconAlignment, 3, options.ResolutionOverlayHorizontal, cancellationToken);
            if (options.ShowVideoFormatIcons && data.VideoFormatIconName != null) await AddGroup(groups, new List<string> { data.VideoFormatIconName }, IconCacheManager.IconType.VideoFormat, options.VideoFormatIconAlignment, 4, options.VideoFormatOverlayHorizontal, cancellationToken);
            if (options.ShowVideoCodecIcons) await AddGroup(groups, data.VideoCodecs, IconCacheManager.IconType.VideoCodec, options.VideoCodecIconAlignment, 5, options.VideoCodecOverlayHorizontal, cancellationToken);
            if (options.ShowTagIcons) await AddGroup(groups, data.Tags, IconCacheManager.IconType.Tag, options.TagIconAlignment, 6, options.TagOverlayHorizontal, cancellationToken);
            if (options.ShowAudioChannelIcons && data.ChannelIconName != null) await AddGroup(groups, new List<string> { data.ChannelIconName }, IconCacheManager.IconType.Channel, options.ChannelIconAlignment, 7, options.ChannelOverlayHorizontal, cancellationToken);
            if (options.ShowAudioCodecIcons) await AddGroup(groups, data.AudioCodecs, IconCacheManager.IconType.AudioCodec, options.AudioCodecIconAlignment, 8, options.AudioCodecOverlayHorizontal, cancellationToken);

            return groups;
        }

        private async Task AddGroup(List<OverlayGroupInfo> groups, ICollection<string> names, IconCacheManager.IconType type, IconAlignment alignment, int priority, bool horizontal, CancellationToken cancellationToken, string prefix = "")
        {
            if (names == null || !names.Any()) return;

            var iconTasks = names.OrderBy(n => n)
                                 .Select(name => _iconCache.GetCachedIconAsync(prefix + name, type, cancellationToken))
                                 .ToList();

            var loadedIcons = await Task.WhenAll(iconTasks);

            var icons = loadedIcons.Where(icon => icon != null).Select(icon => icon!).ToList();

            if (icons.Any())
            {
                groups.Add(new OverlayGroupInfo(alignment, priority, horizontal, icons));
            }
        }

        private async Task<RatingOverlayInfo?> CreateRatingInfo(OverlayData data, PluginOptions options, CancellationToken cancellationToken)
        {
            if (options.ShowCommunityScoreIcon && data.CommunityRating.HasValue && data.CommunityRating.Value > 0)
            {
                var imdbIcon = await _iconCache.GetCachedIconAsync("imdb", IconCacheManager.IconType.CommunityRating, cancellationToken);
                return new RatingOverlayInfo(options.CommunityScoreIconAlignment, 9, options.CommunityScoreOverlayHorizontal, data.CommunityRating.Value, imdbIcon);
            }
            return null;
        }
        #endregion
    }
}