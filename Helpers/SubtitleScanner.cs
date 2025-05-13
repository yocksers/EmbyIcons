using System;
using System.Collections.Generic;
using System.IO;

namespace EmbyIcons.Helpers
{
    internal static class SubtitleScanner
    {
        public static void ScanExternalSubtitles(string? mediaOrInputPath, HashSet<string> subtitleLangs, bool enableLogging)
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

                var srtFiles = Directory.GetFiles(folderPath, "*.srt");

                foreach (var srt in srtFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(srt).ToLowerInvariant();
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
            catch
            {
                // Ignore errors during subtitle scan
            }
        }
    }
}