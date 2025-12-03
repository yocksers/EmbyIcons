using System;
using System.IO;
using MediaBrowser.Model.Logging;
using SkiaSharp;

namespace EmbyIcons.ImageProcessing
{
    public class SkiaSharpImageProcessor : IImageProcessor
    {
        private readonly ILogger _logger;
        private bool _disposed;

        public string Name => "SkiaSharp";

        public bool IsAvailable
        {
            get
            {
                try
                {
                    using (var testBitmap = new SKBitmap(1, 1))
                    {
                        return testBitmap != null;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[EmbyIcons] SkiaSharp is not available: {ex.Message}");
                    return false;
                }
            }
        }

        public SkiaSharpImageProcessor(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public object DecodeImage(Stream inputStream)
        {
            if (inputStream == null)
                throw new ArgumentNullException(nameof(inputStream));

            return SKBitmap.Decode(inputStream);
        }

        public void GetImageDimensions(object image, out int width, out int height)
        {
            var bitmap = image as SKBitmap ?? throw new ArgumentException("Image must be an SKBitmap", nameof(image));
            width = bitmap.Width;
            height = bitmap.Height;
        }

        public object CreateBlankImage(int width, int height)
        {
            var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
            }
            return bitmap;
        }

        public void DrawImage(object targetImage, object sourceImage, int x, int y, int width, int height, bool enableSmoothing)
        {
            var targetBitmap = targetImage as SKBitmap ?? throw new ArgumentException("Target must be an SKBitmap", nameof(targetImage));
            
            SKImage? sourceSkImage = null;
            try
            {
                if (sourceImage is SKImage skImage)
                {
                    sourceSkImage = skImage;
                }
                else if (sourceImage is SKBitmap sourceBitmap)
                {
                    sourceSkImage = SKImage.FromBitmap(sourceBitmap);
                }
                else
                {
                    throw new ArgumentException("Source must be an SKImage or SKBitmap", nameof(sourceImage));
                }

                using (var canvas = new SKCanvas(targetBitmap))
                using (var paint = new SKPaint 
                { 
                    IsAntialias = enableSmoothing,
                    FilterQuality = enableSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None
                })
                {
                    var destRect = new SKRect(x, y, x + width, y + height);
                    canvas.DrawImage(sourceSkImage, destRect, paint);
                }
            }
            finally
            {
                if (sourceImage is SKBitmap && sourceSkImage != null)
                {
                    sourceSkImage.Dispose();
                }
            }
        }

        public void DrawText(object image, string text, int x, int y, float fontSize, string color, string fontFamily, bool enableSmoothing)
        {
            var bitmap = image as SKBitmap ?? throw new ArgumentException("Image must be an SKBitmap", nameof(image));

            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint())
            {
                paint.Color = SKColor.Parse(color);
                paint.TextSize = fontSize;
                paint.IsAntialias = enableSmoothing;
                paint.Typeface = SKTypeface.FromFamilyName(fontFamily);
                
                canvas.DrawText(text, x, y, paint);
            }
        }

        public void EncodeImage(object image, Stream outputStream, string format, int quality)
        {
            var bitmap = image as SKBitmap ?? throw new ArgumentException("Image must be an SKBitmap", nameof(image));

            SKEncodedImageFormat skFormat = format?.ToLowerInvariant() switch
            {
                "png" => SKEncodedImageFormat.Png,
                "jpeg" or "jpg" => SKEncodedImageFormat.Jpeg,
                "webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png
            };

            using (var image2 = SKImage.FromBitmap(bitmap))
            using (var data = image2.Encode(skFormat, quality))
            {
                data.SaveTo(outputStream);
            }
        }

        public void DisposeImage(object image)
        {
            if (image is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
