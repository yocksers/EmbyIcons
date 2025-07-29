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

        public Task<Stream> ApplyOverlaysAsync(SKBitmap sourceBitmap, OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken)
        {
            return ApplyOverlaysAsync(sourceBitmap, data, profileOptions, globalOptions, cancellationToken, null);
        }

        public async Task<Stream> ApplyOverlaysAsync(SKBitmap sourceBitmap, OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken, Dictionary<IconCacheManager.IconType, List<SKImage>>? injectedIcons)
        {
            await _iconCache.InitializeAsync(globalOptions.IconsFolder, cancellationToken);

            using var surface = SKSurface.Create(new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(sourceBitmap, 0, 0);

            var iconSize = Math.Clamp((Math.Min(sourceBitmap.Width, sourceBitmap.Height) * profileOptions.IconSize) / 100, 8, 512);
            var edgePadding = Math.Clamp(iconSize / 4, 2, 64);
            var interIconPadding = Math.Clamp(iconSize / 8, 1, 64);
            var paint = profileOptions.EnableImageSmoothing ? EmbyIconsEnhancer.ResamplingPaint : EmbyIconsEnhancer.AliasedPaint;

            var iconGroups = await CreateIconGroups(data, profileOptions, globalOptions, cancellationToken, injectedIcons);
            var ratingInfo = await CreateRatingInfo(data, profileOptions, globalOptions, cancellationToken);

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
                DrawCorner(canvas, overlaysByCorner[corner], corner, iconSize, edgePadding, interIconPadding, sourceBitmap.Width, sourceBitmap.Height, paint, profileOptions);
            }

            using var image = surface.Snapshot();
            using var encodedData = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(profileOptions.JpegQuality, 10, 100));

            var memoryStream = new MemoryStream();
            await encodedData.AsStream().CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;
            return memoryStream;
        }

        private void DrawCorner(SKCanvas canvas, List<IOverlayInfo> overlays, IconAlignment alignment, int iconSize, int edgePadding, int interIconPadding, int width, int height, SKPaint paint, ProfileSettings options)
        {
            var allOverlays = overlays.OrderBy(o => o.Priority).ToList();

            var horizontalDrawableItems = new List<object>();
            var verticalDrawableItems = new List<IOverlayInfo>();

            foreach (var overlay in allOverlays)
            {
                if (overlay.HorizontalLayout)
                {
                    if (overlay is OverlayGroupInfo group)
                    {
                        horizontalDrawableItems.AddRange(group.Icons);
                    }
                    else if (overlay is RatingOverlayInfo rating)
                    {
                        horizontalDrawableItems.Add(rating);
                    }
                }
                else
                {
                    verticalDrawableItems.Add(overlay);
                }
            }

            float currentHorizontalOffset = 0;
            float currentVerticalOffset = 0;
            float maxHeightOfCurrentRow = 0;
            int rowCount = 1;
            const int maxRows = 2;
            var rowWidthLimit = width - (edgePadding * 2);

            foreach (var item in horizontalDrawableItems)
            {
                SKSize itemSize;
                if (item is SKImage icon)
                {
                    int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(iconSize * ((float)i.Width / i.Height)) : iconSize;
                    itemSize = new SKSize(GetIconWidth(icon), iconSize);
                }
                else if (item is RatingOverlayInfo rating)
                {
                    itemSize = GetCommunityRatingOverlaySize(rating, iconSize, options);
                }
                else
                {
                    continue;
                }

                if (currentHorizontalOffset > 0 && currentHorizontalOffset + itemSize.Width > rowWidthLimit)
                {
                    rowCount++;
                    if (rowCount > maxRows) break;

                    currentVerticalOffset += maxHeightOfCurrentRow + interIconPadding;
                    currentHorizontalOffset = 0;
                    maxHeightOfCurrentRow = 0;
                }

                if (item is SKImage iconToDraw)
                {
                    DrawSingleIcon(canvas, iconToDraw, alignment, iconSize, edgePadding, width, height, paint, (int)currentVerticalOffset, (int)currentHorizontalOffset);
                }
                else if (item is RatingOverlayInfo ratingToDraw)
                {
                    DrawCommunityRatingOverlay(canvas, ratingToDraw, alignment, iconSize, edgePadding, width, height, paint, (int)currentVerticalOffset, (int)currentHorizontalOffset, options);
                }

                currentHorizontalOffset += itemSize.Width + interIconPadding;
                maxHeightOfCurrentRow = Math.Max(maxHeightOfCurrentRow, itemSize.Height);
            }

            if (maxHeightOfCurrentRow > 0)
            {
                currentVerticalOffset += maxHeightOfCurrentRow + interIconPadding;
            }

            // Layout and draw vertical items (these don't wrap, they just stack).
            currentHorizontalOffset = 0;
            foreach (var overlay in verticalDrawableItems)
            {
                var consumedSize = DrawOverlay(canvas, overlay, alignment, iconSize, edgePadding, interIconPadding, width, height, paint, (int)currentVerticalOffset, (int)currentHorizontalOffset, options);
                currentVerticalOffset += consumedSize.Height + interIconPadding;
            }
        }

        private SKSize DrawOverlay(SKCanvas canvas, IOverlayInfo overlay, IconAlignment alignment, int iconSize, int edgePadding, int interIconPadding, int width, int height, SKPaint paint, int verticalOffset, int horizontalOffset, ProfileSettings options)
        {
            if (overlay is OverlayGroupInfo iconGroup)
            {
                return DrawIconGroup(canvas, iconGroup, iconSize, edgePadding, interIconPadding, width, height, paint, verticalOffset, horizontalOffset);
            }
            if (overlay is RatingOverlayInfo ratingInfo)
            {
                return DrawCommunityRatingOverlay(canvas, ratingInfo, alignment, iconSize, edgePadding, width, height, paint, verticalOffset, horizontalOffset, options);
            }
            return SKSize.Empty;
        }

        private SKSize GetCommunityRatingOverlaySize(RatingOverlayInfo rating, int iconSize, ProfileSettings options)
        {
            var typeface = FontHelper.GetDefaultBold(_logger);
            if (typeface == null) return SKSize.Empty;

            var scoreText = rating.Score.ToString("F1");
            var fontSize = iconSize * 0.75f;

            using var textPaint = new SKPaint { Typeface = typeface, TextSize = fontSize };
            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);

            int textWidth = (int)Math.Ceiling(textBounds.Width);
            int iconPadding = Math.Max(1, iconSize / 10);
            int iconDisplayWidth = (rating.Icon != null && rating.Icon.Height > 0) ? (int)Math.Round(iconSize * ((float)rating.Icon.Width / rating.Icon.Height)) : 0;

            float bgHorizontalPadding = iconSize * 0.2f;
            int scoreAreaWidth = (options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None) ? textWidth + (int)(bgHorizontalPadding * 2) : textWidth;

            int totalWidth = iconDisplayWidth + (iconDisplayWidth > 0 ? iconPadding : 0) + scoreAreaWidth;
            int totalHeight = iconSize;

            return new SKSize(totalWidth, totalHeight);
        }

        private void DrawSingleIcon(SKCanvas canvas, SKImage icon, IconAlignment alignment, int size, int edgePadding, int canvasWidth, int canvasHeight, SKPaint paint, int verticalOffset, int horizontalOffset)
        {
            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(size * ((float)i.Width / i.Height)) : size;
            var iconWidth = GetIconWidth(icon);
            var iconHeight = size;

            float x = isRight ? canvasWidth - edgePadding - horizontalOffset - iconWidth : edgePadding + horizontalOffset;
            float y = isBottom ? canvasHeight - edgePadding - verticalOffset - iconHeight : edgePadding + verticalOffset;

            canvas.DrawImage(icon, SKRect.Create(x, y, iconWidth, iconHeight), paint);
        }

        private SKSize DrawIconGroup(SKCanvas canvas, OverlayGroupInfo group, int size, int edgePadding, int interIconPadding, int width, int height, SKPaint paint, int verticalOffset, int horizontalOffset)
        {
            bool isRight = group.Alignment == IconAlignment.TopRight || group.Alignment == IconAlignment.BottomRight;
            bool isBottom = group.Alignment == IconAlignment.BottomLeft || group.Alignment == IconAlignment.BottomRight;

            int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(size * ((float)i.Width / i.Height)) : size;

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

        private SKSize DrawCommunityRatingOverlay(SKCanvas canvas, RatingOverlayInfo rating, IconAlignment alignment, int iconSize, int padding, int canvasWidth, int canvasHeight, SKPaint basePaint, int verticalOffset, int horizontalOffset, ProfileSettings options)
        {
            var totalSize = GetCommunityRatingOverlaySize(rating, iconSize, options);
            if (totalSize.IsEmpty) return SKSize.Empty;

            var typeface = FontHelper.GetDefaultBold(_logger);
            var scoreText = rating.Score.ToString("F1");
            var fontSize = iconSize * 0.75f;

            SKRect textBounds = new();
            var textPaint = options.EnableImageSmoothing ? EmbyIconsEnhancer.TextPaint : EmbyIconsEnhancer.AliasedTextPaint;
            textPaint.Typeface = typeface;
            textPaint.TextSize = fontSize;
            textPaint.MeasureText(scoreText, ref textBounds);

            int textWidth = (int)Math.Ceiling(textBounds.Width);
            int iconPadding = Math.Max(1, iconSize / 10);
            int iconDisplayWidth = (rating.Icon != null && rating.Icon.Height > 0) ? (int)Math.Round(iconSize * ((float)rating.Icon.Width / rating.Icon.Height)) : 0;
            float bgHorizontalPadding = iconSize * 0.2f;
            int scoreAreaWidth = (options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None) ? textWidth + (int)(bgHorizontalPadding * 2) : textWidth;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? canvasWidth - padding - horizontalOffset - totalSize.Width : padding + horizontalOffset;
            float startY = isBottom ? canvasHeight - padding - verticalOffset - totalSize.Height : padding + verticalOffset;
            float currentX = startX;

            if (rating.Icon != null && iconDisplayWidth > 0)
            {
                canvas.DrawImage(rating.Icon, SKRect.Create(currentX, startY, iconDisplayWidth, totalSize.Height), basePaint);
                currentX += iconDisplayWidth + iconPadding;
            }

            var bgRect = SKRect.Create(currentX, startY, scoreAreaWidth, totalSize.Height);

            if (options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None)
            {
                DrawRatingBackground(canvas, options, bgRect);
            }

            var textPos = new SKPoint(currentX + (scoreAreaWidth - textWidth) / 2f, startY + (totalSize.Height - textBounds.Height) / 2f - textBounds.Top);
            DrawRatingText(canvas, options, scoreText, textPaint, EmbyIconsEnhancer.TextStrokePaint, textPos);

            textPaint.Typeface = null;

            return totalSize;
        }

        private void DrawRatingBackground(SKCanvas canvas, ProfileSettings options, SKRect bgRect)
        {
            if (!SKColor.TryParse(options.CommunityScoreBackgroundColor, out var baseBgColor)) return;

            byte alpha = (byte)Math.Clamp(options.CommunityScoreBackgroundOpacity * 2.55, 0, 255);
            using var bgPaint = new SKPaint
            {
                Color = new SKColor(baseBgColor.Red, baseBgColor.Green, baseBgColor.Blue, alpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };

            if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.Square)
            {
                canvas.DrawRect(bgRect, bgPaint);
            }
            else if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.Circle)
            {
                canvas.DrawCircle(bgRect.MidX, bgRect.MidY, Math.Min(bgRect.Width, bgRect.Height) / 2f, bgPaint);
            }
        }

        private void DrawRatingText(SKCanvas canvas, ProfileSettings options, string text, SKPaint textPaint, SKPaint strokePaint, SKPoint position)
        {
            if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.None || options.CommunityScoreBackgroundOpacity < 50)
            {
                strokePaint.Typeface = textPaint.Typeface;
                strokePaint.TextSize = textPaint.TextSize;
                strokePaint.StrokeWidth = Math.Max(1f, textPaint.TextSize / 12f);
                canvas.DrawText(text, position.X, position.Y, strokePaint);
                strokePaint.Typeface = null;
            }

            canvas.DrawText(text, position.X, position.Y, textPaint);
        }

        #region Helper methods to create overlay info
        private async Task<List<OverlayGroupInfo>> CreateIconGroups(OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken, Dictionary<IconCacheManager.IconType, List<SKImage>>? injectedIcons)
        {
            var groups = new List<OverlayGroupInfo>(8);

            var groupDefinitions = new List<(IconAlignment Alignment, int Priority, bool Horizontal, IconCacheManager.IconType Type, ICollection<string>? Names)>
            {
                (profileOptions.AudioIconAlignment, profileOptions.AudioIconPriority, profileOptions.AudioOverlayHorizontal, IconCacheManager.IconType.Language, data.AudioLanguages),
                (profileOptions.SubtitleIconAlignment, profileOptions.SubtitleIconPriority, profileOptions.SubtitleOverlayHorizontal, IconCacheManager.IconType.Subtitle, data.SubtitleLanguages),
                (profileOptions.ResolutionIconAlignment, profileOptions.ResolutionIconPriority, profileOptions.ResolutionOverlayHorizontal, IconCacheManager.IconType.Resolution, data.ResolutionIconName != null ? new[] { data.ResolutionIconName } : null),
                (profileOptions.VideoFormatIconAlignment, profileOptions.VideoFormatIconPriority, profileOptions.VideoFormatOverlayHorizontal, IconCacheManager.IconType.VideoFormat, data.VideoFormatIconName != null ? new[] { data.VideoFormatIconName } : null),
                (profileOptions.VideoCodecIconAlignment, profileOptions.VideoCodecIconPriority, profileOptions.VideoCodecOverlayHorizontal, IconCacheManager.IconType.VideoCodec, data.VideoCodecs),
                (profileOptions.TagIconAlignment, profileOptions.TagIconPriority, profileOptions.TagOverlayHorizontal, IconCacheManager.IconType.Tag, data.Tags),
                (profileOptions.ChannelIconAlignment, profileOptions.ChannelIconPriority, profileOptions.ChannelOverlayHorizontal, IconCacheManager.IconType.Channel, data.ChannelIconName != null ? new[] { data.ChannelIconName } : null),
                (profileOptions.AudioCodecIconAlignment, profileOptions.AudioCodecIconPriority, profileOptions.AudioCodecOverlayHorizontal, IconCacheManager.IconType.AudioCodec, data.AudioCodecs),
                (profileOptions.AspectRatioIconAlignment, profileOptions.AspectRatioIconPriority, profileOptions.AspectRatioOverlayHorizontal, IconCacheManager.IconType.AspectRatio, data.AspectRatioIconName != null ? new[] { data.AspectRatioIconName } : null),
                (profileOptions.ParentalRatingIconAlignment, profileOptions.ParentalRatingIconPriority, profileOptions.ParentalRatingOverlayHorizontal, IconCacheManager.IconType.ParentalRating, data.ParentalRatingIconName != null ? new[] { data.ParentalRatingIconName } : null)
            };

            foreach (var def in groupDefinitions)
            {
                if (def.Alignment != IconAlignment.Disabled && def.Names != null && def.Names.Any())
                {
                    await AddGroup(groups, def.Names, def.Type, def.Alignment, def.Priority, def.Horizontal, globalOptions, cancellationToken, injectedIcons);
                }
            }

            return groups;
        }

        private async Task AddGroup(List<OverlayGroupInfo> groups, ICollection<string> names, IconCacheManager.IconType type, IconAlignment alignment, int priority, bool horizontal, PluginOptions options, CancellationToken cancellationToken, Dictionary<IconCacheManager.IconType, List<SKImage>>? injectedIcons)
        {
            List<SKImage> icons;

            if (injectedIcons != null && injectedIcons.TryGetValue(type, out var preloadedIcons))
            {
                icons = preloadedIcons;
            }
            else
            {
                if (names == null || !names.Any()) return;
                var iconTasks = names.OrderBy(n => n)
                                     .Select(name => _iconCache.GetCachedIconAsync(name, type, options, cancellationToken))
                                     .ToList();

                var loadedIcons = await Task.WhenAll(iconTasks);
                icons = loadedIcons.Where(icon => icon != null).Select(icon => icon!).ToList();
            }


            if (icons.Any())
            {
                groups.Add(new OverlayGroupInfo(alignment, priority, horizontal, icons));
            }
        }

        private async Task<RatingOverlayInfo?> CreateRatingInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken)
        {
            if (profileOptions.CommunityScoreIconAlignment != IconAlignment.Disabled && data.CommunityRating.HasValue && data.CommunityRating.Value > 0)
            {
                var imdbIcon = await _iconCache.GetCachedIconAsync("imdb", IconCacheManager.IconType.CommunityRating, globalOptions, cancellationToken);
                return new RatingOverlayInfo(profileOptions.CommunityScoreIconAlignment, profileOptions.CommunityScoreIconPriority, profileOptions.CommunityScoreOverlayHorizontal, data.CommunityRating.Value, imdbIcon);
            }
            return null;
        }
        #endregion
    }
}