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
        /// <summary>
        /// Asynchronous file copy with buffer.
        /// </summary>
        public static async Task SafeCopyAsync(string source, string dest)
        {
            const int bufferSize = 81920; // 80 KB buffer

            using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
            using (var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                await sourceStream.CopyToAsync(destStream);
                await destStream.FlushAsync();
            }
        }

        /// <summary>
        /// Get the set of allowed library IDs based on names (from options).
        /// WARNING: Never call this in plugin "hot path" checks (e.g., Supports, GetEnhancedImageInfo, etc).
        /// Only call this on plugin options load/save, and cache the result for use everywhere else.
        /// </summary>
        public static HashSet<string> GetAllowedLibraryIds(ILibraryManager libraryManager, string? selectedLibrariesCsv)
        {
            var allowedIds = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(selectedLibrariesCsv))
                return allowedIds; // No restriction if empty

            var selectedNames = selectedLibrariesCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('"', '\''))
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            // This will enumerate all virtual folders - do not call in overlay/image check methods!
            foreach (var lib in libraryManager.GetVirtualFolders())
                if (selectedNames.Contains(lib.Name))
                    allowedIds.Add(lib.Id);

            return allowedIds;
        }

        /// <summary>
        /// Get the library ID for a media item based on its path.
        /// This can be called in plugin "hot path" methods as it's just a simple lookup.
        /// </summary>
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
