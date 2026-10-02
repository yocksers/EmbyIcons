using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Tasks
{
    public class PreDrawPostersTask : IScheduledTask
    {
        private const int PageSize = 500;
        private const int MaxPendingTasks = 256;
        private const int DefaultQuality = 90;

        private static readonly string[] ItemTypesToDraw =
        {
            "Movie", "Series", "Season", "Episode", "BoxSet", "Video", "MusicVideo", "MusicAlbum", "MusicArtist", "Audio", "Photo"
        };

        private static readonly ImageType[] ImageTypesToDraw = { ImageType.Primary, ImageType.Thumb, ImageType.Banner };

        private readonly ILibraryManager _libraryManager;
        private readonly IImageProcessor _imageProcessor;
        private readonly ILogger _logger;

        public PreDrawPostersTask(ILibraryManager libraryManager, IImageProcessor imageProcessor, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _imageProcessor = imageProcessor;
            _logger = logManager.GetLogger(nameof(PreDrawPostersTask));
        }

        public string Name => "Pre-draw posters";

        public string Key => "EmbyIconsPreDrawPosters";

        public string Description => "Draws EmbyIcons overlays ahead of time onto every image your EmbyIcons settings and profiles draw on, so browsing and searching never have to wait for them or read the original artwork. Images that are already drawn are skipped.";

        public string Category => "EmbyIcons";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                _logger.Warn("[EmbyIcons] Pre-draw posters skipped: the plugin is not loaded.");
                return;
            }

            var config = plugin.Configuration;
            var queries = BuildQueries(config, new HashSet<string>(ItemTypesToDraw, StringComparer.OrdinalIgnoreCase), out var scope);

            if (queries.Count == 0)
            {
                _logger.Info("[EmbyIcons] Pre-draw posters skipped: no libraries have an EmbyIcons profile.");
                progress.Report(100);
                return;
            }

            PluginHelper.SuppressDebugLoggingForCurrentFlow();

            var timer = Stopwatch.StartNew();
            var drawnBefore = EmbyIconsEnhancer.EnhanceCallCount;
            var (workMsBefore, timedCountBefore) = EmbyIconsEnhancer.EnhanceTiming;
            var parallelism = Math.Max(1, Environment.ProcessorCount / 2);

            int totalItems = 0;
            int processedItems = 0;
            int imagesChecked = 0;
            int failures = 0;

            _logger.Info($"[EmbyIcons] Pre-draw posters started in {scope} (up to {parallelism} images at a time).");

            using var throttle = new SemaphoreSlim(parallelism, parallelism);
            var pending = new List<Task>();

            async Task ProcessItemAsync(BaseItem item)
            {
                try
                {
                    var (checkedForItem, failedForItem) = await WarmItemAsync(item, cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref imagesChecked, checkedForItem);
                    Interlocked.Add(ref failures, failedForItem);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failures);
                    _logger.Warn($"[EmbyIcons] Pre-draw failed for '{item.Name}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                    var done = Interlocked.Increment(ref processedItems);
                    var total = Volatile.Read(ref totalItems);
                    if (total > 0)
                    {
                        progress.Report(Math.Min(100.0, done * 100.0 / total));
                    }
                }
            }

            try
            {
                foreach (var createQuery in queries)
                {
                    foreach (var item in LibraryItemPager.EnumerateAll(_libraryManager, createQuery, PageSize, total => Interlocked.Add(ref totalItems, total)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                        pending.Add(Task.Run(() => ProcessItemAsync(item)));

                        if (pending.Count >= MaxPendingTasks)
                        {
                            pending.RemoveAll(t => t.IsCompleted);
                        }
                    }
                }
            }
            finally
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100);

            var drawn = EmbyIconsEnhancer.EnhanceCallCount - drawnBefore;
            var (workMsAfter, timedCountAfter) = EmbyIconsEnhancer.EnhanceTiming;
            var timedCount = timedCountAfter - timedCountBefore;
            var timing = timedCount > 0
                ? $" EmbyIcons drawing averaged {(workMsAfter - workMsBefore) / timedCount} ms per image."
                : string.Empty;

            _logger.Info($"[EmbyIcons] Pre-draw posters finished in {timer.Elapsed:hh\\:mm\\:ss}: checked {imagesChecked} images on {processedItems} items, drew {drawn}, failed {failures}.{timing}");
        }

        private List<Func<InternalItemsQuery>> BuildQueries(PluginOptions config, HashSet<string> itemTypes, out string scope)
        {
            var queries = new List<Func<InternalItemsQuery>>();
            var libraryFolderIds = GetProfileLibraryFolderIds(config);

            if (libraryFolderIds == null)
            {
                var allTypes = itemTypes.ToArray();
                queries.Add(() => new InternalItemsQuery
                {
                    IncludeItemTypes = allTypes,
                    IsVirtualItem = false,
                    Recursive = true
                });
                scope = "all libraries";
                return queries;
            }

            var libraryTypes = itemTypes.Where(t => !t.Equals("BoxSet", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (libraryFolderIds.Length > 0 && libraryTypes.Length > 0)
            {
                queries.Add(() => new InternalItemsQuery
                {
                    IncludeItemTypes = libraryTypes,
                    IsVirtualItem = false,
                    Recursive = true,
                    AncestorIds = libraryFolderIds
                });
            }

            if (itemTypes.Contains("BoxSet"))
            {
                queries.Add(() => new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "BoxSet" },
                    IsVirtualItem = false,
                    Recursive = true
                });
            }

            scope = libraryFolderIds.Length == 1 ? "1 library with a profile" : $"{libraryFolderIds.Length} libraries with a profile";
            return queries;
        }

        private long[]? GetProfileLibraryFolderIds(PluginOptions config)
        {
            var profileIds = new HashSet<Guid>((config.Profiles ?? new List<IconProfile>()).Select(p => p.Id));
            var mappedLibraryIds = new HashSet<string>(
                (config.LibraryProfileMappings ?? new List<LibraryMapping>())
                    .Where(m => !string.IsNullOrEmpty(m.LibraryId) && profileIds.Contains(m.ProfileId))
                    .Select(m => m.LibraryId),
                StringComparer.OrdinalIgnoreCase);

            if (mappedLibraryIds.Count == 0) return Array.Empty<long>();

            var folderIds = new List<long>();
            try
            {
                foreach (var folder in _libraryManager.GetVirtualFolders())
                {
                    if (folder == null || string.IsNullOrEmpty(folder.Id) || !mappedLibraryIds.Contains(folder.Id)) continue;

                    var folderItem = ResolveItem(folder.ItemId);
                    if (folderItem == null || folderItem.InternalId <= 0) return null;

                    folderIds.Add(folderItem.InternalId);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EmbyIcons] Pre-draw posters could not resolve the libraries with a profile, so all libraries will be checked: {ex.Message}");
                return null;
            }

            return folderIds.ToArray();
        }

        private BaseItem? ResolveItem(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (long.TryParse(id, out var internalId)) return _libraryManager.GetItemById(internalId);
            if (Guid.TryParse(id, out var guid)) return _libraryManager.GetItemById(guid);
            return null;
        }

        private async Task<(int Checked, int Failed)> WarmItemAsync(BaseItem item, CancellationToken cancellationToken)
        {
            int imagesChecked = 0;
            int imagesFailed = 0;

            foreach (var imageType in ImageTypesToDraw)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imageInfo = item.GetImageInfo(imageType, 0);
                if (imageInfo == null || string.IsNullOrEmpty(imageInfo.Path)) continue;

                var enhancers = _imageProcessor.GetSupportedEnhancers(item, imageType);
                if (enhancers == null || !enhancers.Any(e => e is EmbyIconsEnhancer)) continue;

                var options = new ImageProcessingOptions
                {
                    Item = item,
                    ItemId = item.InternalId,
                    Image = imageInfo,
                    ImageIndex = 0,
                    Enhancers = enhancers,
                    SupportedOutputFormats = _imageProcessor.GetSupportedImageOutputFormats(),
                    RequiresAutoOrientation = true,
                    Quality = DefaultQuality
                };

                var result = await _imageProcessor.ProcessImage(options, cancellationToken).ConfigureAwait(false);
                var resultPath = result?.Item1;

                imagesChecked++;

                if (string.IsNullOrEmpty(resultPath) || string.Equals(resultPath, imageInfo.Path, StringComparison.OrdinalIgnoreCase))
                {
                    imagesFailed++;
                    var imageName = imageType == ImageType.Primary ? "poster" : imageType.ToString().ToLowerInvariant();
                    var hint = enhancers.Length > 1
                        ? "Another image plugin may have failed on it; check the log for 'Error enhancing image'."
                        : "Check the log for 'Error enhancing image'.";
                    _logger.Warn($"[EmbyIcons] Pre-draw: Emby could not enhance the {imageName} of '{item.Name}' ({imageInfo.Path}). {hint}");
                }
            }

            return (imagesChecked, imagesFailed);
        }
    }
}
