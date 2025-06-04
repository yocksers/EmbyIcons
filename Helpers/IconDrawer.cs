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

            startY += verticalOffset; // Apply vertical offset

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

            // If audio, subtitle, and channel icons are all disabled, no overlays should be drawn
            if (!options.ShowAudioIcons && !options.ShowSubtitleIcons && !options.ShowAudioChannelIcons)
                return false;

            // If the item is an episode and showing overlays for episodes is disabled
            if (item is MediaBrowser.Controller.Entities.TV.Episode && !options.ShowOverlaysForEpisodes)
                return false;

            // If icon size is 0, nothing will be drawn
            if (options.IconSize <= 0)
                return false;

            // This method only checks plugin-level enablement, not if actual languages are present.
            // Language detection happens in EnhanceImageInternalAsync.
            return true;
        }
    }
}