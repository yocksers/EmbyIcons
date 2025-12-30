using System;
using System.IO;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.ImageProcessing
{
    public class EmbyNativeImageProcessor : IImageProcessor
    {
        private readonly ILogger _logger;
        private bool _disposed;

        public string Name => "BasicFallback";

        public bool IsAvailable
        {
            get
            {
                return true;
            }
        }

        public EmbyNativeImageProcessor(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.Warn("[EmbyIcons] Using BasicFallback image processor. Icon overlays will not be applied. Please ensure SkiaSharp is available for full functionality.");
        }

        public object DecodeImage(Stream inputStream)
        {
            if (inputStream == null)
                throw new ArgumentNullException(nameof(inputStream));

            // IMPORTANT: Caller MUST dispose the returned MemoryStream
            var ms = new MemoryStream();
            inputStream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        public void GetImageDimensions(object image, out int width, out int height)
        {
            _logger?.Debug("[EmbyIcons] GetImageDimensions not supported in BasicFallback processor.");
            width = 0;
            height = 0;
        }

        public object CreateBlankImage(int width, int height)
        {
            _logger?.Debug("[EmbyIcons] CreateBlankImage not supported in BasicFallback processor.");
            return new MemoryStream();
        }

        public void DrawImage(object targetImage, object sourceImage, int x, int y, int width, int height, bool enableSmoothing)
        {
            _logger?.Debug("[EmbyIcons] DrawImage not supported in BasicFallback processor - SkiaSharp required.");
        }

        public void DrawText(object image, string text, int x, int y, float fontSize, string color, string fontFamily, bool enableSmoothing)
        {
            _logger?.Debug("[EmbyIcons] DrawText not supported in BasicFallback processor - SkiaSharp required.");
        }

        public void EncodeImage(object image, Stream outputStream, string format, int quality)
        {
            var stream = image as Stream ?? throw new ArgumentException("Image must be a Stream", nameof(image));
            
            try
            {
                stream.Position = 0;
                stream.CopyTo(outputStream);
            }
            catch (Exception ex)
            {
                _logger?.Error($"[EmbyIcons] Error encoding image: {ex.Message}");
                throw;
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
