using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class SubtitleScanner
    {
        public static void ScanExternalSubtitles(string? mediaOrInputPath, HashSet<string> subtitleLangs, bool enableLogging, IEnumerable<string>? extensions = null)
        {
            // REMOVED: subtitleLangs.Clear();

            if (string.IsNullOrEmpty(mediaOrInputPath))
                return;

            var folderPath = Path.GetDirectoryName(mediaOrInputPath);
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            var mediaFileNameWithoutExt = Path.GetFileNameWithoutExtension(mediaOrInputPath).ToLowerInvariant();

            var exts = extensions?.Select(e => e.StartsWith(".") ? e : "." + e).ToList() ?? new List<string> { ".srt" };

            foreach (var ext in exts)
            {
                var possibleSubFiles = Directory.GetFiles(folderPath, mediaFileNameWithoutExt + "*" + ext);

                foreach (var file in possibleSubFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                    // Extract language code explicitly from the filename after media name
                    var langCode = ExtractLangCodeFromFilename(fileName, mediaFileNameWithoutExt);

                    if (!string.IsNullOrEmpty(langCode))
                    {
                        langCode = LanguageHelper.NormalizeLangCode(langCode);
                        subtitleLangs.Add(langCode);

                        if (enableLogging)
                            LoggingHelper.Log(true, $"Detected subtitle language '{langCode}' from '{file}'.");
                    }
                }
            }
        }

        private static string? ExtractLangCodeFromFilename(string subtitleFileName, string mediaFileNameWithoutExt)
        {
            // Remove media filename from subtitle filename
            if (!subtitleFileName.StartsWith(mediaFileNameWithoutExt))
                return null;

            var remainder = subtitleFileName.Substring(mediaFileNameWithoutExt.Length).Trim('.', '_', '-', ' ');

            if (string.IsNullOrWhiteSpace(remainder))
                return null;

            var langPart = remainder.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(langPart) && langPart.Length >= 2 && langPart.Length <= 5)
                return langPart;

            return null;
        }
    }
}