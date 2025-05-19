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
            const int maxRetries = 5;
            int tries = 0;
            int delayMs = 100;

            while (true)
            {
                try
                {
                    // Open source for reading with shared read/write access to reduce lock conflicts
                    using var sourceStream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    // Open destination for writing with shared read access to reduce lock conflicts
                    using var destStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read);

                    await sourceStream.CopyToAsync(destStream);
                    await destStream.FlushAsync();
                    break;
                }
                catch (IOException)
                {
                    tries++;
                    if (tries >= maxRetries)
                        throw;

                    await Task.Delay(delayMs);

                    // Exponential backoff capped at 1 second
                    delayMs = delayMs * 2 > 1000 ? 1000 : delayMs * 2;
                }
            }
        }

        // Updated method: trims spaces and removes surrounding quotes from library names
        public static HashSet<string> GetAllowedLibraryIds(ILibraryManager libraryManager, string? selectedLibrariesCsv)
        {
            var allowedIds = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(selectedLibrariesCsv))
                return allowedIds; // no restriction, empty set

            var selectedNames = selectedLibrariesCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('"', '\''))
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            foreach (var lib in libraryManager.GetVirtualFolders())
            {
                if (selectedNames.Contains(lib.Name))
                    allowedIds.Add(lib.Id);
            }

            return allowedIds;
        }

        // Updated method: normalizes paths by trimming trailing slashes and ignoring case
        public static string? GetLibraryIdForItem(ILibraryManager libraryManager, BaseItem item)
        {
            if (item == null) return null;

            var itemPath = item.Path;

            if (string.IsNullOrEmpty(itemPath)) return null;

            string NormalizePath(string path) =>
                path.TrimEnd('\\', '/').ToLowerInvariant();

            var normItemPath = NormalizePath(itemPath);

            foreach (var lib in libraryManager.GetVirtualFolders())
            {
                if (lib.Locations != null)
                {
                    foreach (var loc in lib.Locations)
                    {
                        var normLoc = NormalizePath(loc);
                        if (!string.IsNullOrEmpty(normLoc) && normItemPath.StartsWith(normLoc))
                            return lib.Id;
                    }
                }
            }

            return null;
        }
    }
}