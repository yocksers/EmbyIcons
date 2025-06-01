using System; 
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
        /// Asynchronous file copy with buffer and robust retry logic for file locking issues.
        /// Always writes to a temporary file and then moves it to the destination.
        /// </summary>
        public static async Task SafeCopyAsync(string source, string dest)
        {
            const int bufferSize = 262144; // 256 KB buffer
            const int maxRetries = 5;
            int retries = 0;
            int delayMs = 100;

            // Ensure destination directory exists
            var destDir = Path.GetDirectoryName(dest) ?? throw new ArgumentException("Invalid destination path for file copy.");
            Directory.CreateDirectory(destDir);

            // Generate a unique temporary file path for the destination
            string tempDest = Path.Combine(destDir, Path.GetFileName(dest) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            // Retry loop for the copy operation itself (source to temp dest)
            while (true)
            {
                try
                {
                    using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
                    using (var tempDestStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                    {
                        await sourceStream.CopyToAsync(tempDestStream);
                        await tempDestStream.FlushAsync();
                    }
                    break; // Copy successful, exit loop
                }
                catch (IOException ex)
                {
                    retries++;
                    if (retries >= maxRetries)
                    {
                        // Log failure, but the calling EnhanceImageInternalAsync also has a fallback
                        Plugin.Instance?.Logger?.ErrorException($"[EmbyIcons] Failed to copy '{source}' to temporary '{tempDest}' after {maxRetries} retries due to IO error. Original message: {ex.Message}", ex);
                        throw; // Re-throw to caller for outer handling
                    }
                    Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Retrying copy from '{source}' to temporary '{tempDest}'. Retry {retries}/{maxRetries}. Error: {ex.Message}");
                    await Task.Delay(delayMs);
                    delayMs = Math.Min(5000, delayMs * 2); // Max 5 seconds delay
                }
                catch (Exception ex)
                {
                    // Catch other unexpected errors during copy
                    Plugin.Instance?.Logger?.ErrorException($"[EmbyIcons] Unexpected error copying '{source}' to temporary '{tempDest}'. Original message: {ex.Message}", ex);
                    throw;
                }
            }

            // Reset retries and delay for the move/delete operation
            retries = 0;
            delayMs = 100;

            // Retry loop for replacing the final destination file
            while (true)
            {
                try
                {
                    // Attempt to delete the old file first if it exists
                    if (File.Exists(dest))
                    {
                        File.Delete(dest);
                    }
                    // Move the temporary file to the final destination
                    File.Move(tempDest, dest);
                    break; // Move successful, exit loop
                }
                catch (IOException ex)
                {
                    retries++;
                    if (retries >= maxRetries)
                    {
                        // Log failure, but the calling EnhanceImageInternalAsync also has a fallback
                        Plugin.Instance?.Logger?.ErrorException($"[EmbyIcons] Failed to move temporary '{tempDest}' to final '{dest}' after {maxRetries} retries due to IO error. Original message: {ex.Message}", ex);
                        // Clean up the temporary file if the move ultimately fails
                        try { File.Delete(tempDest); } catch (Exception cleanupEx) { Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Failed to clean up temp file '{tempDest}': {cleanupEx.Message}"); }
                        throw;
                    }
                    Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Retrying move of temporary '{tempDest}' to final '{dest}'. Retry {retries}/{maxRetries}. Error: {ex.Message}");
                    await Task.Delay(delayMs);
                    delayMs = Math.Min(5000, delayMs * 2); // Max 5 seconds delay
                }
                catch (Exception ex)
                {
                    // Catch other unexpected errors during move
                    Plugin.Instance?.Logger?.ErrorException($"[EmbyIcons] Unexpected error moving temporary '{tempDest}' to final '{dest}'. Original message: {ex.Message}", ex);
                    try { File.Delete(tempDest); } catch (Exception cleanupEx) { Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Failed to clean up temp file '{tempDest}': {cleanupEx.Message}"); }
                    throw;
                }
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