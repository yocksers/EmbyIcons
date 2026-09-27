using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.ImageProcessing.Vips
{
    internal static class VipsFontHelper
    {
        private const string FontFamily = "Roboto Bold";

        private static volatile string? _fontFilePath;
        private static readonly object _lock = new object();

        public static string FamilyName => FontFamily;

        public static string? GetFontFilePath(ILogger logger)
        {
            if (_fontFilePath != null) return _fontFilePath;

            lock (_lock)
            {
                if (_fontFilePath != null) return _fontFilePath;

                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var name = $"{typeof(Plugin).Namespace}.Assets.Roboto-Bold.ttf";
                    using var stream = asm.GetManifestResourceStream(name);

                    if (stream == null || stream.Length == 0)
                    {
                        logger?.Warn($"[EmbyIcons] Embedded font '{name}' not found for NetVips text rendering.");
                        return null;
                    }

                    var tempPath = Path.Combine(Path.GetTempPath(), "EmbyIcons_Roboto-Bold.ttf");
                    if (!File.Exists(tempPath) || new FileInfo(tempPath).Length != stream.Length)
                    {
                        using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                        stream.CopyTo(fileStream);
                    }

                    _fontFilePath = tempPath;
                    return _fontFilePath;
                }
                catch (Exception ex)
                {
                    logger?.Warn($"[EmbyIcons] Failed to extract embedded font for NetVips: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
