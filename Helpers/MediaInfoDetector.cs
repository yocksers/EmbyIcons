using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    internal static class MediaInfoDetector
    {
        public static async Task DetectLanguagesFromMediaAsync(string mediaFile, HashSet<string> audioLangs, HashSet<string> subtitleLangs, bool enableLogging)
        {
            try
            {
                var args = $"-v error -show_streams -of json \"{mediaFile}\"";

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;

                var json = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        var codecType = s.GetProperty("codec_type").GetString();
                        if (codecType == "audio")
                        {
                            if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var langProp))
                            {
                                var codeRaw = langProp.GetString();
                                if (!string.IsNullOrEmpty(codeRaw))
                                {
                                    audioLangs.Add(LanguageHelper.NormalizeLangCode(codeRaw));
                                }
                            }
                        }
                        else if (codecType == "subtitle")
                        {
                            if (s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var langProp))
                            {
                                var codeRaw = langProp.GetString();
                                if (!string.IsNullOrEmpty(codeRaw))
                                {
                                    subtitleLangs.Add(LanguageHelper.NormalizeLangCode(codeRaw));
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore ffprobe failure silently or log if needed
            }
        }
    }
}