using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace EmbyIcons.Helpers
{
    public static class SubtitleScanner
    {
        // Existing method scanning subtitles by folder (assumed present)
        public static void ScanExternalSubtitles(string mediaFilePath, HashSet<string> detectedLanguages, bool enableLogging, IEnumerable<string> subtitleExtensions)
        {
            string folder = Path.GetDirectoryName(mediaFilePath) ?? "";
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return;

            foreach (var ext in subtitleExtensions)
            {
                var files = Directory.EnumerateFiles(folder, "*" + ext, SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    TryAddLanguageFromFilename(file, mediaFilePath, detectedLanguages, enableLogging);
                }
            }
        }

        // New method: scan only known subtitle files (from cached folder scan)
        public static void ScanExternalSubtitlesWithKnownFiles(string mediaFilePath, HashSet<string> detectedLanguages, bool enableLogging, List<string> knownSubtitleFiles)
        {
            if (knownSubtitleFiles == null || knownSubtitleFiles.Count == 0)
                return;

            foreach (var file in knownSubtitleFiles)
            {
                TryAddLanguageFromFilename(file, mediaFilePath, detectedLanguages, enableLogging);
            }
        }

        // Helper to detect language code from subtitle filename relative to media file
        private static void TryAddLanguageFromFilename(string subtitleFilePath, string mediaFilePath, HashSet<string> detectedLanguages, bool enableLogging)
        {
            try
            {
                var mediaFileNameWithoutExt = Path.GetFileNameWithoutExtension(mediaFilePath);
                var subtitleFileNameWithoutExt = Path.GetFileNameWithoutExtension(subtitleFilePath);

                // Basic check: subtitle filename must start with media filename to be related
                if (!subtitleFileNameWithoutExt.StartsWith(mediaFileNameWithoutExt, StringComparison.OrdinalIgnoreCase))
                    return;

                // Extract language code part from subtitle filename (e.g. "movie.eng.srt" -> "eng")
                var langMatch = Regex.Match(subtitleFileNameWithoutExt, @"\.(?<lang>[a-z]{2,3})(\..*)?$", RegexOptions.IgnoreCase);
                if (!langMatch.Success)
                    return;

                var langCode = langMatch.Groups["lang"].Value.ToLowerInvariant();

                if (!string.IsNullOrEmpty(langCode) && !detectedLanguages.Contains(langCode))
                {
                    detectedLanguages.Add(langCode);
                    if (enableLogging)
                        Console.WriteLine($"Detected subtitle language '{langCode}' from file '{subtitleFilePath}'.");
                }
            }
            catch (Exception ex)
            {
                if (enableLogging)
                    Console.WriteLine($"Error scanning subtitle file '{subtitleFilePath}': {ex.Message}");
            }
        }
    }
}