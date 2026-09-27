using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Models;
using EmbyIcons.Services;
using MediaBrowser.Model.Logging;
using NetVips;

namespace EmbyIcons.ImageProcessing.Vips
{
    internal class NetVipsImageOverlayService
    {
        private readonly ILogger _logger;
        private readonly NetVipsIconCacheManager _iconCache;

        private sealed record IconGroupInfo(IconAlignment Alignment, int Priority, bool HorizontalLayout, List<Image> Icons) : IOverlayInfo;
        private sealed record RatingOverlayInfo(IconAlignment Alignment, int Priority, bool HorizontalLayout, float Score, Image? Icon, bool IsPercent, ScoreBackgroundShape BackgroundShape, string BackgroundColor, int BackgroundOpacity) : IOverlayInfo;

        private sealed record IconGroupDefinition(
            Func<ProfileSettings, IconAlignment> GetAlignment,
            Func<ProfileSettings, int> GetPriority,
            Func<ProfileSettings, bool> IsHorizontal,
            IconCacheManager.IconType IconType,
            Func<OverlayData, ICollection<string>?> GetNames
        );

        private static readonly IReadOnlyList<IconGroupDefinition> _groupDefinitions = new List<IconGroupDefinition>
        {
            new(p => p.AudioIconAlignment, p => p.AudioIconPriority, p => p.AudioOverlayHorizontal, IconCacheManager.IconType.Language, d => d.AudioLanguages),
            new(p => p.SubtitleIconAlignment, p => p.SubtitleIconPriority, p => p.SubtitleOverlayHorizontal, IconCacheManager.IconType.Subtitle, d => d.SubtitleLanguages),
            new(p => p.ResolutionIconAlignment, p => p.ResolutionIconPriority, p => p.ResolutionOverlayHorizontal, IconCacheManager.IconType.Resolution, d => d.ResolutionIconName != null ? new[] { d.ResolutionIconName } : null),
            new(p => p.VideoFormatIconAlignment, p => p.VideoFormatIconPriority, p => p.VideoFormatOverlayHorizontal, IconCacheManager.IconType.VideoFormat, d => d.VideoFormatIconName != null ? new[] { d.VideoFormatIconName } : null),
            new(p => p.VideoCodecIconAlignment, p => p.VideoCodecIconPriority, p => p.VideoCodecOverlayHorizontal, IconCacheManager.IconType.VideoCodec, d => d.VideoCodecs),
            new(p => p.TagIconAlignment, p => p.TagIconPriority, p => p.TagOverlayHorizontal, IconCacheManager.IconType.Tag, d => d.Tags),
            new(p => p.ChannelIconAlignment, p => p.ChannelIconPriority, p => p.ChannelOverlayHorizontal, IconCacheManager.IconType.Channel, d => d.ChannelIconName != null ? new[] { d.ChannelIconName } : null),
            new(p => p.AudioCodecIconAlignment, p => p.AudioCodecIconPriority, p => p.AudioCodecOverlayHorizontal, IconCacheManager.IconType.AudioCodec, d => d.AudioCodecs),
            new(p => p.AspectRatioIconAlignment, p => p.AspectRatioIconPriority, p => p.AspectRatioOverlayHorizontal, IconCacheManager.IconType.AspectRatio, d => d.AspectRatioIconName != null ? new[] { d.AspectRatioIconName } : null),
            new(p => p.ParentalRatingIconAlignment, p => p.ParentalRatingIconPriority, p => p.ParentalRatingOverlayHorizontal, IconCacheManager.IconType.ParentalRating, d => d.ParentalRatingIconName != null ? new[] { d.ParentalRatingIconName } : null),
            new(p => p.SourceIconAlignment, p => p.SourceIconPriority, p => p.SourceOverlayHorizontal, IconCacheManager.IconType.Source, d => d.SourceIcons),
            new(p => p.FrameRateIconAlignment, p => p.FrameRateIconPriority, p => p.FrameRateOverlayHorizontal, IconCacheManager.IconType.FrameRate, d => d.FrameRateIconName != null ? new[] { d.FrameRateIconName } : null),
            new(p => p.OriginalLanguageIconAlignment, p => p.OriginalLanguageIconPriority, p => p.OriginalLanguageOverlayHorizontal, IconCacheManager.IconType.OriginalLanguage, d => d.OriginalLanguageIconName != null ? new[] { d.OriginalLanguageIconName } : null),
            new(p => p.SeriesStatusIconAlignment, p => p.SeriesStatusIconPriority, p => p.SeriesStatusOverlayHorizontal, IconCacheManager.IconType.SeriesStatus, d => d.SeriesStatusIconName != null ? new[] { d.SeriesStatusIconName } : null),
            new(p => p.SampleRateIconAlignment, p => p.SampleRateIconPriority, p => p.SampleRateOverlayHorizontal, IconCacheManager.IconType.SampleRate, d => d.SampleRateIconName != null ? new[] { d.SampleRateIconName } : null),
            new(p => p.AudioBitRateIconAlignment, p => p.AudioBitRateIconPriority, p => p.AudioBitRateOverlayHorizontal, IconCacheManager.IconType.AudioBitRate, d => d.AudioBitRateIconName != null ? new[] { d.AudioBitRateIconName } : null),
            new(p => p.BitDepthIconAlignment, p => p.BitDepthIconPriority, p => p.BitDepthOverlayHorizontal, IconCacheManager.IconType.BitDepth, d => d.BitDepthIconName != null ? new[] { d.BitDepthIconName } : null)
        }.AsReadOnly();

        public NetVipsImageOverlayService(ILogger logger, NetVipsIconCacheManager iconCache)
        {
            _logger = logger;
            _iconCache = iconCache;
        }

        private bool HasAnyOverlaysEnabled(ProfileSettings profileOptions)
        {
            foreach (var def in _groupDefinitions)
            {
                if (def.GetAlignment(profileOptions) != IconAlignment.Disabled)
                    return true;
            }

            if (profileOptions.CommunityScoreIconAlignment != IconAlignment.Disabled) return true;
            if (profileOptions.RottenTomatoesScoreIconAlignment != IconAlignment.Disabled) return true;
            if (profileOptions.PopcornScoreIconAlignment != IconAlignment.Disabled) return true;
            if (profileOptions.MyAnimeListScoreIconAlignment != IconAlignment.Disabled) return true;
            if (profileOptions.FavoriteCountIconAlignment != IconAlignment.Disabled) return true;
            if (profileOptions.FilenameBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled)) return true;
            if (profileOptions.TagBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled)) return true;

            return false;
        }

        public async Task ApplyOverlaysToStreamAsync(byte[] sourceImageBytes, OverlayData data, ProfileSettings profileOptions, PluginOptions globalOptions, Stream outputStream, CancellationToken cancellationToken)
        {
            using var sourceImage = Image.NewFromBuffer(sourceImageBytes);

            if (!HasAnyOverlaysEnabled(profileOptions))
            {
                EncodeAndSave(sourceImage, globalOptions, outputStream, needsAlphaChannel: false);
                return;
            }

            _iconCache.Initialize(globalOptions.IconsFolder);

            var posterMinDimension = Math.Min(sourceImage.Width, sourceImage.Height);
            var iconGroups = await CreateIconGroups(data, profileOptions, cancellationToken, globalOptions).ConfigureAwait(false);
            var ratingInfo = await CreateRatingInfo(data, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);
            var rottenInfo = await CreateRottenRatingInfo(data, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);
            var popcornInfo = await CreatePopcornRatingInfo(data, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);
            var malInfo = await CreateMyAnimeListRatingInfo(data, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);
            var favoriteInfo = await CreateFavoriteCountInfo(data, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);

            var intermediates = new List<Image>();

            try
            {
                static bool IsTop(IconAlignment a) => a == IconAlignment.TopLeft || a == IconAlignment.TopRight;
                static bool IsBottom(IconAlignment a) => a == IconAlignment.BottomLeft || a == IconAlignment.BottomRight;

                bool hasTopIcons = iconGroups.Any(g => IsTop(g.Alignment))
                    || (ratingInfo != null && IsTop(ratingInfo.Alignment))
                    || (rottenInfo != null && IsTop(rottenInfo.Alignment))
                    || (popcornInfo != null && IsTop(popcornInfo.Alignment))
                    || (malInfo != null && IsTop(malInfo.Alignment))
                    || (favoriteInfo != null && IsTop(favoriteInfo.Alignment));

                bool hasBottomIcons = iconGroups.Any(g => IsBottom(g.Alignment))
                    || (ratingInfo != null && IsBottom(ratingInfo.Alignment))
                    || (rottenInfo != null && IsBottom(rottenInfo.Alignment))
                    || (popcornInfo != null && IsBottom(popcornInfo.Alignment))
                    || (malInfo != null && IsBottom(malInfo.Alignment))
                    || (favoriteInfo != null && IsBottom(favoriteInfo.Alignment));

                bool effectiveTopBar = profileOptions.EnableTopIconBar && (!profileOptions.OnlyDrawBarsWhenIconsPresent || hasTopIcons);
                bool effectiveBottomBar = profileOptions.EnableBottomIconBar && (!profileOptions.OnlyDrawBarsWhenIconsPresent || hasBottomIcons);

                int barRefDimension = Math.Max(sourceImage.Width, sourceImage.Height);
                int topBarHeight = effectiveTopBar ? EmbyIcons.Compat.MathCompat.Clamp((barRefDimension * profileOptions.TopIconBarHeight) / 100, 0, sourceImage.Height / 3) : 0;
                int bottomBarHeight = effectiveBottomBar ? EmbyIcons.Compat.MathCompat.Clamp((barRefDimension * profileOptions.BottomIconBarHeight) / 100, 0, sourceImage.Height / 3) : 0;

                bool topBarScalesImage = effectiveTopBar && !profileOptions.TopIconBarOverlay;
                bool bottomBarScalesImage = effectiveBottomBar && !profileOptions.BottomIconBarOverlay;

                int topBarSpace = topBarScalesImage ? topBarHeight : 0;
                int bottomBarSpace = bottomBarScalesImage ? bottomBarHeight : 0;

                bool needsAlphaChannel = (topBarSpace > 0 && profileOptions.TopIconBarOpacity < 100)
                    || (bottomBarSpace > 0 && profileOptions.BottomIconBarOpacity < 100);

                int finalWidth = sourceImage.Width;
                int finalHeight = sourceImage.Height + topBarSpace + bottomBarSpace;
                int posterYOffset = topBarSpace;

                var working = WithAlpha(sourceImage, intermediates);
                var canvas = CreateSolidImage(finalWidth, finalHeight, new double[] { 0, 0, 0, 0 }, intermediates);

                if (topBarScalesImage && topBarHeight > 0)
                {
                    canvas = CompositeColorRect(canvas, finalWidth, topBarHeight, 0, 0, profileOptions.TopIconBarColor, profileOptions.TopIconBarOpacity, intermediates);
                }

                canvas = Track(canvas.Composite2(working, Enums.BlendMode.Over, 0, posterYOffset), intermediates);

                if (bottomBarScalesImage && bottomBarHeight > 0)
                {
                    canvas = CompositeColorRect(canvas, finalWidth, bottomBarHeight, 0, posterYOffset + sourceImage.Height, profileOptions.BottomIconBarColor, profileOptions.BottomIconBarOpacity, intermediates);
                }

                if (effectiveTopBar && profileOptions.TopIconBarOverlay && topBarHeight > 0)
                {
                    canvas = CompositeColorRect(canvas, finalWidth, topBarHeight, 0, posterYOffset, profileOptions.TopIconBarColor, profileOptions.TopIconBarOpacity, intermediates);
                }

                if (effectiveBottomBar && profileOptions.BottomIconBarOverlay && bottomBarHeight > 0)
                {
                    canvas = CompositeColorRect(canvas, finalWidth, bottomBarHeight, 0, posterYOffset + sourceImage.Height - bottomBarHeight, profileOptions.BottomIconBarColor, profileOptions.BottomIconBarOpacity, intermediates);
                }

                var iconSize = EmbyIcons.Compat.MathCompat.Clamp((posterMinDimension * profileOptions.IconSize) / 100, 8, 512);
                var topLeftIconSize = profileOptions.TopLeftIconSize > 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((posterMinDimension * profileOptions.TopLeftIconSize) / 100, 8, 512) : iconSize;
                var topRightIconSize = profileOptions.TopRightIconSize > 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((posterMinDimension * profileOptions.TopRightIconSize) / 100, 8, 512) : iconSize;
                var bottomLeftIconSize = profileOptions.BottomLeftIconSize > 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((posterMinDimension * profileOptions.BottomLeftIconSize) / 100, 8, 512) : iconSize;
                var bottomRightIconSize = profileOptions.BottomRightIconSize > 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((posterMinDimension * profileOptions.BottomRightIconSize) / 100, 8, 512) : iconSize;
                var edgePaddingHorizontal = profileOptions.EdgePaddingHorizontal >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Width * profileOptions.EdgePaddingHorizontal) / 100, 0, 256) : EmbyIcons.Compat.MathCompat.Clamp(iconSize / 4, 2, 64);
                var edgePaddingVertical = profileOptions.EdgePaddingVertical >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Height * profileOptions.EdgePaddingVertical) / 100, 0, 256) : EmbyIcons.Compat.MathCompat.Clamp(iconSize / 4, 2, 64);

                var topLeftHPadding = profileOptions.TopLeftHorizontalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Width * profileOptions.TopLeftHorizontalPadding) / 100, 0, 256) : edgePaddingHorizontal;
                var topLeftVPadding = profileOptions.TopLeftVerticalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Height * profileOptions.TopLeftVerticalPadding) / 100, 0, 256) : edgePaddingVertical;
                var topRightHPadding = profileOptions.TopRightHorizontalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Width * profileOptions.TopRightHorizontalPadding) / 100, 0, 256) : edgePaddingHorizontal;
                var topRightVPadding = profileOptions.TopRightVerticalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Height * profileOptions.TopRightVerticalPadding) / 100, 0, 256) : edgePaddingVertical;
                var bottomLeftHPadding = profileOptions.BottomLeftHorizontalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Width * profileOptions.BottomLeftHorizontalPadding) / 100, 0, 256) : edgePaddingHorizontal;
                var bottomLeftVPadding = profileOptions.BottomLeftVerticalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Height * profileOptions.BottomLeftVerticalPadding) / 100, 0, 256) : edgePaddingVertical;
                var bottomRightHPadding = profileOptions.BottomRightHorizontalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Width * profileOptions.BottomRightHorizontalPadding) / 100, 0, 256) : edgePaddingHorizontal;
                var bottomRightVPadding = profileOptions.BottomRightVerticalPadding >= 0 ? (int)EmbyIcons.Compat.MathCompat.Clamp((sourceImage.Height * profileOptions.BottomRightVerticalPadding) / 100, 0, 256) : edgePaddingVertical;

                var interIconPadding = (int)EmbyIcons.Compat.MathCompat.Clamp((iconSize * profileOptions.IconSpacing) / 100, 0, 64);

                var context = new DrawingContext(
                    iconSize, topLeftIconSize, topRightIconSize, bottomLeftIconSize, bottomRightIconSize,
                    edgePaddingHorizontal, edgePaddingVertical,
                    topLeftHPadding, topLeftVPadding, topRightHPadding, topRightVPadding,
                    bottomLeftHPadding, bottomLeftVPadding, bottomRightHPadding, bottomRightVPadding,
                    interIconPadding, finalWidth, finalHeight, posterYOffset, topBarHeight, bottomBarHeight,
                    profileOptions);

                var overlaysByCorner = new Dictionary<IconAlignment, List<IOverlayInfo>>();
                void AddOverlay(IOverlayInfo? overlay)
                {
                    if (overlay == null) return;
                    if (!overlaysByCorner.TryGetValue(overlay.Alignment, out var list))
                    {
                        list = new List<IOverlayInfo>();
                        overlaysByCorner[overlay.Alignment] = list;
                    }
                    list.Add(overlay);
                }

                foreach (var group in iconGroups) AddOverlay(group);
                AddOverlay(ratingInfo);
                AddOverlay(rottenInfo);
                AddOverlay(popcornInfo);
                AddOverlay(malInfo);
                AddOverlay(favoriteInfo);

                var finalOverlaysByCorner = overlaysByCorner;
                if (profileOptions.SafeZones?.Count > 0)
                {
                    var activeSafeZones = profileOptions.SafeZones
                        .Where(sz => string.IsNullOrEmpty(sz.Tag) || data.Tags.Contains(sz.Tag))
                        .ToList();
                    if (activeSafeZones.Count > 0)
                        finalOverlaysByCorner = ApplySafeZoneRemapping(overlaysByCorner, activeSafeZones, context);
                }

                foreach (var corner in finalOverlaysByCorner.Keys)
                {
                    canvas = DrawCorner(canvas, finalOverlaysByCorner[corner], corner, context, intermediates);
                }

                EncodeAndSave(canvas, globalOptions, outputStream, needsAlphaChannel);
            }
            finally
            {
                foreach (var group in iconGroups)
                {
                    foreach (var icon in group.Icons)
                    {
                        try { icon.Dispose(); } catch { }
                    }
                }

                foreach (var rating in new[] { ratingInfo, rottenInfo, popcornInfo, malInfo, favoriteInfo })
                {
                    try { rating?.Icon?.Dispose(); } catch { }
                }

                for (int i = intermediates.Count - 1; i >= 0; i--)
                {
                    try { intermediates[i].Dispose(); } catch { }
                }
            }
        }

        private static Image Track(Image image, List<Image> intermediates)
        {
            intermediates.Add(image);
            return image;
        }

        private static Image WithAlpha(Image image, List<Image> intermediates)
        {
            return image.HasAlpha() ? image : Track(image.Bandjoin(255), intermediates);
        }

        private static Image CreateSolidImage(int width, int height, double[] ink, List<Image> intermediates)
        {
            var black = Track(Image.Black(width, height), intermediates);
            var filled = Track(black.NewFromImage(ink), intermediates);
            return Track(filled.Cast(Enums.BandFormat.Uchar), intermediates);
        }

        private static Image CompositeColorRect(Image canvas, int width, int height, int x, int y, string colorHex, int opacityPercent, List<Image> intermediates)
        {
            var (r, g, b) = ParseColor(colorHex);
            var alpha = (double)EmbyIcons.Compat.MathCompat.Clamp(opacityPercent * 2.55, 0, 255);
            var rect = CreateSolidImage(width, height, new double[] { r, g, b, alpha }, intermediates);
            return Track(canvas.Composite2(rect, Enums.BlendMode.Over, x, y), intermediates);
        }

        private static (double r, double g, double b) ParseColor(string colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex)) return (0, 0, 0);
            var hex = colorHex.TrimStart('#');
            if (hex.Length < 6) return (0, 0, 0);
            try
            {
                var r = Convert.ToInt32(hex.Substring(0, 2), 16);
                var g = Convert.ToInt32(hex.Substring(2, 2), 16);
                var b = Convert.ToInt32(hex.Substring(4, 2), 16);
                return (r, g, b);
            }
            catch
            {
                return (0, 0, 0);
            }
        }

        private void EncodeAndSave(Image image, PluginOptions globalOptions, Stream outputStream, bool needsAlphaChannel)
        {
            var useJpeg = globalOptions.OutputFormat switch
            {
                OutputFormat.Png => false,
                OutputFormat.Jpeg => true,
                _ => !image.HasAlpha() && !needsAlphaChannel
            };

            var quality = EmbyIcons.Compat.MathCompat.Clamp(globalOptions.JpegQuality, 10, 100);

            byte[] bytes = useJpeg
                ? image.JpegsaveBuffer(q: quality)
                : image.PngsaveBuffer();

            outputStream.Write(bytes, 0, bytes.Length);
        }

        private async Task<List<IconGroupInfo>> CreateIconGroups(OverlayData data, ProfileSettings profileOptions, CancellationToken cancellationToken, PluginOptions globalOptions)
        {
            var groups = new List<IconGroupInfo>(_groupDefinitions.Count);

            foreach (var def in _groupDefinitions)
            {
                var alignment = def.GetAlignment(profileOptions);
                var names = def.GetNames(data);

                if (alignment != IconAlignment.Disabled && names != null && names.Any())
                {
                    await AddGroup(groups, names, def.IconType, alignment, def.GetPriority(profileOptions), def.IsHorizontal(profileOptions), globalOptions, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var filenameIcon in data.FilenameBasedIcons)
            {
                if (filenameIcon.Alignment != IconAlignment.Disabled && !string.IsNullOrWhiteSpace(filenameIcon.IconName))
                {
                    await AddGroup(groups, new[] { filenameIcon.IconName }, IconCacheManager.IconType.Source, filenameIcon.Alignment, filenameIcon.Priority, filenameIcon.HorizontalLayout, globalOptions, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var tagIcon in data.TagBasedIcons)
            {
                if (tagIcon.Alignment != IconAlignment.Disabled && !string.IsNullOrWhiteSpace(tagIcon.IconName))
                {
                    await AddGroup(groups, new[] { tagIcon.IconName }, IconCacheManager.IconType.Tag, tagIcon.Alignment, tagIcon.Priority, tagIcon.HorizontalLayout, globalOptions, cancellationToken).ConfigureAwait(false);
                }
            }

            return groups;
        }

        private async Task AddGroup(List<IconGroupInfo> groups, IEnumerable<string> names, IconCacheManager.IconType type, IconAlignment align, int priority, bool horizontal, PluginOptions options, CancellationToken cancellationToken)
        {
            var icons = new List<Image>();
            foreach (var name in names)
            {
                var icon = await _iconCache.GetIconAsync(name, type, options, cancellationToken).ConfigureAwait(false);
                if (icon != null) icons.Add(icon);
            }

            if (icons.Count > 0)
            {
                groups.Add(new IconGroupInfo(align, priority, horizontal, icons));
            }
        }

        private sealed record DrawingContext(
            int IconSize, int TopLeftIconSize, int TopRightIconSize, int BottomLeftIconSize, int BottomRightIconSize,
            int EdgePaddingHorizontal, int EdgePaddingVertical,
            int TopLeftHPadding, int TopLeftVPadding, int TopRightHPadding, int TopRightVPadding,
            int BottomLeftHPadding, int BottomLeftVPadding, int BottomRightHPadding, int BottomRightVPadding,
            int InterIconPadding, int CanvasWidth, int CanvasHeight, int PosterYOffset, int TopBarHeight, int BottomBarHeight,
            ProfileSettings ProfileOptions);

        private static int GetIconSizeForCorner(IconAlignment alignment, DrawingContext context) => alignment switch
        {
            IconAlignment.TopLeft => context.TopLeftIconSize,
            IconAlignment.TopRight => context.TopRightIconSize,
            IconAlignment.BottomLeft => context.BottomLeftIconSize,
            IconAlignment.BottomRight => context.BottomRightIconSize,
            _ => context.IconSize
        };

        private static int GetHorizontalPaddingForCorner(IconAlignment alignment, DrawingContext context) => alignment switch
        {
            IconAlignment.TopLeft => context.TopLeftHPadding,
            IconAlignment.TopRight => context.TopRightHPadding,
            IconAlignment.BottomLeft => context.BottomLeftHPadding,
            IconAlignment.BottomRight => context.BottomRightHPadding,
            _ => context.EdgePaddingHorizontal
        };

        private static int GetVerticalPaddingForCorner(IconAlignment alignment, DrawingContext context) => alignment switch
        {
            IconAlignment.TopLeft => context.TopLeftVPadding,
            IconAlignment.TopRight => context.TopRightVPadding,
            IconAlignment.BottomLeft => context.BottomLeftVPadding,
            IconAlignment.BottomRight => context.BottomRightVPadding,
            _ => context.EdgePaddingVertical
        };

        private static int GetIconWidth(Image icon, int cornerIconSize)
            => icon.Height > 0 ? (int)Math.Round(cornerIconSize * ((double)icon.Width / icon.Height)) : cornerIconSize;

        private Image DrawCorner(Image canvas, List<IOverlayInfo> overlaysAtCorner, IconAlignment alignment, DrawingContext context, List<Image> intermediates)
        {
            var cornerIconSize = GetIconSizeForCorner(alignment, context);
            var ordered = overlaysAtCorner.OrderBy(o => o.Priority).ToList();

            float currentVerticalOffset = 0;

            foreach (var overlay in ordered)
            {
                int consumedHeight = overlay switch
                {
                    IconGroupInfo group => DrawIconGroup(ref canvas, group, context, cornerIconSize, (int)currentVerticalOffset, intermediates),
                    RatingOverlayInfo rating => DrawRatingOverlay(ref canvas, rating, context, cornerIconSize, (int)currentVerticalOffset, intermediates),
                    _ => 0
                };
                currentVerticalOffset += consumedHeight + context.InterIconPadding;
            }

            return canvas;
        }

        private int DrawIconGroup(ref Image canvas, IconGroupInfo group, DrawingContext context, int cornerIconSize, int verticalOffset, List<Image> intermediates)
        {
            bool isRight = group.Alignment == IconAlignment.TopRight || group.Alignment == IconAlignment.BottomRight;
            bool isBottom = group.Alignment == IconAlignment.BottomLeft || group.Alignment == IconAlignment.BottomRight;
            bool isTop = group.Alignment == IconAlignment.TopLeft || group.Alignment == IconAlignment.TopRight;

            int totalWidth = group.HorizontalLayout
                ? group.Icons.Sum(i => GetIconWidth(i, cornerIconSize)) + (group.Icons.Count - 1) * context.InterIconPadding
                : group.Icons.Select(i => GetIconWidth(i, cornerIconSize)).DefaultIfEmpty(0).Max();
            int totalHeight = group.HorizontalLayout
                ? cornerIconSize
                : (group.Icons.Count * cornerIconSize) + (group.Icons.Count - 1) * context.InterIconPadding;

            var hPadding = GetHorizontalPaddingForCorner(group.Alignment, context);
            var vPadding = GetVerticalPaddingForCorner(group.Alignment, context);

            float startX = isRight ? context.CanvasWidth - totalWidth - hPadding : hPadding;

            float startY;
            if (isTop && context.TopBarHeight > 0)
            {
                startY = vPadding + verticalOffset;
            }
            else if (isBottom && context.BottomBarHeight > 0)
            {
                startY = context.CanvasHeight - totalHeight - vPadding - verticalOffset;
            }
            else if (isBottom)
            {
                startY = context.PosterYOffset + (context.CanvasHeight - context.PosterYOffset - context.BottomBarHeight) - totalHeight - vPadding - verticalOffset;
            }
            else
            {
                startY = context.PosterYOffset + vPadding + verticalOffset;
            }

            float currentX = startX;
            float currentY = startY;

            foreach (var icon in group.Icons)
            {
                var iconWidth = GetIconWidth(icon, cornerIconSize);
                var resized = Track(icon.ThumbnailImage(iconWidth, height: cornerIconSize, size: Enums.Size.Force), intermediates);
                var toComposite = WithAlpha(resized, intermediates);
                if (group.HorizontalLayout)
                {
                    canvas = Track(canvas.Composite2(toComposite, Enums.BlendMode.Over, (int)currentX, (int)startY), intermediates);
                    currentX += iconWidth + context.InterIconPadding;
                }
                else
                {
                    canvas = Track(canvas.Composite2(toComposite, Enums.BlendMode.Over, (int)startX, (int)currentY), intermediates);
                    currentY += cornerIconSize + context.InterIconPadding;
                }
            }

            return totalHeight;
        }

        private async Task<RatingOverlayInfo?> CreateRatingInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions options, CancellationToken cancellationToken)
        {
            if (profileOptions.CommunityScoreIconAlignment == IconAlignment.Disabled || !data.CommunityRating.HasValue)
                return null;

            var icon = await _iconCache.GetIconAsync(StringConstants.ImdbIcon, IconCacheManager.IconType.CommunityRating, options, cancellationToken).ConfigureAwait(false);

            return new RatingOverlayInfo(
                profileOptions.CommunityScoreIconAlignment,
                profileOptions.CommunityScoreIconPriority,
                profileOptions.CommunityScoreOverlayHorizontal,
                data.CommunityRating.Value,
                icon,
                false,
                profileOptions.CommunityScoreBackgroundShape,
                profileOptions.CommunityScoreBackgroundColor,
                profileOptions.CommunityScoreBackgroundOpacity);
        }

        private async Task<RatingOverlayInfo?> CreateRottenRatingInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions options, CancellationToken cancellationToken)
        {
            if (profileOptions.RottenTomatoesScoreIconAlignment == IconAlignment.Disabled || !data.RottenTomatoesRating.HasValue)
                return null;

            const float RottenThreshold = 60f;
            float percent = data.RottenTomatoesRating.Value;
            string iconKey = percent < RottenThreshold ? StringConstants.SplatIcon : StringConstants.TomatoIcon;

            var icon = await _iconCache.GetIconAsync(iconKey, IconCacheManager.IconType.CommunityRating, options, cancellationToken).ConfigureAwait(false);

            return new RatingOverlayInfo(
                profileOptions.RottenTomatoesScoreIconAlignment,
                profileOptions.RottenTomatoesScoreIconPriority,
                profileOptions.RottenTomatoesScoreOverlayHorizontal,
                percent,
                icon,
                true,
                profileOptions.RottenTomatoesScoreBackgroundShape,
                profileOptions.RottenTomatoesScoreBackgroundColor,
                profileOptions.RottenTomatoesScoreBackgroundOpacity);
        }

        private async Task<RatingOverlayInfo?> CreatePopcornRatingInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions options, CancellationToken cancellationToken)
        {
            if (profileOptions.PopcornScoreIconAlignment == IconAlignment.Disabled || !data.PopcornRating.HasValue)
                return null;

            const float SpilledThreshold = 60f;
            const float VerifiedHotThreshold = 90f;
            const int VerifiedHotVotes = 500;

            float percent = data.PopcornRating.Value;
            int votes = data.PopcornVotes ?? 0;

            string iconKey;
            if (percent < SpilledThreshold)
                iconKey = StringConstants.SpilledPopcornIcon;
            else if (percent >= VerifiedHotThreshold && votes >= VerifiedHotVotes)
                iconKey = StringConstants.FreshPopcornIcon;
            else
                iconKey = StringConstants.PopcornIcon;

            var icon = await _iconCache.GetIconAsync(iconKey, IconCacheManager.IconType.CommunityRating, options, cancellationToken).ConfigureAwait(false);

            return new RatingOverlayInfo(
                profileOptions.PopcornScoreIconAlignment,
                profileOptions.PopcornScoreIconPriority,
                profileOptions.PopcornScoreOverlayHorizontal,
                percent,
                icon,
                true,
                profileOptions.PopcornScoreBackgroundShape,
                profileOptions.PopcornScoreBackgroundColor,
                profileOptions.PopcornScoreBackgroundOpacity);
        }

        private async Task<RatingOverlayInfo?> CreateMyAnimeListRatingInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions options, CancellationToken cancellationToken)
        {
            if (profileOptions.MyAnimeListScoreIconAlignment == IconAlignment.Disabled || !data.MyAnimeListRating.HasValue)
                return null;

            var icon = await _iconCache.GetIconAsync(StringConstants.MyAnimeListIcon, IconCacheManager.IconType.CommunityRating, options, cancellationToken).ConfigureAwait(false);

            return new RatingOverlayInfo(
                profileOptions.MyAnimeListScoreIconAlignment,
                profileOptions.MyAnimeListScoreIconPriority,
                profileOptions.MyAnimeListScoreOverlayHorizontal,
                data.MyAnimeListRating.Value,
                icon,
                false,
                profileOptions.MyAnimeListScoreBackgroundShape,
                profileOptions.MyAnimeListScoreBackgroundColor,
                profileOptions.MyAnimeListScoreBackgroundOpacity);
        }

        private async Task<RatingOverlayInfo?> CreateFavoriteCountInfo(OverlayData data, ProfileSettings profileOptions, PluginOptions options, CancellationToken cancellationToken)
        {
            if (profileOptions.FavoriteCountIconAlignment == IconAlignment.Disabled || !data.FavoriteCount.HasValue || data.FavoriteCount.Value == 0)
                return null;

            var icon = await _iconCache.GetIconAsync(StringConstants.HeartIcon, IconCacheManager.IconType.CommunityRating, options, cancellationToken).ConfigureAwait(false);

            return new RatingOverlayInfo(
                profileOptions.FavoriteCountIconAlignment,
                profileOptions.FavoriteCountIconPriority,
                profileOptions.FavoriteCountOverlayHorizontal,
                data.FavoriteCount.Value,
                icon,
                false,
                profileOptions.FavoriteCountBackgroundShape,
                profileOptions.FavoriteCountBackgroundColor,
                profileOptions.FavoriteCountBackgroundOpacity);
        }

        private static string GetRatingText(RatingOverlayInfo rating, ProfileSettings profileOptions)
        {
            if (rating.IsPercent)
            {
                return Math.Round(rating.Score).ToString("F0", CultureInfo.InvariantCulture) + (profileOptions.RatingPercentageSuffix ?? "%");
            }

            if (Math.Abs(rating.Score - Math.Round(rating.Score)) < 0.01)
            {
                return Math.Round(rating.Score).ToString("F0", CultureInfo.InvariantCulture);
            }

            return rating.Score.ToString("F1", CultureInfo.InvariantCulture);
        }

        private int DrawRatingOverlay(ref Image canvas, RatingOverlayInfo rating, DrawingContext context, int cornerIconSize, int verticalOffset, List<Image> intermediates)
        {
            var fontFile = VipsFontHelper.GetFontFilePath(_logger);
            var scoreText = GetRatingText(rating, context.ProfileOptions);
            var fontSizePixels = Math.Max(6, cornerIconSize * context.ProfileOptions.RatingFontSizeMultiplier);

            Image mask;
            try
            {
                mask = Track(Image.Text(scoreText, font: $"{VipsFontHelper.FamilyName} {(int)Math.Round(fontSizePixels)}", fontfile: fontFile, dpi: 72), intermediates);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[EmbyIcons] NetVips failed to render rating text: {ex.Message}");
                return 0;
            }

            {
                int textWidth = mask.Width;
                int textHeight = mask.Height;
                int iconPadding = Math.Max(1, cornerIconSize / 10);
                int iconDisplayWidth = (rating.Icon != null && rating.Icon.Height > 0) ? (int)Math.Round(cornerIconSize * ((double)rating.Icon.Width / rating.Icon.Height)) : 0;

                double bgHorizontalPadding = cornerIconSize * 0.2;
                int scoreAreaWidth = rating.BackgroundShape != ScoreBackgroundShape.None ? textWidth + (int)(bgHorizontalPadding * 2) : textWidth;

                int totalWidth = iconDisplayWidth + (iconDisplayWidth > 0 ? iconPadding : 0) + scoreAreaWidth;
                int totalHeight = cornerIconSize;

                bool isRight = rating.Alignment == IconAlignment.TopRight || rating.Alignment == IconAlignment.BottomRight;
                bool isBottom = rating.Alignment == IconAlignment.BottomLeft || rating.Alignment == IconAlignment.BottomRight;
                bool isTop = rating.Alignment == IconAlignment.TopLeft || rating.Alignment == IconAlignment.TopRight;

                var hPadding = GetHorizontalPaddingForCorner(rating.Alignment, context);
                var vPadding = GetVerticalPaddingForCorner(rating.Alignment, context);

                float startX = isRight ? context.CanvasWidth - hPadding - totalWidth : hPadding;

                float startY;
                if (isTop && context.TopBarHeight > 0)
                {
                    startY = vPadding + verticalOffset;
                }
                else if (isBottom && context.BottomBarHeight > 0)
                {
                    startY = context.CanvasHeight - vPadding - verticalOffset - totalHeight;
                }
                else if (isBottom)
                {
                    startY = context.PosterYOffset + (context.CanvasHeight - context.PosterYOffset - context.BottomBarHeight) - vPadding - verticalOffset - totalHeight;
                }
                else
                {
                    startY = context.PosterYOffset + vPadding + verticalOffset;
                }

                float currentX = startX;

                if (rating.Icon != null && iconDisplayWidth > 0)
                {
                    var resizedIcon = Track(rating.Icon.ThumbnailImage(iconDisplayWidth, height: totalHeight, size: Enums.Size.Force), intermediates);
                    canvas = Track(canvas.Composite2(WithAlpha(resizedIcon, intermediates), Enums.BlendMode.Over, (int)currentX, (int)startY), intermediates);
                    currentX += iconDisplayWidth + iconPadding;
                }

                if (rating.BackgroundShape != ScoreBackgroundShape.None)
                {
                    var (r, g, b) = ParseColor(rating.BackgroundColor);
                    var alpha = (double)EmbyIcons.Compat.MathCompat.Clamp(rating.BackgroundOpacity * 2.55, 0, 255);
                    var ink = new double[] { r, g, b, alpha };

                    var fixedCanvas = canvas;
                    var shapeCircle = rating.BackgroundShape == ScoreBackgroundShape.Circle;
                    var circleCx = (int)(currentX + scoreAreaWidth / 2.0);
                    var circleCy = (int)(startY + totalHeight / 2.0);
                    var circleRadius = Math.Min(scoreAreaWidth, totalHeight) / 2;
                    var rectLeft = (int)currentX;
                    var rectTop = (int)startY;

                    canvas = Track(fixedCanvas.Mutate(mutable =>
                    {
                        if (shapeCircle)
                            mutable.DrawCircle(ink, circleCx, circleCy, circleRadius, fill: true);
                        else
                            mutable.DrawRect(ink, rectLeft, rectTop, scoreAreaWidth, totalHeight, fill: true);
                    }), intermediates);
                }

                var textX = (int)(currentX + (scoreAreaWidth - textWidth) / 2.0);
                var textY = (int)(startY + (totalHeight - textHeight) / 2.0);

                var colorLayer = CreateSolidImage(mask.Width, mask.Height, new double[] { 255, 255, 255 }, intermediates);
                var textImage = Track(colorLayer.Bandjoin(mask), intermediates);
                canvas = Track(canvas.Composite2(textImage, Enums.BlendMode.Over, textX, textY), intermediates);

                return totalHeight;
            }
        }

        private static readonly Dictionary<IconAlignment, IconAlignment[]> _safeZoneFallbackOrder = new Dictionary<IconAlignment, IconAlignment[]>
        {
            [IconAlignment.TopLeft] = new[] { IconAlignment.TopLeft, IconAlignment.TopRight, IconAlignment.BottomLeft, IconAlignment.BottomRight },
            [IconAlignment.TopRight] = new[] { IconAlignment.TopRight, IconAlignment.TopLeft, IconAlignment.BottomRight, IconAlignment.BottomLeft },
            [IconAlignment.BottomLeft] = new[] { IconAlignment.BottomLeft, IconAlignment.BottomRight, IconAlignment.TopLeft, IconAlignment.TopRight },
            [IconAlignment.BottomRight] = new[] { IconAlignment.BottomRight, IconAlignment.BottomLeft, IconAlignment.TopRight, IconAlignment.TopLeft },
        };

        private readonly record struct RectF(float X, float Y, float Width, float Height)
        {
            public float Right => X + Width;
            public float Bottom => Y + Height;
        }

        private static Dictionary<IconAlignment, List<IOverlayInfo>> ApplySafeZoneRemapping(
            Dictionary<IconAlignment, List<IOverlayInfo>> overlaysByCorner,
            List<SafeZone> safeZones,
            DrawingContext context)
        {
            var result = new Dictionary<IconAlignment, List<IOverlayInfo>>();
            foreach (var kvp in overlaysByCorner)
            {
                var bestCorner = FindBestCornerForSafeZones(kvp.Key, kvp.Value, safeZones, context);
                if (!result.TryGetValue(bestCorner, out var list))
                {
                    list = new List<IOverlayInfo>();
                    result[bestCorner] = list;
                }
                list.AddRange(kvp.Value);
            }
            return result;
        }

        private static IconAlignment FindBestCornerForSafeZones(
            IconAlignment preferred,
            List<IOverlayInfo> overlays,
            List<SafeZone> safeZones,
            DrawingContext context)
        {
            if (!_safeZoneFallbackOrder.TryGetValue(preferred, out var candidates))
                return preferred;

            foreach (var candidate in candidates)
            {
                var bounds = EstimateCornerBounds(candidate, overlays, context);
                if (!OverlapsAnySafeZone(bounds, safeZones, context.CanvasWidth, context.CanvasHeight))
                    return candidate;
            }

            return preferred;
        }

        private static RectF EstimateCornerBounds(IconAlignment alignment, List<IOverlayInfo> overlays, DrawingContext context)
        {
            var cornerIconSize = GetIconSizeForCorner(alignment, context);
            var hPadding = GetHorizontalPaddingForCorner(alignment, context);
            var vPadding = GetVerticalPaddingForCorner(alignment, context);

            int horizontalIconCount = 0;
            int verticalGroupCount = 0;

            foreach (var o in overlays)
            {
                if (o.HorizontalLayout)
                {
                    if (o is IconGroupInfo ig)
                        horizontalIconCount += ig.Icons.Count;
                    else
                        horizontalIconCount++;
                }
                else
                {
                    verticalGroupCount++;
                }
            }

            int rowWidthLimit = Math.Max(1, context.CanvasWidth - hPadding * 2);
            int iconsPerRow = Math.Max(1, rowWidthLimit / Math.Max(1, cornerIconSize + context.InterIconPadding));
            int rows = horizontalIconCount > 0 ? (int)Math.Ceiling((double)horizontalIconCount / iconsPerRow) : 0;

            int estimatedW = Math.Min(horizontalIconCount * (cornerIconSize + context.InterIconPadding), rowWidthLimit);
            if (verticalGroupCount > 0)
                estimatedW = Math.Max(estimatedW, cornerIconSize);

            int estimatedH = (rows + verticalGroupCount) * (cornerIconSize + context.InterIconPadding);
            if (estimatedH <= 0) estimatedH = cornerIconSize;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float x = isRight ? context.CanvasWidth - hPadding - estimatedW : hPadding;
            float y = isBottom ? context.CanvasHeight - vPadding - estimatedH : context.PosterYOffset + vPadding;

            return new RectF(x, y, estimatedW, estimatedH);
        }

        private static bool OverlapsAnySafeZone(RectF bounds, List<SafeZone> safeZones, int canvasWidth, int canvasHeight)
        {
            foreach (var sz in safeZones)
            {
                float szX = sz.X * canvasWidth / 100f;
                float szY = sz.Y * canvasHeight / 100f;
                float szW = sz.Width * canvasWidth / 100f;
                float szH = sz.Height * canvasHeight / 100f;

                bool overlaps = bounds.X <= szX + szW && bounds.Right >= szX &&
                                bounds.Y <= szY + szH && bounds.Bottom >= szY;
                if (overlaps)
                    return true;
            }
            return false;
        }
    }
}
