using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using EmbyIcons.Services;
using EmbyIcons.ImageProcessing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Querying;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lockLastUsed = new();
        private static volatile Timer? _lockCleanupTimer;
        private static readonly object _lockCleanupInitLock = new object();
        private const int LockDictionaryMaxSize = 10000;
        
        private readonly ILibraryManager _libraryManager;
        internal readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly RenderBackend _activeBackend;
        private readonly EmbyIcons.ImageProcessing.Vips.NetVipsIconCacheManager? _netVipsIconCacheManager;

        internal static int ItemLockCount => _locks.Count;

        internal long IconCacheEstimatedBytes => _iconCacheManager.EstimatedCacheBytes + (_netVipsIconCacheManager?.EstimatedCacheBytes ?? 0);
        private readonly EmbyIcons.ImageProcessing.Vips.NetVipsImageOverlayService? _netVipsOverlayService;

        private volatile SemaphoreSlim? _globalConcurrencyLock;
        private readonly object _lockInitializationLock = new object();

        private SemaphoreSlim GlobalConcurrencyLock
        {
            get
            {
                if (_globalConcurrencyLock == null)
                {
                    lock (_lockInitializationLock)
                    {
                        if (_globalConcurrencyLock == null)
                        {
                            var multiplier = EmbyIcons.Compat.MathCompat.Clamp(Plugin.Instance?.Configuration.GlobalConcurrencyMultiplier ?? 0.75, 0.1, 2.0);
                            var maxConcurrency = Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * multiplier));
                            _globalConcurrencyLock = new SemaphoreSlim(maxConcurrency, maxConcurrency);
                        }
                    }
                }
                return _globalConcurrencyLock;
            }
        }

        private MediaBrowser.Controller.Library.IUserDataManager? _userDataManager;
        internal readonly IconCacheManager _iconCacheManager;
        internal static readonly ConcurrentDictionary<Guid, AggregatedSeriesResult> _seriesAggregationCache = new();

        internal readonly OverlayDataService _overlayDataService;

        private const int FavoriteCountCacheSize = 20000;
        private MemoryCache _favoriteCountCache = CreateFavoriteCountCache();

        private static MemoryCache CreateFavoriteCountCache() => new MemoryCache(new MemoryCacheOptions { SizeLimit = FavoriteCountCacheSize });
        private readonly ImageOverlayService _imageOverlayService;

        private static readonly TimeSpan SeriesAggregationPruneInterval = TimeSpan.FromDays(7);
        public ILogger Logger => _logger;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, ILogManager logManager, IFileSystem fileSystem, IImageProcessor imageProcessor)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

            _logger.Info("[EmbyIcons] Session started.");

            _activeBackend = ImageProcessingCapabilities.GetActiveBackend(_logger, imageProcessor);
            switch (_activeBackend)
            {
                case RenderBackend.Skia:
                    _logger.Info("[EmbyIcons] SkiaSharp is available. Icon overlays will be applied.");
                    break;
                case RenderBackend.NetVips:
                    _logger.Info("[EmbyIcons] SkiaSharp is unavailable, but NetVips (libvips) is active. Icon overlays will use the NetVips renderer.");
                    _netVipsIconCacheManager = new EmbyIcons.ImageProcessing.Vips.NetVipsIconCacheManager(_logger);
                    _netVipsOverlayService = new EmbyIcons.ImageProcessing.Vips.NetVipsImageOverlayService(_logger, _netVipsIconCacheManager);
                    break;
                default:
                    _logger.Warn("[EmbyIcons] Neither SkiaSharp nor NetVips is available. Icon overlays will be disabled.");
                    _logger.Warn("[EmbyIcons] To enable icon overlays, ensure SkiaSharp or libvips native libraries are installed for your platform.");
                    break;
            }

            _iconCacheManager = new IconCacheManager(_logger);
            _overlayDataService = new OverlayDataService(this, _libraryManager);
            _imageOverlayService = new ImageOverlayService(_logger, _iconCacheManager);
            
            if (_lockCleanupTimer == null)
            {
                lock (_lockCleanupInitLock)
                {
                    if (_lockCleanupTimer == null)
                    {
                        _lockCleanupTimer = new Timer(_ => CleanupUnusedLocks(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
                    }
                }
            }
        }
        
        private static void CleanupUnusedLocks()
        {
            try
            {
                var now = DateTime.UtcNow;
                var staleThreshold = _locks.Count > LockDictionaryMaxSize
                    ? TimeSpan.FromMinutes(1)
                    : TimeSpan.FromMinutes(15);
                var keysToRemove = _lockLastUsed
                    .Where(kvp => now - kvp.Value > staleThreshold)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    if (_locks.TryGetValue(key, out var semToCheck) && semToCheck.CurrentCount == 0)
                    {
                        _lockLastUsed[key] = DateTime.UtcNow;
                        continue;
                    }
                    if (_locks.TryRemove(key, out var removedSem))
                    {
                        try { removedSem.Dispose(); } catch { }
                    }
                    _lockLastUsed.TryRemove(key, out _);
                }

                if (keysToRemove.Count > 0 && Helpers.PluginHelper.IsDebugLoggingEnabled)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Cleaned up {keysToRemove.Count} unused item locks. Remaining: {_locks.Count}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger.Debug($"[EmbyIcons] Error during lock cleanup: {ex.Message}");
            }
        }
        public void ForceCacheRefresh(string iconsFolder)
        {
            _logger.Info($"[EmbyIcons] Forcing full cache refresh for folder: '{iconsFolder}'");
            ClearAllItemDataCaches();
            ClearItemStatisticsCaches();
            RefreshIconCaches(iconsFolder);
        }

        internal void ClearItemStatisticsCaches()
        {
            ClearStreamHashCache();

            var oldFavoriteCache = Interlocked.Exchange(ref _favoriteCountCache, CreateFavoriteCountCache());
            try { oldFavoriteCache.Dispose(); } catch { }
        }

        internal void RefreshIconCaches(string iconsFolder)
        {
            _iconCacheManager.RefreshCache(iconsFolder);
            _netVipsIconCacheManager?.RefreshCache(iconsFolder);
        }

        public void ClearAllItemDataCaches()
        {
            _seriesAggregationCache.Clear();
            _albumAggregationCache.Clear();
            ClearAllEpisodeCaches();
            _logger.Info("[EmbyIcons] Cleared all series, album, and episode data caches.");
        }

        public void InvalidateMovieProviderPathCache(BaseItem item)
        {
            _overlayDataService.InvalidateProviderPathCacheForItem(item);
        }

        public void ForceSeriesRefresh(Guid seriesId)
        {
            ClearSeriesAggregationCache(seriesId);

            if (seriesId == Guid.Empty)
            {
                _logger.Warn("[EmbyIcons] Attempted to force refresh for an empty series ID. Skipping episode cache clear.");
                return;
            }

            var seriesItem = _libraryManager.GetItemById(seriesId);
            if (seriesItem == null)
            {
                _logger.Warn($"[EmbyIcons] Could not find series with ID '{seriesId}' for ForceSeriesRefresh. Skipping episode cache clear.");
                return;
            }

            const int MAX_EPISODES_TO_CLEAR = 10000;

            var episodesInSeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { seriesItem.InternalId },
                IncludeItemTypes = new[] { Constants.Episode },
                Recursive = true,
                Limit = MAX_EPISODES_TO_CLEAR
            }).Select(ep => ep.Id).ToList();

            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
            {
                _logger.Debug($"[EmbyIcons] ForceSeriesRefresh identified {episodesInSeries.Count} episodes for series '{seriesId}' to clear from cache.");
            }

            foreach (var episodeId in episodesInSeries)
            {
                ClearEpisodeIconCache(episodeId);
            }
        }

        private BaseItem GetFullItem(BaseItem item)
        {
            if (item.Id == Guid.Empty && item.InternalId > 0)
            {
                var fullItem = _libraryManager.GetItemById(item.InternalId);
                return fullItem ?? item;
            }
            return item;
        }

        public void PruneSeriesAggregationCache()
        {
            var now = DateTime.UtcNow;
            int removed = 0;
            var entriesToRemove = _seriesAggregationCache
                .Where(kvp => now - kvp.Value.Timestamp > SeriesAggregationPruneInterval)
                .Select(kvp => kvp.Key)
                .ToArray();
                
            foreach (var key in entriesToRemove)
            {
                if (_seriesAggregationCache.TryRemove(key, out _)) 
                    removed++;
            }
            
            if (removed > 0 && (Plugin.Instance?.Configuration.EnableDebugLogging ?? false))
                _logger.Debug($"[EmbyIcons] Pruned {removed} stale entries from the series overlay aggregation cache.");
        }

        public void ClearSeriesAggregationCache(Guid seriesId)
        {
            if (seriesId != Guid.Empty && _seriesAggregationCache.TryRemove(seriesId, out _))
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    _logger.Debug($"[EmbyIcons] Event handler cleared aggregation cache for series ID: {seriesId}");
            }
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null) return false;

            if (item.Id == Guid.Empty && item.InternalId == 0) return false;

            if (_activeBackend == RenderBackend.None)
            {
                return false;
            }

            var profile = Plugin.Instance?.GetProfileForItem(item);
            if (profile == null) return false;

            var options = profile.Settings;

            bool isTypeSupported = imageType switch
            {
                ImageType.Primary => options.EnableForPosters,
                ImageType.Thumb => options.EnableForThumbs,
                ImageType.Banner => options.EnableForBanners,
                _ => false
            };

            if (!isTypeSupported) return false;

            bool isSupportedType = item is Video || item is Series || item is Season || item is Photo || item is BoxSet || item is Audio || item is MusicAlbum || item is MusicArtist;
            if (!isSupportedType) return false;

            if (item is Episode && !(options.ShowOverlaysForEpisodes)) return false;
            if (item is Season && !(options.ShowOverlaysForSeasons)) return false;
            if (item is Series && !options.UseSeriesLiteMode && !options.ShowSeriesIconsIfAllEpisodesHaveLanguage) return false;
            if (item is BoxSet && !options.UseCollectionLiteMode && !options.ShowCollectionIconsIfAllChildrenHaveLanguage) return false;

            return options.AudioIconAlignment != IconAlignment.Disabled ||
                   options.SubtitleIconAlignment != IconAlignment.Disabled ||
                   options.ChannelIconAlignment != IconAlignment.Disabled ||
                   options.VideoFormatIconAlignment != IconAlignment.Disabled ||
                   options.ResolutionIconAlignment != IconAlignment.Disabled ||
                   options.CommunityScoreIconAlignment != IconAlignment.Disabled ||
                   options.AudioCodecIconAlignment != IconAlignment.Disabled ||
                   options.VideoCodecIconAlignment != IconAlignment.Disabled ||
                   options.TagIconAlignment != IconAlignment.Disabled ||
                   options.AspectRatioIconAlignment != IconAlignment.Disabled ||
                   options.ParentalRatingIconAlignment != IconAlignment.Disabled ||
                   options.SourceIconAlignment != IconAlignment.Disabled ||
                   options.FavoriteCountIconAlignment != IconAlignment.Disabled ||
                   options.FrameRateIconAlignment != IconAlignment.Disabled ||
                   options.OriginalLanguageIconAlignment != IconAlignment.Disabled ||
                   options.SeriesStatusIconAlignment != IconAlignment.Disabled ||
                   options.RottenTomatoesScoreIconAlignment != IconAlignment.Disabled ||
                   options.PopcornScoreIconAlignment != IconAlignment.Disabled ||
                   options.MyAnimeListScoreIconAlignment != IconAlignment.Disabled ||
                   options.SampleRateIconAlignment != IconAlignment.Disabled ||
                   options.AudioBitRateIconAlignment != IconAlignment.Disabled ||
                   options.BitDepthIconAlignment != IconAlignment.Disabled ||
                   options.FilenameBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled) ||
                   options.TagBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled);
        }

        private static string SanitizeTagForKey(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            var buf = new char[tag.Length];
            int j = 0;
            bool lastDash = false;
            for (int i = 0; i < tag.Length; i++)
            {
                char c = char.ToLowerInvariant(tag[i]);
                if (char.IsWhiteSpace(c))
                {
                    if (!lastDash && j > 0)
                    {
                        buf[j++] = '-';
                        lastDash = true;
                    }
                    continue;
                }
                lastDash = false;
                buf[j++] = c;
            }
            int start = 0;
            while (start < j && buf[start] == '-') start++;
            int end = j - 1;
            while (end >= start && buf[end] == '-') end--;
            return (start > end) ? string.Empty : new string(buf, start, end - start + 1);
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return $"ei_np_{item.InternalId}";

            var profile = plugin.GetProfileForItem(item);
            if (profile == null) return $"ei_np_{item.InternalId}";

            if (item.Id == Guid.Empty && item.InternalId > 0)
                item = _libraryManager.GetItemById(item.InternalId) ?? item;

            var globalOptions = plugin.GetConfiguredOptions();
            var options = profile.Settings;
            var sb = new StringBuilder(512);

            var itemIdSegment = item.Id != Guid.Empty
                ? Convert.ToBase64String(item.Id.ToByteArray()).TrimEnd('=')
                : $"iid_{item.InternalId}";

            sb.Append("ei9_")
              .Append(itemIdSegment)
              .Append('_').Append((int)imageType)
              .Append('v').Append(plugin.GetRenderFingerprint(profile))
              .Append('p').Append(Convert.ToBase64String(profile.Id.ToByteArray()).TrimEnd('='));

            if (item is Series series)
            {
                var fullSeries = GetFullItem(series) as Series ?? series;
                var aggResult = GetOrBuildAggregatedDataForParent(fullSeries, options, globalOptions);
                sb.Append("_c").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (item is Season season)
            {
                var fullSeason = GetFullItem(season) as Season ?? season;
                var aggResult = GetOrBuildAggregatedDataForParent(fullSeason, options, globalOptions);
                sb.Append("_c").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (item is BoxSet collection)
            {
                var fullCollection = GetFullItem(collection) as BoxSet ?? collection;
                var aggResult = GetOrBuildAggregatedDataForParent(fullCollection, options, globalOptions);
                sb.Append("_c").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (item is MusicAlbum musicAlbum && options.EnableMusicAlbumAggregation)
            {
                var fullAlbum = GetFullItem(musicAlbum) as MusicAlbum ?? musicAlbum;
                var aggResult = GetOrBuildAggregatedDataForAlbum(fullAlbum, options);
                sb.Append("_c").Append(aggResult.CombinedTracksHashShort);
            }
            else if (item is MusicArtist musicArtist && options.EnableMusicAlbumAggregation)
            {
                var fullArtist = GetFullItem(musicArtist) as MusicArtist ?? musicArtist;
                var aggResult = GetOrBuildAggregatedDataForAlbum(fullArtist, options);
                sb.Append("_c").Append(aggResult.CombinedTracksHashShort);
            }
            else
            {
                sb.Append("_i").Append(GetCachedItemMediaStreamHash(item));
            }

            if (item.Tags != null && item.Tags.Length > 0)
            {
                var normalizedTags = item.Tags
                    .Select(SanitizeTagForKey)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .OrderBy(t => t);
                sb.Append("_t").Append(string.Join(",", normalizedTags));
            }

            sb.Append("_r").Append(item.CommunityRating?.ToString(StringConstants.PercentFormat) ?? "N");

            if (options.FavoriteCountIconAlignment != IconAlignment.Disabled)
            {
                sb.Append("_f").Append(GetFavoriteCount(item));
            }

            return sb.ToString();
        }

        [Obsolete]
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            TempFileJanitor.ScheduleCleanupFor(outputFile);

            if (_activeBackend == RenderBackend.None)
            {
                await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, cancellationToken);
                return;
            }

            var profile = await plugin.GetProfileForItemAsync(item).ConfigureAwait(false);
            if (profile == null)
            {
                await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, cancellationToken);
                return;
            }

            var globalOptions = plugin.GetConfiguredOptions();
            var profileOptions = profile.Settings;

            bool globalLockAcquired = false;
            bool itemLockAcquired = false;
            SemaphoreSlim? itemSemaphore = null;
            var renderTimer = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var itemKey = item.Id != Guid.Empty ? item.Id.ToString("N") : $"iid_{item.InternalId}";
                _lockLastUsed[itemKey] = DateTime.UtcNow;
                itemSemaphore = _locks.GetOrAdd(itemKey, _ => new SemaphoreSlim(1, 1));
                _lockLastUsed[itemKey] = DateTime.UtcNow;
                await itemSemaphore.WaitAsync(cancellationToken);
                itemLockAcquired = true;

                await GlobalConcurrencyLock.WaitAsync(cancellationToken);
                globalLockAcquired = true;
                var waitMs = renderTimer.ElapsedMilliseconds;

                item = GetFullItem(item);
                var overlayData = await _overlayDataService.GetOverlayDataAsync(item, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);
                var dataMs = renderTimer.ElapsedMilliseconds - waitMs;

                if (!RequiresRendering(item, imageType, overlayData, profileOptions))
                {
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _logger.Debug($"[EmbyIcons] Nothing to draw for '{item.Name}' ({imageType}); using the original image.");
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (_activeBackend == RenderBackend.NetVips)
                {
                    await EnhanceImageWithNetVipsAsync(item, inputFile, outputFile, imageType, overlayData, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);

                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _logger.Debug($"[EmbyIcons] Rendered '{item.Name}' ({imageType}) with NetVips in {renderTimer.ElapsedMilliseconds} ms: waiting {waitMs} ms, overlay data {dataMs} ms, image {renderTimer.ElapsedMilliseconds - waitMs - dataMs} ms.");
                    return;
                }

                var decodeStartMs = renderTimer.ElapsedMilliseconds;

                byte[] inputBytes;
                using (var inputStream = _fileSystem.GetFileStream(inputFile, FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true))
                using (var inputBuffer = new MemoryStream())
                {
                    await inputStream.CopyToAsync(inputBuffer, 81920, cancellationToken).ConfigureAwait(false);
                    inputBytes = inputBuffer.ToArray();
                }

                using var sourceBitmap = inputBytes.Length > 0 ? SKBitmap.Decode(inputBytes) : null;

                if (sourceBitmap == null)
                {
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, cancellationToken);
                    return;
                }

                using var downscaledBitmap = TryDownscaleForRendering(sourceBitmap, GetMaxRenderDimension(globalOptions));
                if (downscaledBitmap != null)
                {
                    sourceBitmap.Dispose();
                }
                var baseBitmap = downscaledBitmap ?? sourceBitmap;

                bool isMusicItem = item is Audio || item is MusicAlbum || item is MusicArtist;
                using var normalizedBitmap = isMusicItem
                    ? (imageType == ImageType.Primary && profileOptions.NormalizeMusicPosterAspectRatio ? TryNormalizeToSquare(baseBitmap) : null)
                    : (imageType == ImageType.Primary && profileOptions.NormalizePosterAspectRatio) ? TryNormalizeTo2x3(baseBitmap)
                    : (imageType == ImageType.Thumb && profileOptions.NormalizeThumbAspectRatio) ? TryNormalizeToThumb(baseBitmap)
                    : (imageType == ImageType.Banner && profileOptions.NormalizeBannerAspectRatio) ? TryNormalizeToBanner(baseBitmap)
                    : null;
                var bitmapToProcess = normalizedBitmap ?? baseBitmap;
                var decodeMs = renderTimer.ElapsedMilliseconds - decodeStartMs;

                string tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                    {
                        await _imageOverlayService.ApplyOverlaysToStreamAsync(
                            bitmapToProcess, overlayData, profileOptions, globalOptions, fsOut, cancellationToken, null);
                    }

                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _logger.Debug($"[EmbyIcons] Rendered '{item.Name}' ({imageType}, {bitmapToProcess.Width}x{bitmapToProcess.Height}) in {renderTimer.ElapsedMilliseconds} ms: waiting {waitMs} ms, overlay data {dataMs} ms, decode {decodeMs} ms, draw and encode {renderTimer.ElapsedMilliseconds - decodeStartMs - decodeMs} ms.");

                    try
                    {
                        if (File.Exists(outputFile))
                        {
                            File.Replace(tempOutput, outputFile, null);
                        }
                        else
                        {
                            File.Move(tempOutput, outputFile);
                        }
                    }
                    catch (IOException ioEx) when (ioEx.Message.Contains("volume", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(tempOutput, outputFile, overwrite: true);
                        try { File.Delete(tempOutput); } 
                        catch (Exception cleanupEx) 
                        { 
                            if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                                _logger.Debug($"[EmbyIcons] Failed to delete temp file '{tempOutput}': {cleanupEx.Message}");
                        }
                    }
                }
                catch
                {
                    try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } 
                    catch (Exception cleanupEx) 
                    { 
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _logger.Debug($"[EmbyIcons] Failed to clean up temp file '{tempOutput}': {cleanupEx.Message}");
                    }
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug($"[EmbyIcons] Image enhancement task cancelled for item: {item?.Name}.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Critical error during enhancement for {item?.Name}. Copying original.", ex);
                try { await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, CancellationToken.None); }
                catch (Exception copyEx) 
                { 
                    _logger.ErrorException($"[EmbyIcons] Failed to copy original file for {item?.Name}.", copyEx);
                }
            }
            finally
            {
                if (itemLockAcquired && itemSemaphore != null)
                {
                    try { itemSemaphore.Release(); } 
                    catch (Exception ex) 
                    { 
                        _logger.ErrorException($"[EmbyIcons] CRITICAL: Failed to release item semaphore for '{item?.Name}'. Potential deadlock risk.", ex);
                    }
                }
                
                if (globalLockAcquired)
                {
                    try { GlobalConcurrencyLock.Release(); } 
                    catch (Exception ex) 
                    { 
                        _logger.ErrorException("[EmbyIcons] CRITICAL: Failed to release global concurrency lock. Potential deadlock risk.", ex);
                    }
                }
            }
        }

        private async Task EnhanceImageWithNetVipsAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, Models.OverlayData overlayData, ProfileSettings profileOptions, PluginOptions globalOptions, CancellationToken cancellationToken)
        {
            byte[] sourceBytes;
            using (var inputStream = _fileSystem.GetFileStream(inputFile, FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true))
            using (var ms = new MemoryStream())
            {
                await inputStream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                sourceBytes = ms.ToArray();
            }

            bool isMusicItem = item is Audio || item is MusicAlbum || item is MusicArtist;

            using var decoded = NetVips.Image.NewFromBuffer(sourceBytes);
            using var downscaled = ImageProcessing.Vips.VipsAspectRatioNormalizer.TryDownscale(decoded, GetMaxRenderDimension(globalOptions));
            var baseImage = downscaled ?? decoded;

            using var normalized = isMusicItem
                ? (imageType == ImageType.Primary && profileOptions.NormalizeMusicPosterAspectRatio ? ImageProcessing.Vips.VipsAspectRatioNormalizer.TryNormalizeToSquare(baseImage) : null)
                : (imageType == ImageType.Primary && profileOptions.NormalizePosterAspectRatio) ? ImageProcessing.Vips.VipsAspectRatioNormalizer.TryNormalizeTo2x3(baseImage)
                : (imageType == ImageType.Thumb && profileOptions.NormalizeThumbAspectRatio) ? ImageProcessing.Vips.VipsAspectRatioNormalizer.TryNormalizeToThumb(baseImage)
                : (imageType == ImageType.Banner && profileOptions.NormalizeBannerAspectRatio) ? ImageProcessing.Vips.VipsAspectRatioNormalizer.TryNormalizeToBanner(baseImage)
                : null;
            var imageToProcess = normalized ?? baseImage;

            string tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                {
                    await _netVipsOverlayService!.ApplyOverlaysToStreamAsync(imageToProcess, overlayData, profileOptions, globalOptions, fsOut, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    if (File.Exists(outputFile))
                    {
                        File.Replace(tempOutput, outputFile, null);
                    }
                    else
                    {
                        File.Move(tempOutput, outputFile);
                    }
                }
                catch (IOException ioEx) when (ioEx.Message.Contains("volume", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(tempOutput, outputFile, overwrite: true);
                    try { File.Delete(tempOutput); }
                    catch (Exception cleanupEx)
                    {
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _logger.Debug($"[EmbyIcons] Failed to delete temp file '{tempOutput}': {cleanupEx.Message}");
                    }
                }
            }
            catch
            {
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); }
                catch (Exception cleanupEx)
                {
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _logger.Debug($"[EmbyIcons] Failed to clean up temp file '{tempOutput}': {cleanupEx.Message}");
                }
                throw;
            }
        }

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            new() { RequiresTransparency = false };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) => originalSize;

        private static SKBitmap? TryNormalizeToAspectRatio(SKBitmap source, float targetWidth, float targetHeight)
        {
            float targetAspect = targetWidth / targetHeight;
            float sourceAspect = (float)source.Width / source.Height;

            if (Math.Abs(sourceAspect - targetAspect) <= 0.01f)
                return null;

            int cropWidth, cropHeight;
            if (sourceAspect > targetAspect)
            {
                cropHeight = source.Height;
                cropWidth = (int)Math.Round(source.Height * targetAspect);
            }
            else
            {
                cropWidth = source.Width;
                cropHeight = (int)Math.Round(source.Width / targetAspect);
            }

            int x = (source.Width - cropWidth) / 2;
            int y = (source.Height - cropHeight) / 2;

            if (cropWidth <= 0 || cropHeight <= 0)
                return null;

            var result = new SKBitmap(cropWidth, cropHeight, source.AlphaType == SKAlphaType.Opaque);
            if (result.Handle == IntPtr.Zero || result.GetPixels() == IntPtr.Zero)
            {
                result.Dispose();
                return null;
            }

            using var canvas = new SKCanvas(result);
            canvas.DrawBitmap(source, SKRect.Create(x, y, cropWidth, cropHeight), SKRect.Create(0, 0, cropWidth, cropHeight));
            return result;
        }

        private static bool RequiresRendering(BaseItem item, ImageType imageType, Models.OverlayData overlayData, ProfileSettings profileOptions)
        {
            bool isMusicItem = item is Audio || item is MusicAlbum || item is MusicArtist;
            bool normalizes = isMusicItem
                ? imageType == ImageType.Primary && profileOptions.NormalizeMusicPosterAspectRatio
                : (imageType == ImageType.Primary && profileOptions.NormalizePosterAspectRatio)
                  || (imageType == ImageType.Thumb && profileOptions.NormalizeThumbAspectRatio)
                  || (imageType == ImageType.Banner && profileOptions.NormalizeBannerAspectRatio);
            if (normalizes) return true;

            bool barsAlwaysDrawn = !profileOptions.OnlyDrawBarsWhenIconsPresent && (profileOptions.EnableTopIconBar || profileOptions.EnableBottomIconBar);
            if (barsAlwaysDrawn) return true;

            return ImageOverlayService.HasVisibleContent(overlayData, profileOptions);
        }

        private const int MinimumRenderDimension = 250;

        private static int GetMaxRenderDimension(PluginOptions options)
            => options.MaxRenderDimension <= 0 ? 0 : Math.Max(MinimumRenderDimension, options.MaxRenderDimension);

        private static SKBitmap? TryDownscaleForRendering(SKBitmap source, int maxDimension)
        {
            if (maxDimension <= 0) return null;

            int longest = Math.Max(source.Width, source.Height);
            if (longest <= maxDimension) return null;

            double scale = (double)maxDimension / longest;
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            var result = new SKBitmap(width, height, source.AlphaType == SKAlphaType.Opaque);
            if (result.Handle == IntPtr.Zero || result.GetPixels() == IntPtr.Zero)
            {
                result.Dispose();
                return null;
            }

            using var canvas = new SKCanvas(result);
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
            canvas.DrawBitmap(source, SKRect.Create(0, 0, source.Width, source.Height), SKRect.Create(0, 0, width, height), paint);
            return result;
        }

        private static SKBitmap? TryNormalizeTo2x3(SKBitmap source) => TryNormalizeToAspectRatio(source, 2f, 3f);
        private static SKBitmap? TryNormalizeToThumb(SKBitmap source) => TryNormalizeToAspectRatio(source, 16f, 9f);
        private static SKBitmap? TryNormalizeToBanner(SKBitmap source) => TryNormalizeToAspectRatio(source, 1000f, 185f);
        private static SKBitmap? TryNormalizeToSquare(SKBitmap source) => TryNormalizeToAspectRatio(source, 1f, 1f);

        internal static void CleanupStaticResources(ILogger? logger)
        {
            if (_lockCleanupTimer != null)
            {
                lock (_lockCleanupInitLock)
                {
                    if (_lockCleanupTimer != null)
                    {
                        try { _lockCleanupTimer.Dispose(); } 
                        catch (Exception ex) { logger?.Debug($"[EmbyIcons] Error disposing lock cleanup timer: {ex.Message}"); }
                        _lockCleanupTimer = null;
                    }
                }
            }
            
            try
            {
                var semaphores = _locks.Values.ToArray();
                _locks.Clear();
                _lockLastUsed.Clear();
                foreach (var sem in semaphores)
                {
                    try { sem.Dispose(); } 
                    catch (Exception ex) { logger?.Debug($"[EmbyIcons] Error disposing semaphore: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"[EmbyIcons] Error clearing static locks: {ex.Message}");
            }
            
            try
            {
                _seriesAggregationCache.Clear();
            }
            catch (Exception ex)
            {
                logger?.Debug($"[EmbyIcons] Error clearing series aggregation cache: {ex.Message}");
            }

            try
            {
                _albumAggregationCache.Clear();
            }
            catch (Exception ex)
            {
                logger?.Debug($"[EmbyIcons] Error clearing album aggregation cache: {ex.Message}");
            }
            
            if (_episodeIconCache != null)
            {
                lock (_episodeCacheInitLock)
                {
                    if (_episodeIconCache != null)
                    {
                        try { _episodeIconCache.Dispose(); } 
                        catch (Exception ex) { logger?.Debug($"[EmbyIcons] Error disposing static episode cache: {ex.Message}"); }
                        _episodeIconCache = null;
                    }
                }
            }
        }

        public int GetFavoriteCount(BaseItem item)
        {
            var cache = _favoriteCountCache;
            if (item.Id != Guid.Empty && cache.TryGetValue(item.Id, out int cachedCount))
            {
                return cachedCount;
            }

            var count = ComputeFavoriteCount(item);

            if (item.Id != Guid.Empty)
            {
                try
                {
                    cache.Set(item.Id, count, new MemoryCacheEntryOptions()
                        .SetSize(1)
                        .SetSlidingExpiration(TimeSpan.FromHours(6)));
                }
                catch (ObjectDisposedException) { }
            }

            return count;
        }

        internal void InvalidateFavoriteCount(Guid itemId)
        {
            if (itemId == Guid.Empty) return;
            try { _favoriteCountCache.Remove(itemId); } catch (ObjectDisposedException) { }
        }

        private int ComputeFavoriteCount(BaseItem item)
        {
            try
            {
                var userManager = Plugin.Instance?.UserManager;
                var appHost = Plugin.Instance?.ApplicationHost;
                if (userManager == null || appHost == null)
                {
                    _logger?.Debug("[EmbyIcons] UserManager or AppHost not available for favorite count.");
                    return 0;
                }

                int favoriteCount = 0;

                var userDataManager = _userDataManager;
                if (userDataManager == null)
                {
                    userDataManager = appHost.Resolve<MediaBrowser.Controller.Library.IUserDataManager>();
                    if (userDataManager == null)
                    {
                        _logger?.Debug("[EmbyIcons] IUserDataManager not available.");
                        return 0;
                    }
                    _userDataManager = userDataManager;
                }
                
                var userQuery = new UserQuery();
                var userIds = userManager.GetUserIds(userQuery);
                
                foreach (var userId in userIds.Items)
                {
                    try
                    {
                        var user = userManager.GetUserById(userId);
                        if (user != null && item.IsVisibleStandalone(user))
                        {
                            var userData = userDataManager.GetUserData(user, item);
                            if (userData != null && userData.IsFavorite)
                            {
                                favoriteCount++;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                return favoriteCount;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[EmbyIcons] Error calculating favorite count.", ex);
                return 0;
            }
        }

        public void Dispose()
        {
            try { _iconCacheManager?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing icon cache manager: {ex.Message}"); }

            try { _netVipsIconCacheManager?.Dispose(); }
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing NetVips icon cache manager: {ex.Message}"); }

            try { _overlayDataService?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing overlay data service: {ex.Message}"); }
            
            if (_globalConcurrencyLock != null)
            {
                try { _globalConcurrencyLock.Dispose(); } 
                catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing global concurrency lock: {ex.Message}"); }
            }
        }
    }
}