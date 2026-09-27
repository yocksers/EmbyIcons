using System;
using System.Linq;
using System.Runtime.InteropServices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Logging;
using SkiaSharp;

namespace EmbyIcons.ImageProcessing
{
    public enum RenderBackend
    {
        None,
        Skia,
        NetVips
    }

    public static class ImageProcessingCapabilities
    {
        private static bool? _skiaSharpAvailable;
        private static bool? _netVipsAvailable;
        private static readonly object _lock = new object();

        public static RenderBackend GetActiveBackend(ILogger logger, IImageProcessor? imageProcessor = null)
        {
            if (IsSkiaSharpAvailable(logger, imageProcessor))
            {
                return RenderBackend.Skia;
            }

            if (IsNetVipsAvailable(logger, imageProcessor))
            {
                return RenderBackend.NetVips;
            }

            return RenderBackend.None;
        }

        public static bool IsNetVipsAvailable(ILogger logger, IImageProcessor? imageProcessor = null)
        {
            var config = EmbyIcons.Plugin.Instance?.Configuration;
            if (config?.ForceDisableSkiaSharp == true)
            {
                logger?.Info("[EmbyIcons] NetVips is forcibly disabled via configuration (ForceDisableSkiaSharp=true).");
                return false;
            }

            if (_netVipsAvailable.HasValue)
            {
                return _netVipsAvailable.Value;
            }

            lock (_lock)
            {
                if (_netVipsAvailable.HasValue)
                {
                    return _netVipsAvailable.Value;
                }

                if (imageProcessor != null && Helpers.PluginHelper.IsDebugLoggingEnabled)
                {
                    var hostHasVips = imageProcessor.ImageEncoders.Any(e => e.Name.IndexOf("libvips", StringComparison.OrdinalIgnoreCase) >= 0);
                    logger?.Debug($"[EmbyIcons] Emby-reported libvips encoder present at NetVips detection time: {hostHasVips}");
                }

                try
                {
                    if (ProbeNetVips(out var reason))
                    {
                        logger?.Info($"[EmbyIcons] NetVips is available and functional (process architecture: {ProcessArchitectureName}).");
                        _netVipsAvailable = true;
                        return true;
                    }

                    logger?.Warn($"[EmbyIcons] NetVips self-test failed on this system: {reason}");
                }
                catch (Exception ex)
                {
                    logger?.Warn($"[EmbyIcons] NetVips is not available on this system: {ex.Message}");
                }

                _netVipsAvailable = false;
                return false;
            }
        }

        public static bool IsSkiaSharpAvailable(ILogger logger, IImageProcessor? imageProcessor = null)
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

                if (imageProcessor != null && Helpers.PluginHelper.IsDebugLoggingEnabled)
                {
                    var hostHasSkia = imageProcessor.ImageEncoders.Any(e => e.Name.IndexOf("skia", StringComparison.OrdinalIgnoreCase) >= 0);
                    logger?.Debug($"[EmbyIcons] Emby-reported Skia encoder present at SkiaSharp detection time: {hostHasSkia}");
                }

                try
                {
                    if (ProbeSkiaSharp(out var reason))
                    {
                        logger?.Info($"[EmbyIcons] SkiaSharp is available and functional (process architecture: {ProcessArchitectureName}).");
                        _skiaSharpAvailable = true;
                        return true;
                    }

                    logger?.Warn($"[EmbyIcons] SkiaSharp self-test failed on this system: {reason}");
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

        private static string ProcessArchitectureName
        {
            get
            {
                try { return RuntimeInformation.ProcessArchitecture.ToString(); }
                catch { return "Unknown"; }
            }
        }

        private static bool ProbeSkiaSharp(out string reason)
        {
            const int size = 4;

            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface == null)
            {
                reason = "could not create a drawing surface";
                return false;
            }

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                canvas.DrawRect(0, 0, size, size, paint);
            }
            canvas.Flush();

            using var snapshot = surface.Snapshot();
            if (snapshot == null)
            {
                reason = "could not snapshot a drawing surface";
                return false;
            }

            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded == null || encoded.Size == 0)
            {
                reason = "could not encode PNG data";
                return false;
            }

            var pngBytes = encoded.ToArray();
            using var decoded = SKBitmap.Decode(pngBytes);
            if (decoded == null || decoded.Width != size || decoded.Height != size)
            {
                reason = "could not decode PNG data";
                return false;
            }

            using var fromBitmap = SKImage.FromBitmap(decoded);
            if (fromBitmap == null)
            {
                reason = "could not create an image from a bitmap";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool ProbeNetVips(out string reason)
        {
            const int size = 4;

            using var black = global::NetVips.Image.Black(size, size);
            using var withAlpha = black.Bandjoin(255);
            var pngBytes = withAlpha.PngsaveBuffer();
            if (pngBytes == null || pngBytes.Length == 0)
            {
                reason = "could not encode PNG data";
                return false;
            }

            using var decoded = global::NetVips.Image.NewFromBuffer(pngBytes);
            if (decoded == null || decoded.Width != size || decoded.Height != size)
            {
                reason = "could not decode PNG data";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
