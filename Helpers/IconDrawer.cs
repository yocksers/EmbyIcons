using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using static System.Net.Mime.MediaTypeNames;
using System;

namespace EmbyIcons.Helpers
{
    internal static class IconDrawer
    {
        public static void DrawIcons(SKCanvas canvas,
                                     List<SKImage> icons,
                                     int size,
                                     int interIconPadding,
                                     int edgePadding,
                                     int width,
                                     int height,
                                     IconAlignment alignment,
                                     SKPaint paint,
                                     bool horizontal = true,
                                     int horizontalOffset = 0,
                                     int verticalOffset = 0)
        {
            int count = icons.Count;
            if (count == 0) return;

            int GetIconWidth(SKImage i)
            {
                if (i.Height == 0) return size; 
                return (int)Math.Round(size * ((float)i.Width / i.Height));
            }

            int totalWidth = horizontal ? icons.Sum(GetIconWidth) + (count - 1) * interIconPadding : icons.Select(GetIconWidth).DefaultIfEmpty(0).Max();
            int totalHeight = horizontal ? size : (count * size) + (count - 1) * interIconPadding;

            bool isRight = alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight;
            bool isBottom = alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight;

            float startX = isRight ? width - totalWidth - edgePadding - horizontalOffset : edgePadding + horizontalOffset;
            float startY = isBottom ? height - totalHeight - edgePadding - verticalOffset : edgePadding + verticalOffset;

            float currentX = startX;
            float currentY = startY;

            foreach (var img in icons)
            {
                if (img == null) continue;

                var iconWidth = GetIconWidth(img);
                var iconHeight = size;

                float xPos, yPos;

                if (horizontal)
                {
                    xPos = currentX;
                    yPos = isBottom ? height - size - edgePadding - verticalOffset : edgePadding + verticalOffset;
                    canvas.DrawImage(img, new SKRect(xPos, yPos, xPos + iconWidth, yPos + iconHeight), paint);
                    currentX += iconWidth + interIconPadding;
                }
                else
                {
                    xPos = isRight ? width - iconWidth - edgePadding - horizontalOffset : edgePadding + horizontalOffset;
                    yPos = currentY;
                    canvas.DrawImage(img, new SKRect(xPos, yPos, xPos + iconWidth, yPos + iconHeight), paint);
                    currentY += iconHeight + interIconPadding;
                }
            }
        }
    }
}
