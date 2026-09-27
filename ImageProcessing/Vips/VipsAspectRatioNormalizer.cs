using System;
using NetVips;

namespace EmbyIcons.ImageProcessing.Vips
{
    internal static class VipsAspectRatioNormalizer
    {
        private static Image? NormalizeToAspectRatio(Image source, float targetWidth, float targetHeight)
        {
            float targetAspect = targetWidth / targetHeight;
            float sourceAspect = (float)source.Width / source.Height;

            if (Math.Abs(sourceAspect - targetAspect) <= 0.01f)
                return null;

            int cropWidth, cropHeight;
            if (sourceAspect > targetAspect)
            {
                cropHeight = source.Height;
                cropWidth = (int)Math.Round(source.Height * targetAspect);
            }
            else
            {
                cropWidth = source.Width;
                cropHeight = (int)Math.Round(source.Width / targetAspect);
            }

            int x = (source.Width - cropWidth) / 2;
            int y = (source.Height - cropHeight) / 2;

            return source.Crop(x, y, cropWidth, cropHeight);
        }

        public static Image? TryNormalizeTo2x3(Image source) => NormalizeToAspectRatio(source, 2f, 3f);
        public static Image? TryNormalizeToThumb(Image source) => NormalizeToAspectRatio(source, 16f, 9f);
        public static Image? TryNormalizeToBanner(Image source) => NormalizeToAspectRatio(source, 1000f, 185f);
        public static Image? TryNormalizeToSquare(Image source) => NormalizeToAspectRatio(source, 1f, 1f);
    }
}
