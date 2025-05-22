using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    internal static class MediaInfoDetector
    {
        /// <summary>
        /// Detect embedded audio and subtitle languages from media using ffprobe.
        /// </summary>
        public static async Task DetectLanguagesFromMediaAsync(string mediaFile, HashSet<string> audioLangs, HashSet<string> subtitleLangs, bool enableLogging)
        {
            audioLangs.Clear();
            subtitleLangs.Clear();

            try
            {
                // Corrected ffprobe args (no select_streams)
                var args = $"-v error -show_entries stream=codec_type:stream_tags=language -of json \"{mediaFile}\"";

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;

                var json = await proc.StandardOutput.ReadToEndAsync();
                var errors = await proc.StandardError.ReadToEndAsync();

                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    if (enableLogging)
                        LoggingHelper.Log(true, $"ffprobe error for '{mediaFile}': {errors}");
                    return;
                }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    if (enableLogging)
                        LoggingHelper.Log(true, $"No streams found in '{mediaFile}'. JSON output: {json}");
                    return;
                }

                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecTypeElem))
                        continue;

                    var codecType = codecTypeElem.GetString();
                    if (string.IsNullOrEmpty(codecType))
                        continue;

                    if (stream.TryGetProperty("tags", out var tags) &&
                        tags.TryGetProperty("language", out var langProp))
                    {
                        var langCodeRaw = langProp.GetString();
                        if (string.IsNullOrEmpty(langCodeRaw))
                            continue;

                        var langCodeNormalized = LanguageHelper.NormalizeLangCode(langCodeRaw);

                        if (codecType == "audio")
                        {
                            audioLangs.Add(langCodeNormalized);
                            if (enableLogging)
                                LoggingHelper.Log(true, $"Detected embedded audio language '{langCodeNormalized}' in '{mediaFile}'.");
                        }
                        else if (codecType == "subtitle")
                        {
                            subtitleLangs.Add(langCodeNormalized);
                            if (enableLogging)
                                LoggingHelper.Log(true, $"Detected embedded subtitle language '{langCodeNormalized}' in '{mediaFile}'.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (enableLogging)
                    LoggingHelper.Log(true, $"MediaInfoDetector Exception for '{mediaFile}': {ex.Message}");
            }
        }
    }
}