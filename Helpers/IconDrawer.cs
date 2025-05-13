using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace EmbyIcons.Helpers
{
    internal static class IconDrawer
    {
        public static void DrawIcons(SKCanvas canvas, List<string> icons, int size, int pad, int width, int height, IconAlignment alignment)
        {
            int count = icons.Count;

            if (count == 0) return;

            int totalWidth = count * size + (count - 1) * pad;

            float startX = (alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight)
                           ? width - totalWidth - pad : pad;

            float startY = (alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight)
                           ? height - size - pad : pad;

            for (int i = 0; i < count; i++)
            {
                using var bmpIcon = SKBitmap.Decode(icons[i]);
                if (bmpIcon == null) continue;

                float xPos = startX + i * (size + pad);

                canvas.DrawBitmap(bmpIcon, bmpIcon.Info.Rect, new SKRect(xPos, startY, xPos + size, startY + size));
            }
        }
    }
}