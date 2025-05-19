using System;
using System.Collections.Generic;
using System.IO;

namespace EmbyIcons.Helpers
{
    internal static class SubtitleScanner
    {
        /// <summary>
        /// Scans the folder adjacent to mediaOrInputPath for external subtitle files with specified extensions,
        /// extracts language codes from filenames, and adds them to subtitleLangs.
        /// </summary>
        /// <param name="mediaOrInputPath">Full path to media or input file</param>
        /// <param name="subtitleLangs">HashSet to add detected subtitle languages</param>
        /// <param name="enableLogging">Enable logging</param>
        /// <param name="extensions">Subtitle file extensions to scan, e.g. ".srt", ".ass", ".vtt", etc.</param>
        public static void ScanExternalSubtitles(string? mediaOrInputPath, HashSet<string> subtitleLangs, bool enableLogging, IEnumerable<string>? extensions = null)
        {
            try
            {
                if (string.IsNullOrEmpty(mediaOrInputPath))
                    return;

                var folderPath = Path.GetDirectoryName(mediaOrInputPath);

                if (string.IsNullOrEmpty(folderPath))
                    return;

                if (!Directory.Exists(folderPath))
                    return;

                // Default to .srt if no extensions provided
                var exts = extensions ?? new[] { ".srt" };

                foreach (var ext in exts)
                {
                    var trimmedExt = ext.Trim().ToLowerInvariant();
                    if (!trimmedExt.StartsWith("."))
                        trimmedExt = "." + trimmedExt;

                    var files = Directory.GetFiles(folderPath, "*" + trimmedExt);

                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                        var langCode = ExtractLangCodeFromFilename(fileName);

                        if (!string.IsNullOrEmpty(langCode) && !subtitleLangs.Contains(langCode))
                        {
                            subtitleLangs.Add(langCode);

                            if (enableLogging)
                                LoggingHelper.Log(true, $"SubtitleScanner: Detected subtitle language '{langCode}' from file '{file}'.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (enableLogging)
                    LoggingHelper.Log(true, $"SubtitleScanner: Exception scanning subtitles: {ex.Message}");
            }
        }

        private static string? ExtractLangCodeFromFilename(string fileName)
        {
            // Split by '.' or '_'
            var parts = fileName.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries);

            // Try last part first
            if (parts.Length > 1)
            {
                var candidate = parts[^1];
                if (candidate.Length >= 2 && candidate.Length <= 5) // Allow regional codes like en-US
                    return LanguageHelper.NormalizeLangCode(candidate);
            }

            // Try second last part as fallback
            if (parts.Length > 2)
            {
                var candidate = parts[^2];
                if (candidate.Length >= 2 && candidate.Length <= 5)
                    return LanguageHelper.NormalizeLangCode(candidate);
            }

            return null;
        }
    }
}