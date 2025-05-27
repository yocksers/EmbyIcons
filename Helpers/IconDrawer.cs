using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using SkiaSharp;

namespace EmbyIcons.Helpers
{
    internal static class IconDrawer
    {
        public static void DrawIcons(SKCanvas canvas,
                                     List<SKImage> icons,
                                     int size,
                                     int pad,
                                     int width,
                                     int height,
                                     IconAlignment alignment,
                                     SKPaint paint,
                                     int verticalOffset = 0)
        {
            int count = icons.Count;

            if (count == 0) return;

            int totalWidth = count * size + (count - 1) * pad;

            float startX =
                alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight
                    ? width - totalWidth - pad
                    : pad;

            float startY =
                alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight
                    ? height - size - pad
                    : pad;

            startY += verticalOffset;

            for (int i = 0; i < count; i++)
            {
                var img = icons[i];
                if (img == null) continue;

                float xPos = startX + i * (size + pad);

                canvas.DrawImage(img, new SKRect(xPos, startY, xPos + size, startY + size), paint);
            }
        }

        public static bool ShouldDrawAnyOverlays(BaseItem item, PluginOptions options)
        {
            if (item == null || options == null)
                return false;

            if (!options.ShowAudioIcons && !options.ShowSubtitleIcons)
                return false;

            var streams = item.GetMediaStreams() ?? new List<MediaStream>();

            bool hasAudio = false, hasSubs = false;

            if (options.ShowAudioIcons)
            {
                var matchingAudio = streams
                    .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                    .Select(s => s.Language.ToLowerInvariant());

                hasAudio = matchingAudio.Any(lang => Plugin.Instance?.AudioLangSet?.Contains(lang) == true);
            }

            if (options.ShowSubtitleIcons)
            {
                var matchingSubs = streams
                    .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                    .Select(s => s.Language.ToLowerInvariant());

                hasSubs = matchingSubs.Any(lang => Plugin.Instance?.SubtitleLangSet?.Contains(lang) == true);
            }

            // ✅ If layout settings are present, allow overlay even if nothing matches yet
            return hasAudio || hasSubs || options.IconSize > 0;
        }
    }
}
