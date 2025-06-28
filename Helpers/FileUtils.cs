using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using System.Threading;

namespace EmbyIcons.Helpers
{
    internal static class FileUtils
    {
        public static async Task SafeCopyAsync(string source, string dest, CancellationToken cancellationToken)
        {
            try
            {
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    Plugin.Instance?.Logger?.Warn($"[EmbyIcons] Source file for SafeCopyAsync does not exist: '{source}'.");
                    return;
                }

                const int bufferSize = 262144;

                var destDir = Path.GetDirectoryName(dest);
                if (string.IsNullOrEmpty(destDir))
                {
                    throw new ArgumentException("Invalid destination path for file copy.");
                }

                Directory.CreateDirectory(destDir);

                string tempDest = Path.Combine(destDir, Path.GetFileName(dest) + "." + Guid.NewGuid().ToString("N") + ".tmp");

                using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
                using (var tempDestStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
                {
                    await sourceStream.CopyToAsync(tempDestStream, cancellationToken);
                    await tempDestStream.FlushAsync(cancellationToken);
                }

                if (File.Exists(dest))
                {
                    File.Replace(tempDest, dest, null);
                }
                else
                {
                    File.Move(tempDest, dest);
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.ErrorException($"[EmbyIcons] SafeCopyAsync failed to copy '{source}' to '{dest}'. Error: {ex.Message}", ex);
                throw;
            }
        }

        // --- BEGIN REWRITE: The old library checking methods are no longer used and have been removed. ---
        // --- END REWRITE ---
    }
}