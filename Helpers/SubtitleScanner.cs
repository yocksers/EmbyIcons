using System;
using System.Collections.Generic;
using System.IO;

namespace EmbyIcons.Helpers
{
    internal static class SubtitleScanner
    {
        // Updated method signature with extensions parameter
        public static void ScanExternalSubtitles(string? mediaOrInputPath, HashSet<string> subtitleLangs, bool enableLogging, IEnumerable<string>? extensions = null)
        {
            try
            {
                if (string.IsNullOrEmpty(mediaOrInputPath) || extensions == null)
                    return;

                var folderPath = Path.GetDirectoryName(mediaOrInputPath);

                if (string.IsNullOrEmpty(folderPath))
                    return;

                if (!Directory.Exists(folderPath))
                    return;

                foreach (var ext in extensions)
                {
                    var extTrimmed = ext.Trim().ToLowerInvariant();

                    if (!extTrimmed.StartsWith("."))
                        extTrimmed = "." + extTrimmed;

                    var files = Directory.GetFiles(folderPath, "*" + extTrimmed);

                    foreach (var file in files)
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        string? langCode = null;

                        var parts = fileName.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length > 1)
                        {
                            var candidate = parts[^1];

                            if (candidate.Length >= 2 && candidate.Length <= 3)
                            {
                                langCode = LanguageHelper.NormalizeLangCode(candidate);
                            }
                        }

                        if (!string.IsNullOrEmpty(langCode) && !subtitleLangs.Contains(langCode))
                            subtitleLangs.Add(langCode);
                    }
                }
            }
            catch
            {
                // Ignore errors during subtitle scan
            }
        }
    }
}