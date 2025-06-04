using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    // The main partial class for EmbyIconsEnhancer.
    // Other parts of this class are defined in EmbyIconsMetadataSupport.cs and EmbyIconsDataAggregator.cs
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        // SemaphoreSlims to prevent concurrent image processing for the same item.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ILibraryManager _libraryManager;
        private readonly IUserViewManager _userViewManager;
        internal readonly IconCacheManager _iconCacheManager;
        private readonly ILogger _logger; // Injected ILogger

        // Static string to hold the current version of the icon cache, updated by IconCacheManager.
        private static string _iconCacheVersion = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="EmbyIconsEnhancer"/> class.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="userViewManager">The user view manager.</param>
        /// <param name="logManager">The log manager for logging.</param>
        public EmbyIconsEnhancer(ILibraryManager libraryManager, IUserViewManager userViewManager, ILogManager logManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new InvalidOperationException("IUserViewManager not initialized");
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));
            // Initialize the IconCacheManager with a TTL and logger.
            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30), _logger, 4);
            // Subscribe to the CacheRefreshedWithVersion event to update the internal version string.
            _iconCacheManager.CacheRefreshedWithVersion += (sender, version) =>
            {
                _iconCacheVersion = version ?? string.Empty;
            };
        }

        /// <summary>
        /// Enhances an image asynchronously by overlaying icons. This is the public interface method.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="inputFile">The path to the input image file.</param>
        /// <param name="outputFile">The path where the enhanced image will be saved.</param>
        /// <param name="imageType">The type of the image being enhanced.</param>
        /// <param name="imageIndex">The index of the image (if multiple images of the same type exist).</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        Task IImageEnhancer.EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                              ImageType imageType, int imageIndex)
            => EnhanceImageAsync(item, inputFile, outputFile, imageType, CancellationToken.None);

        /// <summary>
        /// Enhances an image asynchronously by overlaying icons. This method acquires a lock per item.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="inputFile">The path to the input image file.</param>
        /// <param name="outputFile">The path where the enhanced image will be saved.</param>
        /// <param name="imageType">The type of the image being enhanced.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                            ImageType imageType,
                                            CancellationToken cancellationToken)
        {
            // Check if the library is allowed to have icons. If not, just copy the original file.
            if (!IsLibraryAllowed(item))
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            // Get or add a semaphore for the current item to prevent concurrent processing.
            var key = item.Id.ToString();
            var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken); // Acquire the lock

            try
            {
                // Call the internal enhancement logic.
                await EnhanceImageInternalAsync(item, inputFile, outputFile, imageType, cancellationToken);
            }
            finally
            {
                sem.Release(); // Release the lock
            }
        }

        /// <summary>
        /// Internal method to enhance an image by drawing overlays.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="inputFile">The path to the input image file.</param>
        /// <param name="outputFile">The path where the enhanced image will be saved.</param>
        /// <param name="imageType">The type of the image being enhanced.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        internal async Task EnhanceImageInternalAsync(
            BaseItem item,
            string inputFile,
            string outputFile,
            ImageType imageType,
            CancellationToken cancellationToken)
        {
            var options = Plugin.Instance?.GetConfiguredOptions();
            if (options == null)
            {
                _logger.Error("[EmbyIcons] Plugin options not initialized. Copying original image.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            // Check if any overlays should be drawn based on general plugin settings.
            if (!Helpers.IconDrawer.ShouldDrawAnyOverlays(item, options))
            {
                _logger.Debug($"[EmbyIcons] No overlays configured for item: {item?.Name} ({item?.Id}). Copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            // Validate input file.
            if (string.IsNullOrEmpty(inputFile) || !File.Exists(inputFile))
            {
                _logger.Warn($"[EmbyIcons] Input file for image enhancement is invalid or missing: '{inputFile}'. Item: {item?.Name} ({item?.Id}). Copying original instead.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            var inputInfo = new FileInfo(inputFile);
            if (inputInfo.Length < 100) // Basic check for potentially corrupt/empty image files.
            {
                _logger.Warn($"[EmbyIcons] Input file for image enhancement is too small or corrupt: '{inputFile}'. Skipping overlays and copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            HashSet<string> audioLangsDetected = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> subtitleLangsDetected = new(StringComparer.OrdinalIgnoreCase);
            int? detectedChannelCount = null;

            // Determine detected languages and channel count based on item type (series/season vs. individual item).
            if (item is MediaBrowser.Controller.Entities.TV.Series || item is MediaBrowser.Controller.Entities.TV.Season)
            {
                // For series/season, aggregate info from all episodes.
                var (audio, subtitle, channels) = await GetAggregatedInfoForSeriesAsync(item, options, cancellationToken);
                audioLangsDetected = audio;
                subtitleLangsDetected = subtitle;
                detectedChannelCount = channels;
            }
            else
            {
                // For individual items, get info directly from its media streams.
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                int maxChannelsForSingleItem = 0;
                foreach (var stream in streams)
                {
                    if (stream?.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = LanguageHelper.NormalizeLangCode(stream.Language);
                        audioLangsDetected.Add(norm);
                    }

                    if (stream?.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = LanguageHelper.NormalizeLangCode(stream.Language);
                        subtitleLangsDetected.Add(norm);
                    }

                    if (stream?.Type == MediaStreamType.Audio && stream.Channels.HasValue)
                    {
                        maxChannelsForSingleItem = Math.Max(maxChannelsForSingleItem, stream.Channels.Value);
                    }
                }
                if (maxChannelsForSingleItem > 0)
                {
                    detectedChannelCount = maxChannelsForSingleItem;
                }
            }

            cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation.

            // Map detected channel count to a specific icon name (e.g., "5.1", "stereo").
            string? channelIconName = null;
            if (options.ShowAudioChannelIcons && detectedChannelCount.HasValue)
            {
                if (detectedChannelCount.Value == 1) channelIconName = "mono";
                else if (detectedChannelCount.Value == 2) channelIconName = "stereo";
                else if (detectedChannelCount.Value == 6) channelIconName = "5.1";
                else if (detectedChannelCount.Value == 8) channelIconName = "7.1";
                else if (detectedChannelCount.Value > 0) channelIconName = $"{detectedChannelCount.Value}ch"; // Fallback for other channel counts.
            }

            // If no icons are enabled or detected, copy the original image and return.
            if (!options.ShowAudioIcons && !options.ShowSubtitleIcons && !options.ShowAudioChannelIcons)
            {
                _logger.Debug($"[EmbyIcons] All icon types disabled for item: {item?.Name} ({item?.Id}). Copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            if (audioLangsDetected.Count == 0 && subtitleLangsDetected.Count == 0 && channelIconName == null)
            {
                _logger.Debug($"[EmbyIcons] No relevant audio, subtitle, or channel info found for item: {item?.Name} ({item?.Id}). Copying original.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            // Load the input image using SkiaSharp.
            using var surfBmp = SKBitmap.Decode(inputFile);
            if (surfBmp == null)
            {
                _logger.Error($"[EmbyIcons] SKBitmap.Decode failed for input file: '{inputFile}'. Item: {item?.Name} ({item?.Id}). Copying original instead.");
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                return;
            }

            // Initialize the icon cache manager with the configured icons folder.
            await _iconCacheManager.InitializeAsync(options.IconsFolder!, cancellationToken);

            int width = surfBmp.Width;
            int height = surfBmp.Height;
            int shortSide = Math.Min(width, height);

            // Calculate icon size and padding based on plugin options and image dimensions.
            int iconSize = Math.Clamp((shortSide * options.IconSize) / 100, 8, 512);
            int padding = Math.Clamp(iconSize / 4, 2, 128);

            // Calculate vertical offsets in pixels for each icon type.
            int audioVerticalOffsetPx = (shortSide * options.AudioIconVerticalOffset) / 100;
            int subtitleVerticalOffsetPx = (shortSide * options.SubtitleIconVerticalOffset) / 100;
            int channelVerticalOffsetPx = (shortSide * options.ChannelIconVerticalOffset) / 100;

            // Create a new SkiaSharp surface to draw on.
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent); // Ensure transparency
            canvas.DrawBitmap(surfBmp, 0, 0); // Draw the original image.

            // Set up the paint for drawing icons (with or without smoothing).
            var filterQuality = options.EnableImageSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None;
            using var paint = new SKPaint { FilterQuality = filterQuality };

            // Dictionary to group icons by their alignment, along with their vertical offset and type order.
            // Item1: SKImage, Item2: verticalOffset, Item3: typeOrder (0=audio, 1=subtitle, 2=channel)
            var iconsToDrawByAlignment = new Dictionary<IconAlignment, List<(SKImage image, int verticalOffset, int typeOrder)>>();

            // Local helper function to add icons to the grouped dictionary.
            void AddIconsToAlignment(IconAlignment alignment, List<SKImage> icons, int verticalOffset, int typeOrder)
            {
                if (!iconsToDrawByAlignment.ContainsKey(alignment))
                {
                    iconsToDrawByAlignment[alignment] = new List<(SKImage image, int verticalOffset, int typeOrder)>();
                }
                foreach (var icon in icons)
                {
                    iconsToDrawByAlignment[alignment].Add((icon, verticalOffset, typeOrder));
                }
            }

            // Process and collect audio language icons.
            if (options.ShowAudioIcons && audioLangsDetected.Count > 0)
            {
                var audioIcons = audioLangsDetected.OrderBy(l => l)
                    .Select(lang => _iconCacheManager.GetCachedIcon(lang, false))
                    .Where(i => i != null)
                    .ToList();
                if (audioIcons.Any())
                {
                    AddIconsToAlignment(options.AudioIconAlignment, audioIcons!, audioVerticalOffsetPx, 0); // Type order 0 for audio.
                }
            }

            // Process and collect subtitle icons.
            if (options.ShowSubtitleIcons && subtitleLangsDetected.Count > 0)
            {
                var subIcons = subtitleLangsDetected.OrderBy(l => l)
                    .Select(lang => _iconCacheManager.GetCachedIcon($"srt.{lang}", true))
                    .Where(i => i != null)
                    .ToList();
                if (subIcons.Any())
                {
                    AddIconsToAlignment(options.SubtitleIconAlignment, subIcons!, subtitleVerticalOffsetPx, 1); // Type order 1 for subtitles.
                }
            }

            // Process and collect audio channel icons.
            if (options.ShowAudioChannelIcons && channelIconName != null)
            {
                var icon = _iconCacheManager.GetCachedIcon(channelIconName, false);
                if (icon != null)
                {
                    AddIconsToAlignment(options.ChannelIconAlignment, new List<SKImage> { icon }, channelVerticalOffsetPx, 2); // Type order 2 for channels.
                }
            }

            // Iterate through the grouped icons by alignment and draw them.
            foreach (var entry in iconsToDrawByAlignment)
            {
                var alignment = entry.Key;
                var iconsWithOrder = entry.Value;

                // Sort icons based on the specified order (audio, then subtitle, then channel) within each alignment group.
                var sortedIcons = iconsWithOrder.OrderBy(x => x.typeOrder).Select(x => x.image).ToList();

                // Determine the vertical offset for this group. Use the offset of the first icon type in the sorted list.
                // This ensures a consistent baseline for side-by-side icons.
                int groupVerticalOffset = iconsWithOrder.OrderBy(x => x.typeOrder).Select(x => x.verticalOffset).FirstOrDefault();

                if (sortedIcons.Any())
                {
                    Helpers.IconDrawer.DrawIcons(canvas, sortedIcons, iconSize, padding, width, height, alignment, paint, groupVerticalOffset);
                }
            }

            canvas.Flush(); // Ensure all drawing operations are complete.

            // Encode and save the enhanced image to the output file.
            try
            {
                using var snapshot = surface.Snapshot();
                int jpegQuality = Math.Clamp(options.JpegQuality, 10, 100);
                using var encodedImg = snapshot.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);

                int fileOpRetries = 0;
                int fileOpDelayMs = 100;
                const int maxFileOpRetries = 5;

                // Retry loop for file saving to handle potential IO issues.
                while (true)
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(outputFile);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir); // Ensure output directory exists.

                        string tempOutput = outputFile + "." + Guid.NewGuid() + ".tmp"; // Use a temporary file for atomic write.

                        using (var fsOut = File.Create(tempOutput))
                        {
                            await encodedImg.AsStream().CopyToAsync(fsOut);
                            await fsOut.FlushAsync();
                        }

                        File.Move(tempOutput, outputFile, overwrite: true); // Atomically replace the original.
                        break; // Save successful, exit loop.
                    }
                    catch (IOException ioEx)
                    {
                        fileOpRetries++;
                        if (fileOpRetries >= maxFileOpRetries)
                        {
                            _logger.ErrorException($"[EmbyIcons] Failed to save output image '{outputFile}' due to IO error after {maxFileOpRetries} retries. Item: {item?.Name} ({item?.Id}). Copying original as fallback.", ioEx);
                            await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                            return;
                        }
                        _logger.Warn($"[EmbyIcons] Retrying image save for '{outputFile}' due to IO error. Retry {fileOpRetries}/{maxFileOpRetries}. Error: {ioEx.Message}");
                        await Task.Delay(fileOpDelayMs, cancellationToken);
                        fileOpDelayMs = Math.Min(5000, fileOpDelayMs * 2); // Exponential backoff.
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"[EmbyIcons] Unexpected critical error during image encoding or file saving for item: {item?.Name} ({item?.Id}). Copying original as fallback.", ex);
                        await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Unhandled critical error during image enhancement for item: {item?.Name} ({item?.Id}). Copying original as fallback.", ex);
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile, _logger);
            }
        }

        /// <summary>
        /// Disposes managed resources.
        /// </summary>
        public void Dispose()
        {
            // Dispose all semaphores in the _locks dictionary.
            foreach (var sem in _locks.Values)
                sem.Dispose();

            _locks.Clear(); // Clear the dictionary.
            _iconCacheManager.Dispose(); // Dispose the IconCacheManager.
            // The item media stream hash cache does not hold disposable resources, so just clear it.
            _itemMediaStreamHashCache.Clear();
        }
    }
}
