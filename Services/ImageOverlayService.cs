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
    internal class ImageOverlayService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IconCacheManager _iconCache;

        private record OverlayGroupInfo(IconAlignment Alignment, int Priority, bool HorizontalLayout, List<SKImage> Icons) : IOverlayInfo;
        private record RatingOverlayInfo(IconAlignment Alignment, int Priority, bool HorizontalLayout, float Score, SKImage? Icon) : IOverlayInfo;
        private record DrawingContext(SKCanvas Canvas, SKPaint Paint, SKPaint TextPaint, ProfileSettings Options, int IconSize, int EdgePadding, int InterIconPadding, int CanvasWidth, int CanvasHeight);

        private static readonly SKPaint _backgroundPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };

        private static readonly IReadOnlyList<(Func<ProfileSettings, IconAlignment> align, Func<ProfileSettings, int> prio, Func<ProfileSettings, bool> horiz, IconCacheManager.IconType type, Func<OverlayData, ICollection<string>?> names)> _groupDefinitions =
            new List<(Func<ProfileSettings, IconAlignment>, Func<ProfileSettings, int>, Func<ProfileSettings, bool>, IconCacheManager.IconType, Func<OverlayData, ICollection<string>?>)>
            {
                (p => p.AudioIconAlignment, p => p.AudioIconPriority, p => p.AudioOverlayHorizontal, IconCacheManager.IconType.Language, d => d.AudioLanguages),
                (p => p.SubtitleIconAlignment, p => p.SubtitleIconPriority, p => p.SubtitleOverlayHorizontal, IconCacheManager.IconType.Subtitle, d => d.SubtitleLanguages),
                (p => p.ResolutionIconAlignment, p => p.ResolutionIconPriority, p => p.ResolutionOverlayHorizontal, IconCacheManager.IconType.Resolution, d => d.ResolutionIconName != null ? new[] { d.ResolutionIconName } : null),
                (p => p.VideoFormatIconAlignment, p => p.VideoFormatIconPriority, p => p.VideoFormatOverlayHorizontal, IconCacheManager.IconType.VideoFormat, d => d.VideoFormatIconName != null ? new[] { d.VideoFormatIconName } : null),
                (p => p.VideoCodecIconAlignment, p => p.VideoCodecIconPriority, p => p.VideoCodecOverlayHorizontal, IconCacheManager.IconType.VideoCodec, d => d.VideoCodecs),
                (p => p.TagIconAlignment, p => p.TagIconPriority, p => p.TagOverlayHorizontal, IconCacheManager.IconType.Tag, d => d.Tags),
                (p => p.ChannelIconAlignment, p => p.ChannelIconPriority, p => p.ChannelOverlayHorizontal, IconCacheManager.IconType.Channel, d => d.ChannelIconName != null ? new[] { d.ChannelIconName } : null),
                (p => p.AudioCodecIconAlignment, p => p.AudioCodecIconPriority, p => p.AudioCodecOverlayHorizontal, IconCacheManager.IconType.AudioCodec, d => d.AudioCodecs),
                (p => p.AspectRatioIconAlignment, p => p.AspectRatioIconPriority, p => p.AspectRatioOverlayHorizontal, IconCacheManager.IconType.AspectRatio, d => d.AspectRatioIconName != null ? new[] { d.AspectRatioIconName } : null),
                (p => p.ParentalRatingIconAlignment, p => p.ParentalRatingIconPriority, p => p.ParentalRatingOverlayHorizontal, IconCacheManager.IconType.ParentalRating, d => d.ParentalRatingIconName != null ? new[] { d.ParentalRatingIconName } : null)
            }.AsReadOnly();

        public ImageOverlayService(ILogger logger, IconCacheManager iconCache)
        {
            _logger = logger;
            _iconCache = iconCache;
        }

        public async Task<Stream> ApplyOverlaysAsync(SKBitmap sourceBitmap, OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken, bool sourceWasPng = false)
        {
            return await ApplyOverlaysAsync(sourceBitmap, data, profileOptions, globalOptions, cancellationToken, null, sourceWasPng);
        }

        public async Task<Stream> ApplyOverlaysAsync(SKBitmap sourceBitmap, OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken, Dictionary<IconCacheManager.IconType, List<SKImage>>? injectedIcons, bool sourceWasPng = false)
        {
            await _iconCache.InitializeAsync(globalOptions.IconsFolder, cancellationToken);

            using var surface = SKSurface.Create(new SKImageInfo(sourceBitmap.Width, sourceBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(sourceBitmap, 0, 0);

            var iconSize = Math.Clamp((Math.Min(sourceBitmap.Width, sourceBitmap.Height) * profileOptions.IconSize) / 100, 8, 512);
            var edgePadding = Math.Clamp(iconSize / 4, 2, 64);
            var interIconPadding = Math.Clamp(iconSize / 8, 1, 64);

            var paint = globalOptions.EnableImageSmoothing ? EmbyIconsEnhancer.ResamplingPaint : EmbyIconsEnhancer.AliasedPaint;
            var textPaint = globalOptions.EnableImageSmoothing ? EmbyIconsEnhancer.TextPaint : EmbyIconsEnhancer.AliasedTextPaint;

            var drawingContext = new DrawingContext(canvas, paint, textPaint, profileOptions, iconSize, edgePadding, interIconPadding, sourceBitmap.Width, sourceBitmap.Height);

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
                DrawCorner(overlaysByCorner[corner], corner, drawingContext);
            }

            using var image = surface.Snapshot();

            // Decide output format
            var format = globalOptions.OutputFormat switch
            {
                OutputFormat.Png => SKEncodedImageFormat.Png,
                OutputFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                // New "Auto" logic: Use JPEG unless the source image has transparency, in which case use PNG to preserve it.
                _ => (sourceBitmap.Info.AlphaType == SKAlphaType.Opaque) ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png
            };

            int quality = Math.Clamp(globalOptions.JpegQuality, 10, 100);
            using var encodedData = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? quality : 100);

            var memoryStream = new MemoryStream();
            await encodedData.AsStream().CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;
            return memoryStream;
        }

        private void DrawCorner(List<IOverlayInfo> overlays, IconAlignment alignment, DrawingContext context)
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
            var rowWidthLimit = context.CanvasWidth - (context.EdgePadding * 2);

            foreach (var item in horizontalDrawableItems)
            {
                SKSize itemSize;
                if (item is SKImage icon)
                {
                    int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(context.IconSize * ((float)i.Width / i.Height)) : context.IconSize;
                    itemSize = new SKSize(GetIconWidth(icon), context.IconSize);
                }
                else if (item is RatingOverlayInfo rating)
                {
                    itemSize = GetCommunityRatingOverlaySize(rating, context);
                }
                else
                {
                    continue;
                }

                if (currentHorizontalOffset > 0 && currentHorizontalOffset + itemSize.Width > rowWidthLimit)
                {
                    rowCount++;
                    if (rowCount > maxRows) break;

                    currentVerticalOffset += maxHeightOfCurrentRow + context.InterIconPadding;
                    currentHorizontalOffset = 0;
                    maxHeightOfCurrentRow = 0;
                }

                if (item is SKImage iconToDraw)
                {
                    DrawSingleIcon(iconToDraw, alignment, context, (int)currentVerticalOffset, (int)currentHorizontalOffset);
                }
                else if (item is RatingOverlayInfo ratingToDraw)
                {
                    DrawCommunityRatingOverlay(ratingToDraw, alignment, context, (int)currentVerticalOffset, (int)currentHorizontalOffset);
                }

                currentHorizontalOffset += itemSize.Width + context.InterIconPadding;
                maxHeightOfCurrentRow = Math.Max(maxHeightOfCurrentRow, itemSize.Height);
            }

            if (maxHeightOfCurrentRow > 0)
            {
                currentVerticalOffset += maxHeightOfCurrentRow + context.InterIconPadding;
            }

            currentHorizontalOffset = 0;
            foreach (var overlay in verticalDrawableItems)
            {
                var consumedSize = DrawOverlay(overlay, alignment, context, (int)currentVerticalOffset, (int)currentHorizontalOffset);
                currentVerticalOffset += consumedSize.Height + context.InterIconPadding;
            }
        }

        private SKSize DrawOverlay(IOverlayInfo overlay, IconAlignment alignment, DrawingContext context, int verticalOffset, int horizontalOffset)
        {
            if (overlay is OverlayGroupInfo iconGroup)
            {
                return DrawIconGroup(iconGroup, context, verticalOffset, horizontalOffset);
            }
            if (overlay is RatingOverlayInfo ratingInfo)
            {
                return DrawCommunityRatingOverlay(ratingInfo, alignment, context, verticalOffset, horizontalOffset);
            }
            return SKSize.Empty;
        }

        private SKSize GetCommunityRatingOverlaySize(RatingOverlayInfo rating, DrawingContext context)
        {
            var typeface = FontHelper.GetDefaultBold(_logger);
            if (typeface == null) return SKSize.Empty;

            var scoreText = rating.Score.ToString("F1");
            var fontSize = context.IconSize * 0.75f;

            var textPaint = context.TextPaint;
            textPaint.Typeface = typeface;
            textPaint.TextSize = fontSize;
            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);
            textPaint.Typeface = null;

            int textWidth = (int)Math.Ceiling(textBounds.Width);
            int iconPadding = Math.Max(1, context.IconSize / 10);
            int iconDisplayWidth = (rating.Icon != null && rating.Icon.Height > 0) ? (int)Math.Round(context.IconSize * ((float)rating.Icon.Width / rating.Icon.Height)) : 0;

            float bgHorizontalPadding = context.IconSize * 0.2f;
            int scoreAreaWidth = (context.Options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None) ? textWidth + (int)(bgHorizontalPadding * 2) : textWidth;

            int totalWidth = iconDisplayWidth + (iconDisplayWidth > 0 ? iconPadding : 0) + scoreAreaWidth;
            int totalHeight = context.IconSize;

            return new SKSize(totalWidth, totalHeight);
        }

        private void DrawSingleIcon(SKImage icon, IconAlignment alignment, DrawingContext context, int verticalOffset, int horizontalOffset)
        {
            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(context.IconSize * ((float)i.Width / i.Height)) : context.IconSize;
            var iconWidth = GetIconWidth(icon);
            var iconHeight = context.IconSize;

            float x = isRight ? context.CanvasWidth - context.EdgePadding - horizontalOffset - iconWidth : context.EdgePadding + horizontalOffset;
            float y = isBottom ? context.CanvasHeight - context.EdgePadding - verticalOffset - iconHeight : context.EdgePadding + verticalOffset;

            context.Canvas.DrawImage(icon, SKRect.Create(x, y, iconWidth, iconHeight), context.Paint);
        }

        private SKSize DrawIconGroup(OverlayGroupInfo group, DrawingContext context, int verticalOffset, int horizontalOffset)
        {
            bool isRight = group.Alignment == IconAlignment.TopRight || group.Alignment == IconAlignment.BottomRight;
            bool isBottom = group.Alignment == IconAlignment.BottomLeft || group.Alignment == IconAlignment.BottomRight;

            int GetIconWidth(SKImage i) => i.Height > 0 ? (int)Math.Round(context.IconSize * ((float)i.Width / i.Height)) : context.IconSize;

            int totalWidth = group.HorizontalLayout ? group.Icons.Sum(GetIconWidth) + (group.Icons.Count - 1) * context.InterIconPadding : group.Icons.Select(GetIconWidth).DefaultIfEmpty(0).Max();
            int totalHeight = group.HorizontalLayout ? context.IconSize : (group.Icons.Count * context.IconSize) + (group.Icons.Count - 1) * context.InterIconPadding;

            float startX = isRight ? context.CanvasWidth - totalWidth - context.EdgePadding - horizontalOffset : context.EdgePadding + horizontalOffset;
            float startY = isBottom ? context.CanvasHeight - totalHeight - context.EdgePadding - verticalOffset : context.EdgePadding + verticalOffset;

            float currentX = startX;
            float currentY = startY;

            foreach (var img in group.Icons)
            {
                var iconWidth = GetIconWidth(img);
                var iconHeight = context.IconSize;

                if (group.HorizontalLayout)
                {
                    context.Canvas.DrawImage(img, new SKRect(currentX, startY, currentX + iconWidth, iconHeight + startY), context.Paint);
                    currentX += iconWidth + context.InterIconPadding;
                }
                else
                {
                    context.Canvas.DrawImage(img, new SKRect(startX, currentY, startX + iconWidth, iconHeight + currentY), context.Paint);
                    currentY += iconHeight + context.InterIconPadding;
                }
            }

            return new SKSize(totalWidth, totalHeight);
        }

        private SKSize DrawCommunityRatingOverlay(RatingOverlayInfo rating, IconAlignment alignment, DrawingContext context, int verticalOffset, int horizontalOffset)
        {
            var totalSize = GetCommunityRatingOverlaySize(rating, context);
            if (totalSize.IsEmpty) return SKSize.Empty;

            var typeface = FontHelper.GetDefaultBold(_logger);
            var scoreText = rating.Score.ToString("F1");
            var fontSize = context.IconSize * 0.75f;

            var textPaint = context.TextPaint;
            var textStrokePaint = EmbyIconsEnhancer.TextStrokePaint;

            textPaint.Typeface = typeface;
            textPaint.TextSize = fontSize;

            SKRect textBounds = new();
            textPaint.MeasureText(scoreText, ref textBounds);

            int textWidth = (int)Math.Ceiling(textBounds.Width);
            int iconPadding = Math.Max(1, context.IconSize / 10);
            int iconDisplayWidth = (rating.Icon != null && rating.Icon.Height > 0) ? (int)Math.Round(context.IconSize * ((float)rating.Icon.Width / rating.Icon.Height)) : 0;
            float bgHorizontalPadding = context.IconSize * 0.2f;
            int scoreAreaWidth = (context.Options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None) ? textWidth + (int)(bgHorizontalPadding * 2) : textWidth;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? context.CanvasWidth - context.EdgePadding - horizontalOffset - totalSize.Width : context.EdgePadding + horizontalOffset;
            float startY = isBottom ? context.CanvasHeight - context.EdgePadding - verticalOffset - totalSize.Height : context.EdgePadding + verticalOffset;
            float currentX = startX;

            if (rating.Icon != null && iconDisplayWidth > 0)
            {
                context.Canvas.DrawImage(rating.Icon, SKRect.Create(currentX, startY, iconDisplayWidth, totalSize.Height), context.Paint);
                currentX += iconDisplayWidth + iconPadding;
            }

            var bgRect = SKRect.Create(currentX, startY, scoreAreaWidth, totalSize.Height);

            if (context.Options.CommunityScoreBackgroundShape != ScoreBackgroundShape.None)
            {
                DrawRatingBackground(context.Canvas, context.Options, bgRect);
            }

            var textPos = new SKPoint(currentX + (scoreAreaWidth - textWidth) / 2f, startY + (totalSize.Height - textBounds.Height) / 2f - textBounds.Top);
            DrawRatingText(context.Canvas, context.Options, scoreText, textPaint, EmbyIconsEnhancer.TextStrokePaint, textPos);

            textPaint.Typeface = null;

            return totalSize;
        }

        private void DrawRatingBackground(SKCanvas canvas, ProfileSettings options, SKRect bgRect)
        {
            if (!SKColor.TryParse(options.CommunityScoreBackgroundColor, out var baseBgColor)) return;

            byte alpha = (byte)Math.Clamp(options.CommunityScoreBackgroundOpacity * 2.55, 0, 255);
            _backgroundPaint.Color = new SKColor(baseBgColor.Red, baseBgColor.Green, baseBgColor.Blue, alpha);

            if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.Square)
            {
                canvas.DrawRect(bgRect, _backgroundPaint);
            }
            else if (options.CommunityScoreBackgroundShape == ScoreBackgroundShape.Circle)
            {
                canvas.DrawCircle(bgRect.MidX, bgRect.MidY, Math.Min(bgRect.Width, bgRect.Height) / 2f, _backgroundPaint);
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

        private async Task<List<OverlayGroupInfo>> CreateIconGroups(OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken, Dictionary<IconCacheManager.IconType, List<SKImage>>? injectedIcons)
        {
            var groups = new List<OverlayGroupInfo>(8);

            foreach (var def in _groupDefinitions)
            {
                var alignment = def.align(profileOptions);
                var names = def.names(data);

                if (alignment != IconAlignment.Disabled && names != null && names.Any())
                {
                    var priority = def.prio(profileOptions);
                    var isHorizontal = def.horiz(profileOptions);
                    await AddGroup(groups, names, def.type, alignment, priority, isHorizontal, globalOptions, cancellationToken, injectedIcons);
                }
            }

            return groups;
        }

        private async Task AddGroup(List<OverlayGroupInfo> groups, IEnumerable<string> names, IconCacheManager.IconType type, IconAlignment align, int prio, bool horizontal, PluginOptions options, CancellationToken cancellationToken, Dictionary<IconCacheManager.IconType, List<SKImage>>? injectedIcons)
        {
            var imgs = new List<SKImage>();

            if (injectedIcons != null && injectedIcons.TryGetValue(type, out var inj) && inj != null && inj.Count > 0)
            {
                imgs.AddRange(inj);
            }
            else
            {
                foreach (var name in names)
                {
                    var img = await _iconCache.GetCachedIconAsync(name, type, options, cancellationToken);
                    if (img != null) imgs.Add(img);
                }
            }

            if (imgs.Count > 0)
            {
                groups.Add(new OverlayGroupInfo(align, prio, horizontal, imgs));
            }
        }

        private async Task<RatingOverlayInfo?> CreateRatingInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions options, CancellationToken cancellationToken)
        {
            if (profileOptions.CommunityScoreIconAlignment == IconAlignment.Disabled || !(data.CommunityRating.HasValue))
            {
                return null;
            }

            var ratingIcon = await _iconCache.GetCachedIconAsync("imdb", IconCacheManager.IconType.CommunityRating, options, cancellationToken);
            return new RatingOverlayInfo(profileOptions.CommunityScoreIconAlignment, profileOptions.CommunityScoreIconPriority, profileOptions.CommunityScoreOverlayHorizontal, data.CommunityRating.Value, ratingIcon);
        }

        public void Dispose()
        {
            _backgroundPaint.Dispose();
        }
    }
}