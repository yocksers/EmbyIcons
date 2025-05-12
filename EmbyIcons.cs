using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using SkiaSharp;
using System.Collections.Generic;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace EmbyIcons
{
    public class EmbyIconsEnhancer : IImageEnhancer
    {
        private readonly ILibraryManager _libraryManager;

        private static readonly Dictionary<string, string> LangCodeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "da", "dan" },
            { "en", "eng" },
            { "fr", "fre" },
            { "de", "ger" },
            { "es", "spa" },
            { "pl", "pol" },
            { "jp", "jpn" },
        };

        public EmbyIconsEnhancer(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        /// <summary>
        /// Only support main media items, exclude person (actor) posters.
        /// </summary>
        public bool Supports(BaseItem item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary)
                return false;

            if (item is Person)
                return false;

            return item is Movie || item is Episode || item is Series || item is Season || item is BoxSet || item is MusicVideo;
        }

        /// <summary>
        /// Parses comma-separated language codes into a HashSet.
        /// </summary>
        private HashSet<string> ParseLanguageList(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim().ToLowerInvariant())
                      .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var options = Plugin.Instance.GetConfiguredOptions();
            var libsKey = (options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", "");
            return $"embyicons_{item.Id}_{imageType}_scale{options.IconSize}_libs{libsKey}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            new() { RequiresTransparency = true };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            originalSize;

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            var options = Plugin.Instance.GetConfiguredOptions();
            var allowedLibraryIds = GetAllowedLibraryIds(options.SelectedLibraries);

            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                Log(options.EnableLogging, $"Input missing or invalid for '{item.Name}', copying original");
                File.Copy(inputFile, outputFile, true);
                return;
            }

            var libraryId = GetLibraryIdForItem(item);

            if (allowedLibraryIds.Count > 0 && (libraryId == null || !allowedLibraryIds.Contains(libraryId)))
            {
                Log(options.EnableLogging, $"Skipping item '{item.Name}' due to library restriction.");
                File.Copy(inputFile, outputFile, true);
                return;
            }

            var audioLangsAllowed = ParseLanguageList(options.AudioLanguages);
            var subtitleLangsAllowed = ParseLanguageList(options.SubtitleLanguages);

            var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Detect languages using ffprobe if possible
            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) &&
                new[] { ".mkv", ".mp4", ".avi", ".mov" }.Contains(Path.GetExtension(item.Path).ToLowerInvariant()))
            {
                await DetectLanguagesFromMediaAsync(item.Path, audioLangs, subtitleLangs, options.EnableLogging);
            }

            // Detect external subtitles
            ScanExternalSubtitles(item.Path ?? inputFile, subtitleLangs, options.EnableLogging);

            // Filter by allowed languages
            audioLangs.IntersectWith(audioLangsAllowed);
            subtitleLangs.IntersectWith(subtitleLangsAllowed);

            if (!options.ShowAudioIcons || audioLangs.Count == 0)
                audioLangs.Clear();

            if (!options.ShowSubtitleIcons || subtitleLangs.Count == 0)
                subtitleLangs.Clear();

            if (audioLangs.Count == 0 && subtitleLangs.Count == 0)
            {
                Log(options.EnableLogging, $"No icons to draw for '{item.Name}', copying original");
                File.Copy(inputFile, outputFile, true);
                return;
            }

            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                Log(true, $"Failed to decode original image for '{item.Name}', copying original");
                File.Copy(inputFile, outputFile, true);
                return;
            }

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);
            int iconSize = Math.Max(16, (shortSide * options.IconSize) / 100);
            int padding = Math.Max(4, iconSize / 4);

            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;

            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(surfBmp, 0, 0);

            if (audioLangs.Count > 0)
                DrawIcons(canvas,
                          audioLangs.OrderBy(l => l).Select(l => Path.Combine(options.IconsFolder ?? @"D:\icons", $"{l}.png")).Where(File.Exists).ToList(),
                          iconSize,
                          padding,
                          width,
                          height,
                          options.AudioIconAlignment);

            if (subtitleLangs.Count > 0)
                DrawIcons(canvas,
                          subtitleLangs.OrderBy(l => l).Select(l => Path.Combine(options.IconsFolder ?? @"D:\icons", $"srt.{l}.png")).Where(File.Exists).ToList(),
                          iconSize,
                          padding,
                          width,
                          height,
                          options.SubtitleIconAlignment);

            canvas.Flush();

            using var image = surface.Snapshot();
            using var encodedImg = image.Encode(SKEncodedImageFormat.Png, 100);

            Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? throw new Exception("Invalid output path"));

            using var fsOut = File.OpenWrite(outputFile);
            encodedImg.AsStream().CopyTo(fsOut);

            Log(options.EnableLogging, $"Saved enhanced image to '{outputFile}'");
        }

        private async Task DetectLanguagesFromMediaAsync(string mediaFile, HashSet<string> audioLangs, HashSet<string> subtitleLangs, bool enableLogging)
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

                Log(enableLogging, "ffprobe output received");

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
                                    audioLangs.Add(NormalizeLangCode(codeRaw));
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
                                    subtitleLangs.Add(NormalizeLangCode(codeRaw));
                                }
                            }
                        }
                    }
                }
                Log(enableLogging, $"Detected audio langs: {string.Join(',', audioLangs)}");
                Log(enableLogging, $"Detected subtitle langs: {string.Join(',', subtitleLangs)}");
            }
            catch (Exception ex)
            {
                LogAlways($"ffprobe failed: {ex.Message}");
            }
        }

        private void ScanExternalSubtitles(string? mediaOrInputPath, HashSet<string> subtitleLangs, bool enableLogging)
        {
            try
            {
                if (string.IsNullOrEmpty(mediaOrInputPath))
                {
                    Log(enableLogging, "Media path is empty, skipping subtitle scan");
                    return;
                }

                var folderPath = Path.GetDirectoryName(mediaOrInputPath);

                if (string.IsNullOrEmpty(folderPath))
                {
                    Log(enableLogging, "Could not determine folder path, skipping subtitle scan");
                    return;
                }

                if (!Directory.Exists(folderPath))
                {
                    Log(enableLogging, $"Folder does not exist: {folderPath}, skipping subtitle scan");
                    return;
                }

                var srtFiles = Directory.GetFiles(folderPath, "*.srt");

                foreach (var srt in srtFiles)
                {
                    var fileName = Path.GetFileNameWithoutExtension(srt).ToLowerInvariant();
                    string? langCode = null;

                    var parts = fileName.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length > 1)
                    {
                        var candidate = parts[^1];

                        if (candidate.Length >= 2 && candidate.Length <= 3)
                        {
                            langCode = NormalizeLangCode(candidate);
                        }
                    }

                    if (!string.IsNullOrEmpty(langCode) && !subtitleLangs.Contains(langCode))
                    {
                        subtitleLangs.Add(langCode);
                        Log(enableLogging, $"Detected external subtitle language: {langCode} from file {srt}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogAlways($"External subtitles scan failed: {ex.Message}");
            }
        }

        private void DrawIcons(SKCanvas canvas, List<string> icons, int size, int pad, int width, int height, IconAlignment alignment)
        {
            int count = icons.Count;

            if (count == 0) return;

            int totalWidth = count * size + (count - 1) * pad;

            float startX = (alignment == IconAlignment.TopRight || alignment == IconAlignment.BottomRight) ? width - totalWidth - pad : pad;

            float startY = (alignment == IconAlignment.BottomLeft || alignment == IconAlignment.BottomRight) ? height - size - pad : pad;

            for (int i = 0; i < count; i++)
            {
                using var bmpIcon = SKBitmap.Decode(icons[i]);

                if (bmpIcon == null) continue;

                float xPos = startX + i * (size + pad);

                canvas.DrawBitmap(bmpIcon, bmpIcon.Info.Rect, new SKRect(xPos, startY, xPos + size, startY + size));

                Log(true, $"Drew icon '{Path.GetFileName(icons[i])}' at {alignment} with size {size}px");
            }
        }

        private string NormalizeLangCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code!;

            return LangCodeMap.TryGetValue(code.ToLowerInvariant(), out var mapped) ? mapped.ToLowerInvariant() : code.ToLowerInvariant();
        }

        private HashSet<string> GetAllowedLibraryIds(string? selectedLibrariesCsv)
        {
            var allowedIds = new HashSet<string>();

            if (string.IsNullOrWhiteSpace(selectedLibrariesCsv))
                return allowedIds; // empty means no restriction

            var selectedNames = selectedLibrariesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var lib in _libraryManager.GetVirtualFolders())
            {
                if (selectedNames.Contains(lib.Name))
                    allowedIds.Add(lib.Id);
            }

            return allowedIds;
        }

        private string? GetLibraryIdForItem(BaseItem item)
        {
            if (item == null) return null;

            string? itemPath = item.Path;

            if (string.IsNullOrEmpty(itemPath)) return null;

            foreach (var lib in _libraryManager.GetVirtualFolders())
            {
                if (lib.Locations != null)
                {
                    foreach (var loc in lib.Locations)
                    {
                        if (!string.IsNullOrEmpty(loc) && itemPath.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                        {
                            return lib.Id;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Logs message only if logging enabled OR alwaysWhenTrue is true.
        /// </summary>
        private void Log(bool loggingEnabledOrAlwaysWhenTrue, string message)
        {
            if (!loggingEnabledOrAlwaysWhenTrue) return;

            try
            {
                string logPath = Path.Combine((Plugin.Instance?.GetConfiguredOptions()?.LogFolder) ?? @"C:\temp", "EmbyIcons.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch
            {
                // ignore logging errors
            }
        }

        /// <summary>
        /// Logs message always regardless of option setting.
        /// </summary>
        private void LogAlways(string message)
        {
            try
            {
                string logPath = Path.Combine((Plugin.Instance?.GetConfiguredOptions()?.LogFolder) ?? @"C:\temp", "EmbyIcons.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
            catch
            {
                // ignore logging errors
            }
        }
    }
}
