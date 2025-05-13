using System.Collections.Generic;
using SkiaSharp;

namespace EmbyIcons.Helpers
{
    internal static class IconDrawer
    {
        /// <summary>
        /// Draws a list of SKBitmap icons on the given canvas, aligned as specified.
        /// </summary>
        /// <param name="canvas">SKCanvas to draw on</param>
        /// <param name="icons">List of decoded SKBitmap icons</param>
        /// <param name="size">Size (width and height) of each icon</param>
        /// <param name="pad">Padding between icons and edges</param>
        /// <param name="width">Width of the canvas surface</param>
        /// <param name="height">Height of the canvas surface</param>
        /// <param name="alignment">Corner alignment for icon placement</param>
        public static void DrawIcons(SKCanvas canvas, List<SKBitmap> icons, int size, int pad, int width, int height, IconAlignment alignment)
        {
            int count = icons.Count;

            if (count == 0) return;

            // Calculate total width occupied by all icons and padding
            int totalWidth = count * size + (count - 1) * pad;

            // Determine starting X coordinate based on alignment (left or right)
            float startX = (alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight)
                           ? width - totalWidth - pad
                           : pad;

            // Determine starting Y coordinate based on alignment (top or bottom)
            float startY = (alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight)
                           ? height - size - pad
                           : pad;

            for (int i = 0; i < count; i++)
            {
                var bmpIcon = icons[i];
                if (bmpIcon == null) continue;

                float xPos = startX + i * (size + pad);

                // Draw the icon scaled to desired size at calculated position
                canvas.DrawBitmap(bmpIcon, bmpIcon.Info.Rect, new SKRect(xPos, startY, xPos + size, startY + size));
            }
        }
    }
}