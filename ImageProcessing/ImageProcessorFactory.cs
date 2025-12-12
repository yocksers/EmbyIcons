using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.ImageProcessing
{
    public static class ImageProcessorFactory
    {
        private static IImageProcessor? _cachedProcessor;
        private static readonly object _lock = new object();
        public static IImageProcessor GetImageProcessor(ILogger logger)
        {
            if (_cachedProcessor != null && !_cachedProcessor.IsAvailable)
            {
                lock (_lock)
                {
                    _cachedProcessor?.Dispose();
                    _cachedProcessor = null;
                }
            }

            if (_cachedProcessor != null)
            {
                return _cachedProcessor;
            }

            lock (_lock)
            {
                if (_cachedProcessor != null)
                {
                    return _cachedProcessor;
                }

                var processors = new List<Func<IImageProcessor>>
                {
                    () => new SkiaSharpImageProcessor(logger),
                    
                    () => new EmbyNativeImageProcessor(logger)
                };

                foreach (var processorFactory in processors)
                {
                    try
                    {
                        var processor = processorFactory();
                        if (processor.IsAvailable)
                        {
                            logger?.Info($"[EmbyIcons] Using image processor: {processor.Name}");
                            _cachedProcessor = processor;
                            return processor;
                        }
                        else
                        {
                            logger?.Debug($"[EmbyIcons] Image processor {processor.Name} is not available");
                            processor.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Debug($"[EmbyIcons] Failed to initialize image processor: {ex.Message}");
                    }
                }

                logger?.Warn("[EmbyIcons] No preferred image processor available, using BasicFallback. Icon overlays will not be applied.");
                if (logger == null)
                    throw new InvalidOperationException("Logger is required but was null");
                _cachedProcessor = new EmbyNativeImageProcessor(logger);
                return _cachedProcessor;
            }
        }
        public static void Reset()
        {
            lock (_lock)
            {
                _cachedProcessor?.Dispose();
                _cachedProcessor = null;
            }
        }
        public static bool IsSkiaSharpAvailable(ILogger logger)
        {
            try
            {
                var processor = new SkiaSharpImageProcessor(logger);
                bool available = processor.IsAvailable;
                processor.Dispose();
                return available;
            }
            catch
            {
                return false;
            }
        }
    }
}
