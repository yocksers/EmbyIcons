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
                                     int horizontalOffset = 0,
                                     bool useActualSize = false)
        {
            int count = icons.Count;
            if (count == 0) return;

            int GetWidth(SKImage i) => useActualSize ? i.Width : size;
            int GetHeight(SKImage i) => useActualSize ? i.Height : size;

            int totalWidth = horizontal ? icons.Sum(GetWidth) + (count - 1) * pad : icons.Select(GetWidth).DefaultIfEmpty(0).Max();
            int totalHeight = horizontal ? icons.Select(GetHeight).DefaultIfEmpty(0).Max() : icons.Sum(GetHeight) + (count - 1) * pad;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? width - totalWidth - pad - horizontalOffset : pad + horizontalOffset;
            float startY = isBottom ? height - totalHeight - pad - verticalOffset : pad + verticalOffset;

            float currentX = startX;
            float currentY = startY;

            foreach (var img in icons)
            {
                if (img == null) continue;

                var iconWidth = GetWidth(img);
                var iconHeight = GetHeight(img);

                float xPos, yPos;

                if (horizontal)
                {
                    xPos = currentX;
                    yPos = isBottom ? height - iconHeight - pad - verticalOffset : pad + verticalOffset;
                    canvas.DrawImage(img, new SKRect(xPos, yPos, xPos + iconWidth, yPos + iconHeight), paint);
                    currentX += iconWidth + pad;
                }
                else
                {
                    xPos = isRight ? width - iconWidth - pad - horizontalOffset : pad + horizontalOffset;
                    yPos = currentY;
                    canvas.DrawImage(img, new SKRect(xPos, yPos, xPos + iconWidth, yPos + iconHeight), paint);
                    currentY += iconHeight + pad;
                }
            }
        }

        public static bool ShouldDrawAnyOverlays(BaseItem item, PluginOptions options)
        {
            if (item == null || options == null)
                return false;

            if (!options.ShowAudioIcons && !options.ShowSubtitleIcons && !options.ShowAudioChannelIcons && !options.ShowVideoFormatIcons)
                return false;

            return true;
        }
    }
}
