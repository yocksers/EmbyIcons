using System.Collections.Generic;
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
    }
}