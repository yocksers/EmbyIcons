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
        private readonly ILibraryManager _libraryManager;
        internal readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        private static long _enhanceCallCount;
        internal static long EnhanceCallCount => Interlocked.Read(ref _enhanceCallCount);

        private static long _enhanceWorkMs;
        private static long _enhanceTimedCount;
        internal static (long WorkMs, long Count) EnhanceTiming => (Interlocked.Read(ref _enhanceWorkMs), Interlocked.Read(ref _enhanceTimedCount));

        private volatile SemaphoreSlim? _globalConcurrencyLock;
        private readonly object _lockInitializationLock = new object();
        private readonly object _templateCacheLock = new object();

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
        private readonly ImageOverlayService _imageOverlayService;
        private volatile IconTemplateCache? _templateCache;

        private static readonly TimeSpan SeriesAggregationPruneInterval = TimeSpan.FromDays(7);
        public ILogger Logger => _logger;
        public IconTemplateCache? TemplateCache => _templateCache;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, ILogManager logManager, IFileSystem fileSystem)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

            _logger.Info("[EmbyIcons] Session started.");

            if (ImageProcessingCapabilities.IsSkiaSharpAvailable(_logger))
            {
                _logger.Info("[EmbyIcons] SkiaSharp is available. Icon overlays will be applied.");
            }
            else
            {
                _logger.Warn("[EmbyIcons] SkiaSharp is not available. Icon overlays will be disabled.");
                _logger.Warn("[EmbyIcons] To enable icon overlays, ensure SkiaSharp native libraries are installed for your platform.");
            }

            _iconCacheManager = new IconCacheManager(_logger);
            _overlayDataService = new OverlayDataService(this, _libraryManager);
            _imageOverlayService = new ImageOverlayService(_logger, _iconCacheManager);
        }
        public void EnsureTemplateCacheInitialized()
        {
            if (Plugin.Instance?.Configuration.EnableIconTemplateCaching ?? false)
            {
                if (_templateCache == null)
                {
                    lock (_templateCacheLock)
                    {
                        if (_templateCache == null)
                        {
                            _templateCache = new IconTemplateCache(_logger);
                            _logger.Info("[EmbyIcons] Icon template caching enabled.");
                        }
                    }
                }
            }
            else
            {
                if (_templateCache != null)
                {
                    lock (_templateCacheLock)
                    {
                        if (_templateCache != null)
                        {
                            try
                            {
                                _templateCache.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug($"[EmbyIcons] Error disposing template cache: {ex.Message}");
                            }
                            _templateCache = null;
                            _logger.Info("[EmbyIcons] Icon template caching disabled.");
                        }
                    }
                }
            }
        }
        
        public Task ForceCacheRefreshAsync(string iconsFolder)
        {
            _logger.Info($"[EmbyIcons] Forcing full cache refresh for folder: '{iconsFolder}'");
            ClearAllItemDataCaches();
            ClearFavoriteCounts();
            _templateCache?.Clear();
            return _iconCacheManager.RefreshCacheOnDemandAsync(iconsFolder);
        }

        public void ClearAllItemDataCaches()
        {
            _seriesAggregationCache.Clear();
            _albumAggregationCache.Clear();
            _aggregatedParentIds.Clear();
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

        private static long _lastHostCallErrorTicks;
        private static long _suppressedHostCallErrors;

        private void LogHostCallError(string operation, BaseItem? item, ImageType imageType, Exception ex)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Interlocked.Read(ref _lastHostCallErrorTicks);
            if (nowTicks - lastTicks < TimeSpan.FromMinutes(5).Ticks ||
                Interlocked.CompareExchange(ref _lastHostCallErrorTicks, nowTicks, lastTicks) != lastTicks)
            {
                Interlocked.Increment(ref _suppressedHostCallErrors);
                return;
            }

            var suppressed = Interlocked.Exchange(ref _suppressedHostCallErrors, 0);
            var suppressedText = suppressed > 0 ? $" {suppressed} similar error(s) since the last report were not logged." : string.Empty;
            _logger.ErrorException($"[EmbyIcons] {operation} failed for '{item?.Name}' ({imageType}). EmbyIcons skipped this image so Emby can continue.{suppressedText}", ex);
        }

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            try
            {
                return SupportsCore(item, imageType);
            }
            catch (Exception ex)
            {
                LogHostCallError("Checking image support", item, imageType, ex);
                return false;
            }
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            try
            {
                return GetConfigurationCacheKeyCore(item, imageType);
            }
            catch (Exception ex)
            {
                LogHostCallError("Building the image cache key", item, imageType, ex);
                return $"ei_err_{item?.InternalId ?? 0}_{(int)imageType}";
            }
        }

        private bool SupportsCore(BaseItem? item, ImageType imageType)
        {
            if (item == null) return false;

            if (item.Id == Guid.Empty && item.InternalId == 0) return false;

            if (!ImageProcessingCapabilities.IsSkiaSharpAvailable(_logger))
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

        private string GetConfigurationCacheKeyCore(BaseItem item, ImageType imageType)
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

            sb.Append("ei10_")
              .Append(itemIdSegment)
              .Append('_').Append((int)imageType)
              .Append('v').Append(globalOptions.PersistedVersion)
              .Append('p').Append(Convert.ToBase64String(profile.Id.ToByteArray()).TrimEnd('='));

            bool isMusicItem = item is Audio || item is MusicAlbum || item is MusicArtist;
            var content = DrawableContent.None;
            if (isMusicItem)
            {
                content |= DrawableContent.Music;
                if (imageType == ImageType.Primary) content |= DrawableContent.MusicPoster;
            }
            else if (imageType == ImageType.Primary) content |= DrawableContent.PosterImage;
            else if (imageType == ImageType.Thumb) content |= DrawableContent.ThumbImage;
            else if (imageType == ImageType.Banner) content |= DrawableContent.BannerImage;

            if (item is Series || item is Season) content |= DrawableContent.TvShow;
            else if (item is BoxSet) content |= DrawableContent.Collection;
            if (!string.IsNullOrWhiteSpace(globalOptions.MDBListApiKey)) content |= DrawableContent.MdbListRatings;

            var overlayData = _overlayDataService.GetBaseOverlayData(item, options, globalOptions);
            content |= OverlayDataKey.GetContent(overlayData);

            sb.Append("_s").Append(RenderSettingsKey.Get(profile, globalOptions, content));
            sb.Append("_d").Append(OverlayDataKey.Compute(overlayData));

            return sb.ToString();
        }

        [Obsolete]
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            Interlocked.Increment(ref _enhanceCallCount);
            TempFileJanitor.ScheduleCleanupFor(outputFile);

            var profile = await plugin.GetProfileForItemAsync(item).ConfigureAwait(false);
            if (profile == null)
            {
                await CopyOriginalAsync(item, imageType, inputFile, outputFile, cancellationToken).ConfigureAwait(false);
                return;
            }

            var globalOptions = plugin.GetConfiguredOptions();
            var profileOptions = profile.Settings;

            bool globalLockAcquired = false;
            var renderTimer = System.Diagnostics.Stopwatch.StartNew();
            long lockWaitMs = -1;

            try
            {
                await GlobalConcurrencyLock.WaitAsync(cancellationToken);
                globalLockAcquired = true;
                lockWaitMs = renderTimer.ElapsedMilliseconds;

                item = GetFullItem(item);
                var overlayData = await _overlayDataService.GetOverlayDataAsync(item, profileOptions, globalOptions, cancellationToken).ConfigureAwait(false);

                using var inputStream = FileUtils.OpenSourceImage(inputFile, _fileSystem);
                using var sourceBitmap = DecodeForRendering(inputStream, GetMaxRenderDimension(globalOptions));

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

                string tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                    {
                        await _imageOverlayService.ApplyOverlaysToStreamAsync(
                            bitmapToProcess, overlayData, profileOptions, globalOptions, fsOut, cancellationToken, null, _templateCache);
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
            catch (OperationCanceledException)
            {
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug($"[EmbyIcons] Image enhancement task cancelled for item: {item?.Name}.");
            }
            catch (SourceImageUnavailableException ex)
            {
                LogSourceImageUnavailable(item, imageType, ex);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Critical error during enhancement for {item?.Name}. Copying original.", ex);
                try { await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, CancellationToken.None); }
                catch (SourceImageUnavailableException sourceEx)
                {
                    LogSourceImageUnavailable(item, imageType, sourceEx);
                }
                catch (Exception copyEx)
                {
                    _logger.ErrorException($"[EmbyIcons] Failed to copy original file for {item?.Name}.", copyEx);
                }
            }
            finally
            {
                if (globalLockAcquired)
                {
                    try { GlobalConcurrencyLock.Release(); }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("[EmbyIcons] CRITICAL: Failed to release global concurrency lock. Potential deadlock risk.", ex);
                    }
                }

                if (lockWaitMs >= 0)
                {
                    Interlocked.Add(ref _enhanceWorkMs, renderTimer.ElapsedMilliseconds - lockWaitMs);
                    Interlocked.Increment(ref _enhanceTimedCount);
                }
            }
        }

        private async Task CopyOriginalAsync(BaseItem? item, ImageType imageType, string inputFile, string outputFile, CancellationToken cancellationToken)
        {
            try
            {
                await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceImageUnavailableException ex)
            {
                LogSourceImageUnavailable(item, imageType, ex);
            }
        }

        private void LogSourceImageUnavailable(BaseItem? item, ImageType imageType, SourceImageUnavailableException ex)
        {
            _logger.Warn($"[EmbyIcons] Skipped the {imageType} image of '{item?.Name}': {ex.Message}");
        }

        private const int MinimumRenderDimension = 250;

        private static int GetMaxRenderDimension(PluginOptions options)
            => options.MaxRenderDimension <= 0 ? 0 : Math.Max(MinimumRenderDimension, options.MaxRenderDimension);

        private static readonly float[] DecodeScales = { 0.125f, 0.25f, 0.5f };

        private static SKBitmap? DecodeForRendering(Stream input, int maxDimension)
        {
            using var codec = SKCodec.Create(input);
            if (codec == null) return null;

            var info = codec.Info;
            int longest = Math.Max(info.Width, info.Height);
            if (maxDimension <= 0 || longest <= maxDimension)
            {
                return SKBitmap.Decode(codec);
            }

            foreach (var scale in DecodeScales)
            {
                if (longest * scale < maxDimension) continue;

                var scaled = codec.GetScaledDimensions(scale);
                if (scaled.Width <= 0 || scaled.Height <= 0) continue;
                if (Math.Max(scaled.Width, scaled.Height) < maxDimension) continue;
                if (scaled.Width >= info.Width && scaled.Height >= info.Height) break;

                var target = new SKImageInfo(scaled.Width, scaled.Height, info.ColorType, info.AlphaType == SKAlphaType.Unpremul ? SKAlphaType.Premul : info.AlphaType);

                var bitmap = SKBitmap.Decode(codec, target);
                if (bitmap != null) return bitmap;
                break;
            }

            return SKBitmap.Decode(codec);
        }

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

            var subset = new SKBitmap();
            if (source.ExtractSubset(subset, SKRectI.Create(x, y, cropWidth, cropHeight)))
            {
                return subset;
            }
            subset.Dispose();

            var result = new SKBitmap(cropWidth, cropHeight);
            using var canvas = new SKCanvas(result);
            canvas.DrawBitmap(source, SKRect.Create(x, y, cropWidth, cropHeight), SKRect.Create(0, 0, cropWidth, cropHeight));
            return result;
        }

        private static SKBitmap? TryNormalizeTo2x3(SKBitmap source) => TryNormalizeToAspectRatio(source, 2f, 3f);
        private static SKBitmap? TryNormalizeToThumb(SKBitmap source) => TryNormalizeToAspectRatio(source, 16f, 9f);
        private static SKBitmap? TryNormalizeToBanner(SKBitmap source) => TryNormalizeToAspectRatio(source, 1000f, 185f);
        private static SKBitmap? TryNormalizeToSquare(SKBitmap source) => TryNormalizeToAspectRatio(source, 1f, 1f);

        internal static void CleanupStaticResources(ILogger? logger)
        {
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

        private const int FavoriteCountCacheSize = 20000;
        private MemoryCache _favoriteCountCache = CreateFavoriteCountCache();

        private static MemoryCache CreateFavoriteCountCache() => new MemoryCache(new MemoryCacheOptions { SizeLimit = FavoriteCountCacheSize });

        public int GetFavoriteCount(BaseItem item)
        {
            var cache = _favoriteCountCache;
            try
            {
                if (item.Id != Guid.Empty && cache.TryGetValue(item.Id, out int cachedCount))
                {
                    return cachedCount;
                }
            }
            catch (ObjectDisposedException) { }

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

        internal void ClearFavoriteCounts()
        {
            Interlocked.Exchange(ref _favoriteCountCache, CreateFavoriteCountCache());
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
            
            try { _overlayDataService?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing overlay data service: {ex.Message}"); }
            
            try { _templateCache?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing template cache: {ex.Message}"); }
            
            if (_globalConcurrencyLock != null)
            {
                try { _globalConcurrencyLock.Dispose(); } 
                catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing global concurrency lock: {ex.Message}"); }
            }
        }
    }
}