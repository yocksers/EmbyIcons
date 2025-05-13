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
            const int maxRetries = 3;
            int tries = 0;

            while (true)
            {
                try
                {
                    using var sourceStream = File.OpenRead(source);
                    using var destStream = File.Create(dest);
                    await sourceStream.CopyToAsync(destStream);
                    await destStream.FlushAsync();
                    break;
                }
                catch (IOException)
                {
                    tries++;
                    if (tries >= maxRetries) throw;
                    await Task.Delay(100);
                }
            }
        }

        // Added missing method
        public static HashSet<string> GetAllowedLibraryIds(ILibraryManager libraryManager, string? selectedLibrariesCsv)
        {
            var allowedIds = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(selectedLibrariesCsv))
                return allowedIds; // no restriction, empty set

            var selectedNames = selectedLibrariesCsv.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            foreach (var lib in libraryManager.GetVirtualFolders())
            {
                if (selectedNames.Contains(lib.Name))
                    allowedIds.Add(lib.Id);
            }

            return allowedIds;
        }

        // Added missing method
        public static string? GetLibraryIdForItem(ILibraryManager libraryManager, BaseItem item)
        {
            if (item == null) return null;

            var itemPath = item.Path;

            if (string.IsNullOrEmpty(itemPath)) return null;

            foreach (var lib in libraryManager.GetVirtualFolders())
            {
                if (lib.Locations != null)
                {
                    foreach (var loc in lib.Locations)
                    {
                        if (!string.IsNullOrEmpty(loc) && itemPath.StartsWith(loc, System.StringComparison.OrdinalIgnoreCase))
                            return lib.Id;
                    }
                }
            }

            return null;
        }
    }
}