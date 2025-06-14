using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;

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
                                     bool horizontal = true,
                                     int horizontalOffset = 0)
        {
            int count = icons.Count;
            if (count == 0) return;

            int totalWidth = horizontal ? count * size + (count - 1) * pad : size;
            int totalHeight = horizontal ? size : count * size + (count - 1) * pad;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? width - totalWidth - pad - horizontalOffset : pad + horizontalOffset;
            float startY = isBottom ? height - totalHeight - pad - verticalOffset : pad + verticalOffset;

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