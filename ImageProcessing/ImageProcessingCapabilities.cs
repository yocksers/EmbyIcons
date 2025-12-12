using System;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.ImageProcessing
{
    public static class ImageProcessingCapabilities
    {
        private static bool? _skiaSharpAvailable;
        private static readonly object _lock = new object();
        public static bool IsSkiaSharpAvailable(ILogger logger)
        {
            var config = EmbyIcons.Plugin.Instance?.Configuration;
            if (config?.ForceDisableSkiaSharp == true)
            {
                logger?.Info("[EmbyIcons] SkiaSharp is forcibly disabled via configuration (ForceDisableSkiaSharp=true).");
                return false;
            }

            if (_skiaSharpAvailable.HasValue)
            {
                return _skiaSharpAvailable.Value;
            }

            lock (_lock)
            {
                if (_skiaSharpAvailable.HasValue)
                {
                    return _skiaSharpAvailable.Value;
                }

                try
                {
                    using (var testBitmap = new SkiaSharp.SKBitmap(1, 1))
                    {
                        if (testBitmap != null)
                        {
                            logger?.Info("[EmbyIcons] SkiaSharp is available and functional.");
                            _skiaSharpAvailable = true;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn($"[EmbyIcons] SkiaSharp is not available on this system: {ex.Message}");
                    logger?.Warn("[EmbyIcons] Icon overlays will be disabled. To enable this feature, ensure SkiaSharp libraries are properly installed.");
                }

                _skiaSharpAvailable = false;
                return false;
            }
        }
        public static void Reset()
        {
            lock (_lock)
            {
                _skiaSharpAvailable = null;
            }
        }
    }
}
