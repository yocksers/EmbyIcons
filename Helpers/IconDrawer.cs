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
                                     int verticalOffset = 0,
                                     bool horizontal = true)
        {
            int count = icons.Count;
            if (count == 0) return;

            int totalWidth = horizontal ? count * size + (count - 1) * pad : size;
            int totalHeight = horizontal ? size : count * size + (count - 1) * pad;

            float startX =
                alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight
                    ? width - totalWidth - pad
                    : pad;

            float startY =
                alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight
                    ? height - totalHeight - pad
                    : pad;

            startY += verticalOffset;

            for (int i = 0; i < count; i++)
            {
                var img = icons[i];
                if (img == null) continue;

                float xPos = horizontal ? startX + i * (size + pad) : startX;
                float yPos = horizontal ? startY : startY + i * (size + pad);

                canvas.DrawImage(img, new SKRect(xPos, yPos, xPos + size, yPos + size), paint);
            }
        }

        public static bool ShouldDrawAnyOverlays(BaseItem item, PluginOptions options)
        {
            if (item == null || options == null)
                return false;

            if (!options.ShowAudioIcons && !options.ShowSubtitleIcons && !options.ShowAudioChannelIcons && !options.ShowVideoFormatIcons && !options.ShowResolutionIcons)
                return false;

            if (item is MediaBrowser.Controller.Entities.TV.Episode && !options.ShowOverlaysForEpisodes)
                return false;

            if (options.IconSize <= 0)
                return false;

            return true;
        }
    }
}
