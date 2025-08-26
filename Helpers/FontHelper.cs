using MediaBrowser.Model.Logging;
using SkiaSharp;
using System.Reflection;

namespace EmbyIcons.Helpers
{
    internal static class FontHelper
    {
        private static SKTypeface? _typeface;
        private static readonly object _lock = new object();

        public static SKTypeface GetDefaultBold(ILogger logger)
        {
            if (_typeface != null) return _typeface;

            lock (_lock)
            {
                if (_typeface != null) return _typeface;

                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var name = $"{typeof(Plugin).Namespace}.Assets.Roboto-Bold.ttf";
                    using var stream = asm.GetManifestResourceStream(name);

                    if (stream == null || stream.Length == 0)
                    {
                        logger?.Warn($"[EmbyIcons] Embedded font '{name}' not found. Falling back to system font.");
                        _typeface = SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.CreateDefault();
                    }
                    else
                    {
                        logger?.Info("[EmbyIcons] Successfully loaded embedded font.");
                        _typeface = SKTypeface.FromStream(stream);
                    }
                }
                catch (System.Exception ex)
                {
                    logger?.ErrorException("[EmbyIcons] CRITICAL: Failed to load embedded font, falling back to absolute default.", ex);
                    _typeface = SKTypeface.CreateDefault();
                }

                return _typeface;
            }
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                _typeface?.Dispose();
                _typeface = null;
            }
        }
    }
}