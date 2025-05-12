using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace EmbyIcons.Helpers
{
    internal static class FileUtils
    {
        public static void SafeCopy(string source, string dest)
        {
            const int maxRetries = 3;
            int tries = 0;

            while (true)
            {
                try
                {
                    File.Copy(source, dest, true);
                    break;
                }
                catch (IOException)
                {
                    tries++;
                    if (tries >= maxRetries)
                        throw;
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        public static HashSet<string> GetAllowedLibraryIds(ILibraryManager libraryManager, string? selectedLibrariesCsv)
        {
            var allowedIds = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(selectedLibrariesCsv))
                return allowedIds; // empty means no restriction

            var selectedNames = selectedLibrariesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var lib in libraryManager.GetVirtualFolders())
            {
                if (selectedNames.Contains(lib.Name))
                    allowedIds.Add(lib.Id);
            }

            return allowedIds;
        }

        public static string? GetLibraryIdForItem(ILibraryManager libraryManager, BaseItem item)
        {
            if (item == null) return null;

            string? itemPath = item.Path;

            if (string.IsNullOrEmpty(itemPath)) return null;

            foreach (var lib in libraryManager.GetVirtualFolders())
            {
                if (lib.Locations != null)
                {
                    foreach (var loc in lib.Locations)
                    {
                        if (!string.IsNullOrEmpty(loc) && itemPath.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                            return lib.Id;
                    }
                }
            }

            return null;
        }
    }
}