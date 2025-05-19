using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbyIcons.Helpers
{
    public static class SubtitleFolderCacheHelper
    {
        private static readonly ConcurrentDictionary<string, List<string>> _folderSubtitleCache = new();

        public static List<string> GetSubtitleFilesForFolder(string folderPath, string[] subtitleExtensions)
        {
            if (_folderSubtitleCache.TryGetValue(folderPath, out var cached))
                return cached;

            if (!Directory.Exists(folderPath))
                return new List<string>();

            var files = Directory.EnumerateFiles(folderPath)
                .Where(f => subtitleExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            _folderSubtitleCache[folderPath] = files;
            return files;
        }
    }
}