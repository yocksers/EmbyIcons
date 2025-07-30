using EmbyIcons.Helpers;
using EmbyIcons.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ILibraryManager _libraryManager;
        internal readonly ILogger _logger;

        private static readonly SemaphoreSlim _globalConcurrencyLock = new(Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * 0.75)));

        internal readonly IconCacheManager _iconCacheManager;
        internal static readonly ConcurrentDictionary<Guid, AggregatedSeriesResult> _seriesAggregationCache = new();

        private readonly OverlayDataService _overlayDataService;
        private readonly ImageOverlayService _imageOverlayService;

        private static readonly TimeSpan SeriesAggregationPruneInterval = TimeSpan.FromDays(7);

        internal static readonly SKPaint ResamplingPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        internal static readonly SKPaint AliasedPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        internal static readonly SKPaint TextPaint = new() { IsAntialias = true, Color = SKColors.White };
        internal static readonly SKPaint AliasedTextPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None, Color = SKColors.White };
        internal static readonly SKPaint TextStrokePaint = new() { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.StrokeAndFill };

        public ILogger Logger => _logger;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, IUserViewManager userViewManager, ILogManager logManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));

            _logger.Info("[EmbyIcons] Session started.");

            _iconCacheManager = new IconCacheManager(_logger);

            _overlayDataService = new OverlayDataService(this);
            _imageOverlayService = new ImageOverlayService(_logger, _iconCacheManager);
        }

        public async Task ForceCacheRefreshAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            _logger.Info($"[EmbyIcons] Forcing full cache refresh for folder: '{iconsFolder}'");
            ClearAllItemDataCaches();
            await _iconCacheManager.RefreshCacheOnDemandAsync(iconsFolder, cancellationToken, force: true);
        }

        public void ClearAllItemDataCaches()
        {
            _seriesAggregationCache.Clear();
            _episodeIconCache.Clear();
            _logger.Info("[EmbyIcons] Cleared all series and episode data caches.");
        }

        public void ForceSeriesRefresh(Guid seriesId)
        {
            ClearSeriesAggregationCache(seriesId);
            var episodesToClear = _episodeIconCache.Where(kvp =>
                _libraryManager.GetItemById(kvp.Key) is Episode ep &&
                ep.Series?.Id == seriesId).ToList();
            foreach (var episode in episodesToClear)
            {
                _episodeIconCache.TryRemove(episode.Key, out _);
            }
        }

        private BaseItem GetFullItem(BaseItem item)
        {
            if (item is Series series && series.Id == Guid.Empty && series.InternalId > 0)
            {
                var fullItem = _libraryManager.GetItemById(series.InternalId);
                return fullItem ?? item;
            }
            return item;
        }

        public void PruneSeriesAggregationCache()
        {
            var now = DateTime.UtcNow;
            int removed = 0;
            foreach (var kvp in _seriesAggregationCache.ToList())
            {
                if (now - kvp.Value.Timestamp > SeriesAggregationPruneInterval)
                {
                    if (_seriesAggregationCache.TryRemove(kvp.Key, out _)) removed++;
                }
            }
            if (removed > 0 && (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)) _logger.Debug($"[EmbyIcons] Pruned {removed} stale entries from the series overlay aggregation cache.");
        }

        public void ClearSeriesAggregationCache(Guid seriesId)
        {
            if (seriesId != Guid.Empty && _seriesAggregationCache.TryRemove(seriesId, out _))
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Event handler cleared aggregation cache for series ID: {seriesId}");
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

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return "";

            var profile = plugin.GetProfileForItem(item);
            if (profile == null) return "";

            var options = profile.Settings;
            var sb = new StringBuilder(512);

            sb.Append("ei5_").Append(item.Id).Append('_').Append((int)imageType)
              .Append("_v").Append(plugin.ConfigurationVersion)
              .Append("_p").Append(profile.Id.ToString("N"));

            var settingsJson = JsonSerializer.Serialize(options);
            sb.Append("_cfg").Append(System.BitConverter.ToString(System.Security.Cryptography.MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(settingsJson))).Replace("-", ""));

            if (item is Series series)
            {
                var fullSeries = GetFullItem(series) as Series ?? series;
                var aggResult = GetOrBuildAggregatedDataForParent(fullSeries, options, plugin.GetConfiguredOptions());
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (item is BoxSet collection)
            {
                var aggResult = GetOrBuildAggregatedDataForParent(collection, options, plugin.GetConfiguredOptions());
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else
            {
                sb.Append("_ih").Append(MediaStreamHelper.GetItemMediaStreamHash(item, item.GetMediaStreams() ?? new List<MediaStream>()));
            }

            if (item.Tags != null && item.Tags.Length > 0)
            {
                var sortedTags = string.Join(",", item.Tags.OrderBy(t => t));
                sb.Append("_t").Append(sortedTags);
            }

            sb.Append("_r").Append(item.CommunityRating?.ToString("F1") ?? "N");
            sb.Append("_d").Append(item.DateModified.Ticks);
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
                await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                return;
            }

            var globalOptions = plugin.GetConfiguredOptions();
            var profileOptions = profile.Settings;

            await _globalConcurrencyLock.WaitAsync(cancellationToken);
            try
            {
                var sem = _locks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(cancellationToken);
                try
                {
                    item = GetFullItem(item);
                    var overlayData = _overlayDataService.GetOverlayData(item, profileOptions, globalOptions);

                    using var sourceBitmap = SKBitmap.Decode(inputFile);
                    if (sourceBitmap == null)
                    {
                        await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                        return;
                    }

                    using var outputStream = await _imageOverlayService.ApplyOverlaysAsync(sourceBitmap, overlayData, profileOptions, globalOptions, cancellationToken);

                    string tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                    {
                        await outputStream.CopyToAsync(fsOut, cancellationToken);
                    }

                    if (File.Exists(outputFile))
                    {
                        File.Replace(tempOutput, outputFile, null);
                    }
                    else
                    {
                        File.Move(tempOutput, outputFile);
                    }
                }
                finally
                {
                    sem.Release();
                }
            }
            catch (OperationCanceledException)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Image enhancement task cancelled for item: {item?.Name}.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Critical error during enhancement for {item?.Name}. Copying original.", ex);
                try { await FileUtils.SafeCopyAsync(inputFile, outputFile, CancellationToken.None); }
                catch { /* Ignore fallback copy exception */ }
            }
            finally
            {
                _globalConcurrencyLock.Release();
            }
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            (Plugin.Instance?.IsLibraryAllowed(item) ?? false) ? new() { RequiresTransparency = false } : null;

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) => originalSize;

        public void Dispose()
        {
            foreach (var sem in _locks.Values)
            {
                sem.Dispose();
            }
            _locks.Clear();
            _iconCacheManager.Dispose();
            _globalConcurrencyLock.Dispose();
            ResamplingPaint.Dispose();
            AliasedPaint.Dispose();
            TextPaint.Dispose();
            AliasedTextPaint.Dispose();
            TextStrokePaint.Dispose();
        }
    }
}