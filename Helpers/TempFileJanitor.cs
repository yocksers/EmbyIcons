using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    internal static class TempFileJanitor
    {
        private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);
        private static readonly Regex TempNamePattern = new Regex(@"\.[0-9a-f]{32}\.tmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, byte> _checkedDirectories = new(StringComparer.Ordinal);

        public static void ScheduleCleanupFor(string outputFile)
        {
            string? directory;
            try
            {
                directory = Path.GetDirectoryName(outputFile);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(directory) || !_checkedDirectories.TryAdd(directory!, 0)) return;

            _ = Task.Run(() => CleanDirectory(directory!));
        }

        private static void CleanDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return;

                var cutoff = DateTime.UtcNow - MinimumAge;
                int removed = 0;

                foreach (var file in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    if (!TempNamePattern.IsMatch(file)) continue;

                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                    }
                }

                if (removed > 0)
                {
                    Plugin.Instance?.Logger.Info($"[EmbyIcons] Removed {removed} leftover temporary image file(s) from '{directory}'.");
                }
            }
            catch (Exception ex)
            {
                if (PluginHelper.IsDebugLoggingEnabled)
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Could not clean temporary files in '{directory}': {ex.Message}");
            }
        }
    }
}
