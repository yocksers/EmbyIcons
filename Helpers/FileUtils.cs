using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace EmbyIcons.Helpers
{
    internal static class FileUtils
    {
        public static async Task SafeCopyAsync(string source, string dest)
        {
            // Asynchronous file copy with buffer
            const int bufferSize = 81920; // 80 KB buffer

            using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            using (var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                await sourceStream.CopyToAsync(destStream);
                await destStream.FlushAsync();
            }
        }

        public static HashSet<string> GetAllowedLibraryIds(ILibraryManager libraryManager, string? selectedLibrariesCsv)
        {
            var allowedIds = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(selectedLibrariesCsv))
                return allowedIds; // no restriction

            var selectedNames = selectedLibrariesCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('"', '\''))
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            foreach (var lib in libraryManager.GetVirtualFolders())
                if (selectedNames.Contains(lib.Name))
                    allowedIds.Add(lib.Id);

            return allowedIds;
        }

        public static string? GetLibraryIdForItem(ILibraryManager libraryManager, BaseItem item)
        {
            if (item == null) return null;

            var itemPath = item.Path;

            if (string.IsNullOrEmpty(itemPath)) return null;

            string NormalizePath(string path) =>
                path.TrimEnd('\\', '/').ToLowerInvariant();

            var normItemPath = NormalizePath(itemPath);

            foreach (var lib in libraryManager.GetVirtualFolders())
                if (lib.Locations != null)
                    foreach (var loc in lib.Locations)
                    {
                        var normLoc = NormalizePath(loc);
                        if (!string.IsNullOrEmpty(normLoc) && normItemPath.StartsWith(normLoc))
                            return lib.Id;
                    }

            return null;
        }
    }
}