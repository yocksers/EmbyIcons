﻿using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using EmbyIcons.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
using System.Collections.Generic;
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
        private readonly ILibraryManager _libraryManager;
        internal readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        private static SemaphoreSlim? _globalConcurrencyLock;
        private static readonly object _lockInitializationLock = new object();

        private static SemaphoreSlim GlobalConcurrencyLock
        {
            get
            {
                if (_globalConcurrencyLock == null)
                {
                    lock (_lockInitializationLock)
                    {
                        if (_globalConcurrencyLock == null)
                        {
                            var multiplier = Math.Clamp(Plugin.Instance?.Configuration.GlobalConcurrencyMultiplier ?? 0.75, 0.1, 2.0);
                            var maxConcurrency = Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * multiplier));
                            _globalConcurrencyLock = new SemaphoreSlim(maxConcurrency, maxConcurrency);
                        }
                    }
                }
                return _globalConcurrencyLock;
            }
        }

        internal readonly IconCacheManager _iconCacheManager;
        internal static readonly ConcurrentDictionary<Guid, AggregatedSeriesResult> _seriesAggregationCache = new();

        internal readonly OverlayDataService _overlayDataService;
        private readonly ImageOverlayService _imageOverlayService;

        private static readonly TimeSpan SeriesAggregationPruneInterval = TimeSpan.FromDays(7);

        /// <summary>
        /// Gets the logger instance for this enhancer.
        /// </summary>
        public ILogger Logger => _logger;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, ILogManager logManager, IFileSystem fileSystem)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

            // Don't initialize episode cache here - it will be initialized on first use
            // to avoid accessing Plugin.Instance.Configuration before Plugin is fully constructed

            _logger.Info("[EmbyIcons] Session started.");

            _iconCacheManager = new IconCacheManager(_logger);
            _overlayDataService = new OverlayDataService(this, _libraryManager);
            _imageOverlayService = new ImageOverlayService(_logger, _iconCacheManager);
        }

        /// <summary>
        /// Forces a complete cache refresh by clearing all caches and reloading icons from the specified folder.
        /// </summary>
        /// <param name="iconsFolder">The folder containing custom icons.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        public async Task ForceCacheRefreshAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            _logger.Info($"[EmbyIcons] Forcing full cache refresh for folder: '{iconsFolder}'");
            ClearAllItemDataCaches();
            await _iconCacheManager.RefreshCacheOnDemandAsync(iconsFolder, cancellationToken, force: true).ConfigureAwait(false);
        }

        public void ClearAllItemDataCaches()
        {
            _seriesAggregationCache.Clear();
            ClearAllEpisodeCaches();
            _logger.Info("[EmbyIcons] Cleared all series and episode data caches.");
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

            var episodesInSeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentIds = new[] { seriesItem.InternalId },
                IncludeItemTypes = new[] { Constants.Episode },
                Recursive = true
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
            if ((item is Series || item is BoxSet || item is Season) && item.Id == Guid.Empty && item.InternalId > 0)
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

            bool isSupportedType = item is Video || item is Series || item is Season || item is Photo || item is BoxSet;
            if (!isSupportedType) return false;

            if (item is Episode && !(options.ShowOverlaysForEpisodes)) return false;
            if (item is Season && !(options.ShowOverlaysForSeasons)) return false;

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
                   options.ParentalRatingIconAlignment != IconAlignment.Disabled;
        }

        private static string SanitizeTagForKey(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
            Span<char> buf = stackalloc char[tag.Length];
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
            return (start > end) ? string.Empty : new string(buf.Slice(start, end - start + 1));
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return "";

            var profile = plugin.GetProfileForItem(item);
            if (profile == null) return "";

            var globalOptions = plugin.GetConfiguredOptions();
            var options = profile.Settings;
            var sb = new StringBuilder(768);

            sb.Append("ei7_")
              .Append(item.Id.ToString("N")).Append('_').Append((int)imageType)
              .Append("_v").Append(plugin.ConfigurationVersion)
              .Append("_p").Append(profile.Id.ToString("N"));

            sb.Append('_').Append((int)options.AudioIconAlignment).Append(options.AudioOverlayHorizontal ? 't' : 'f').Append(options.AudioIconPriority);
            sb.Append('_').Append((int)options.SubtitleIconAlignment).Append(options.SubtitleOverlayHorizontal ? 't' : 'f').Append(options.SubtitleIconPriority);
            sb.Append('_').Append((int)options.ChannelIconAlignment).Append(options.ChannelOverlayHorizontal ? 't' : 'f').Append(options.ChannelIconPriority);
            sb.Append('_').Append((int)options.AudioCodecIconAlignment).Append(options.AudioCodecOverlayHorizontal ? 't' : 'f').Append(options.AudioCodecIconPriority);
            sb.Append('_').Append((int)options.VideoFormatIconAlignment).Append(options.VideoFormatOverlayHorizontal ? 't' : 'f').Append(options.VideoFormatIconPriority);
            sb.Append('_').Append((int)options.VideoCodecIconAlignment).Append(options.VideoCodecOverlayHorizontal ? 't' : 'f').Append(options.VideoCodecIconPriority);
            sb.Append('_').Append((int)options.TagIconAlignment).Append(options.TagOverlayHorizontal ? 't' : 'f').Append(options.TagIconPriority);
            sb.Append('_').Append((int)options.ResolutionIconAlignment).Append(options.ResolutionOverlayHorizontal ? 't' : 'f').Append(options.ResolutionIconPriority);
            sb.Append('_').Append((int)options.CommunityScoreIconAlignment).Append(options.CommunityScoreOverlayHorizontal ? 't' : 'f').Append(options.CommunityScoreIconPriority);
            sb.Append('_').Append((int)options.AspectRatioIconAlignment).Append(options.AspectRatioOverlayHorizontal ? 't' : 'f').Append(options.AspectRatioIconPriority);
            sb.Append('_').Append((int)options.ParentalRatingIconAlignment).Append(options.ParentalRatingOverlayHorizontal ? 't' : 'f').Append(options.ParentalRatingIconPriority);

            sb.Append('_').Append(options.IconSize)
              .Append(globalOptions.JpegQuality)
              .Append(globalOptions.EnableImageSmoothing ? 't' : 'f')
              .Append((int)globalOptions.OutputFormat);

            sb.Append('_').Append((int)options.CommunityScoreBackgroundShape).Append(options.CommunityScoreBackgroundColor).Append(options.CommunityScoreBackgroundOpacity);
            sb.Append('_').Append(options.UseSeriesLiteMode ? 't' : 'f').Append(options.UseCollectionLiteMode ? 't' : 'f');
            sb.Append('_').Append(options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? 't' : 'f').Append(options.ShowCollectionIconsIfAllChildrenHaveLanguage ? 't' : 'f');
            sb.Append('_').Append(options.ExcludeSpecialsFromSeriesAggregation ? 't' : 'f').Append(options.SnapAspectRatioToCommon ? 't' : 'f');
            sb.Append('_').Append(globalOptions.EnableCollectionProfileLookup ? 't' : 'f');

            if (item is Series series)
            {
                var fullSeries = GetFullItem(series) as Series ?? series;
                var aggResult = GetOrBuildAggregatedDataForParent(fullSeries, options, globalOptions);
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (item is Season season)
            {
                var fullSeason = GetFullItem(season) as Season ?? season;
                var aggResult = GetOrBuildAggregatedDataForParent(fullSeason, options, globalOptions);
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (item is BoxSet collection)
            {
                var fullCollection = GetFullItem(collection) as BoxSet ?? collection;
                var aggResult = GetOrBuildAggregatedDataForParent(fullCollection, options, globalOptions);
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else
            {
                // Cache GetMediaStreams result to avoid multiple calls
                var mediaStreams = item.GetMediaStreams();
                if (mediaStreams == null) mediaStreams = new List<MediaStream>();
                sb.Append("_ih").Append(MediaStreamHelper.GetItemMediaStreamHashV2(item, mediaStreams));
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

            return sb.ToString();
        }

        [Obsolete]
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            var profile = plugin.GetProfileForItem(item);
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
            
            try
            {
                await GlobalConcurrencyLock.WaitAsync(cancellationToken);
                globalLockAcquired = true;

                itemSemaphore = _locks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                await itemSemaphore.WaitAsync(cancellationToken);
                itemLockAcquired = true;
                
                item = GetFullItem(item);
                var overlayData = _overlayDataService.GetOverlayData(item, profileOptions, globalOptions);

                await using var inputStream = _fileSystem.GetFileStream(inputFile, FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true);
                using var sourceBitmap = SKBitmap.Decode(inputStream);

                if (sourceBitmap == null)
                {
                    await FileUtils.SafeCopyAsync(inputFile, outputFile, _fileSystem, cancellationToken);
                    return;
                }

                string tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                {
                    await _imageOverlayService.ApplyOverlaysToStreamAsync(
                        sourceBitmap, overlayData, profileOptions, globalOptions, fsOut, cancellationToken, null);
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
                    try { File.Delete(tempOutput); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
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
                    try { itemSemaphore.Release(); } catch { }
                }
                
                if (globalLockAcquired)
                {
                    try { GlobalConcurrencyLock.Release(); } catch { }
                }
            }
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            (Plugin.Instance?.IsLibraryAllowed(item) ?? false) ? new() { RequiresTransparency = false } : null;

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) => originalSize;

        public void Dispose()
        {
            var semaphores = _locks.Values.ToArray();
            _locks.Clear();
            foreach (var sem in semaphores)
            {
                try { sem.Dispose(); } 
                catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing semaphore: {ex.Message}"); }
            }
            
            try { _iconCacheManager?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing icon cache manager: {ex.Message}"); }
            
            try { _overlayDataService?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing overlay data service: {ex.Message}"); }
            
            if (_globalConcurrencyLock != null)
            {
                try { _globalConcurrencyLock.Dispose(); } 
                catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing global concurrency lock: {ex.Message}"); }
            }
            
            try { _episodeIconCache?.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing episode cache: {ex.Message}"); }
            
            try { FontHelper.Dispose(); } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error disposing FontHelper: {ex.Message}"); }
        }
    }
}