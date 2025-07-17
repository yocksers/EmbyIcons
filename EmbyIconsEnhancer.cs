using EmbyIcons.Helpers;
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
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Services;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ILibraryManager _libraryManager;
        internal readonly ILogger _logger;

        private static readonly SemaphoreSlim _globalConcurrencyLock = new(Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * 0.75)));
        private static string _iconCacheVersion = string.Empty;

        internal readonly IconCacheManager _iconCacheManager;
        internal static readonly ConcurrentDictionary<Guid, AggregatedSeriesResult> _seriesAggregationCache = new();

        private readonly OverlayDataService _overlayDataService;
        private readonly ImageOverlayService _imageOverlayService;

        private static readonly TimeSpan SeriesAggregationPruneInterval = TimeSpan.FromDays(7);

        internal static readonly SKPaint ResamplingPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        internal static readonly SKPaint AliasedPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        internal static readonly SKPaint TextPaint = new() { IsAntialias = true, Color = SKColors.White };
        internal static readonly SKPaint TextStrokePaint = new() { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.StrokeAndFill };

        public ILogger Logger => _logger;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, IUserViewManager userViewManager, ILogManager logManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));

            _iconCacheVersion = Guid.NewGuid().ToString("N");
            _logger.Info($"[EmbyIcons] New session started. Set icon cache version to '{_iconCacheVersion}' to force poster redraw.");

            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30), _logger, 4);
            _iconCacheManager.CacheRefreshedWithVersion += (sender, version) => { _iconCacheVersion = version ?? string.Empty; };

            _overlayDataService = new OverlayDataService(this);
            _imageOverlayService = new ImageOverlayService(_logger, _iconCacheManager);
        }

        public void ClearAllItemDataCaches()
        {
            _seriesAggregationCache.Clear();
            _episodeIconCache.Clear();
            _logger.Info("[EmbyIcons] Cleared all series and episode data caches.");
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
            if (removed > 0) _logger.Info($"[EmbyIcons] Pruned {removed} stale entries from the series overlay aggregation cache.");
        }

        public void ClearSeriesAggregationCache(Guid seriesId)
        {
            if (seriesId != Guid.Empty && _seriesAggregationCache.TryRemove(seriesId, out _))
            {
                _logger.Info($"[EmbyIcons] Event handler cleared aggregation cache for series ID: {seriesId}");
            }
        }

        public Task RefreshIconCacheAsync(CancellationToken cancellationToken, bool force = false) => _iconCacheManager.RefreshCacheOnDemandAsync(cancellationToken, force);
        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary) return false;

            bool isSupportedType = item is Video || item is Series || item is Season || item is Photo;
            if (!isSupportedType) return false;

            if (!(Plugin.Instance?.IsLibraryAllowed(item) ?? false)) return false;

            var options = Plugin.Instance?.GetConfiguredOptions();
            if (item is Episode && !(options?.ShowOverlaysForEpisodes ?? true)) return false;

            return (options?.ShowAudioIcons ?? false) || (options?.ShowSubtitleIcons ?? false) || (options?.ShowAudioChannelIcons ?? false) || (options?.ShowVideoFormatIcons ?? false) || (options?.ShowResolutionIcons ?? false) || (options?.ShowCommunityScoreIcon ?? false) || (options?.ShowAudioCodecIcons ?? false) || (options?.ShowVideoCodecIcons ?? false) || (options?.ShowTagIcons ?? false);
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.IsLibraryAllowed(item)) return "";
            var options = plugin.GetConfiguredOptions();

            var sb = new StringBuilder(512);

            sb.Append("ei4_").Append(item.Id).Append('_').Append((int)imageType)
              .Append("_s").Append(options.IconSize)
              .Append("_q").Append(options.JpegQuality)
              .Append("_sm").Append(options.EnableImageSmoothing ? 1 : 0)
              .Append("_iv").Append(_iconCacheVersion)
              .Append("_pv").Append(plugin.ConfigurationVersion);

            int settingsMask = (options.ShowAudioIcons ? 1 : 0) |
                               ((options.ShowSubtitleIcons ? 1 : 0) << 1) |
                               ((options.ShowAudioChannelIcons ? 1 : 0) << 2) |
                               ((options.ShowAudioCodecIcons ? 1 : 0) << 3) |
                               ((options.ShowVideoFormatIcons ? 1 : 0) << 4) |
                               ((options.ShowVideoCodecIcons ? 1 : 0) << 5) |
                               ((options.ShowTagIcons ? 1 : 0) << 6) |
                               ((options.ShowResolutionIcons ? 1 : 0) << 7) |
                               ((options.ShowCommunityScoreIcon ? 1 : 0) << 8) |
                               ((options.ShowOverlaysForEpisodes ? 1 : 0) << 9) |
                               ((options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? 1 : 0) << 10) |
                               ((options.UseSeriesLiteMode ? 1 : 0) << 11);
            sb.Append("_cfg").Append(settingsMask);

            sb.Append("_align")
              .Append((int)options.AudioIconAlignment).Append(options.AudioOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.SubtitleIconAlignment).Append(options.SubtitleOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.ChannelIconAlignment).Append(options.ChannelOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.AudioCodecIconAlignment).Append(options.AudioCodecOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.VideoFormatIconAlignment).Append(options.VideoFormatOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.VideoCodecIconAlignment).Append(options.VideoCodecOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.TagIconAlignment).Append(options.TagOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.ResolutionIconAlignment).Append(options.ResolutionOverlayHorizontal ? 'h' : 'v')
              .Append((int)options.CommunityScoreIconAlignment).Append(options.CommunityScoreOverlayHorizontal ? 'h' : 'v');

            if (options.ShowCommunityScoreIcon)
            {
                sb.Append("_rbs")
                  .Append((int)options.CommunityScoreBackgroundShape)
                  .Append("_rbc").Append(options.CommunityScoreBackgroundColor)
                  .Append("_rbo").Append(options.CommunityScoreBackgroundOpacity);
            }

            if (item is Series series)
            {
                var fullSeries = GetFullItem(series) as Series ?? series;
                var aggResult = GetAggregatedDataForParentSync(fullSeries, options);
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
            var options = plugin.GetConfiguredOptions();
            if (!plugin.IsLibraryAllowed(item))
            {
                await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                return;
            }

            await _globalConcurrencyLock.WaitAsync(cancellationToken);
            try
            {
                var sem = _locks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(cancellationToken);
                try
                {
                    item = GetFullItem(item);
                    var overlayData = _overlayDataService.GetOverlayData(item, options);

                    using var sourceBitmap = SKBitmap.Decode(inputFile);
                    if (sourceBitmap == null)
                    {
                        await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                        return;
                    }

                    using var outputStream = await _imageOverlayService.ApplyOverlaysAsync(sourceBitmap, overlayData, options, cancellationToken);

                    string tempOutput = outputFile + "." + Guid.NewGuid() + ".tmp";
                    await using (var fsOut = File.Create(tempOutput))
                    {
                        await outputStream.CopyToAsync(fsOut, cancellationToken);
                    }
                    File.Move(tempOutput, outputFile, true);
                }
                finally
                {
                    sem.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Debug($"[EmbyIcons] Image enhancement task cancelled for item: {item?.Name}.");
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
            TextStrokePaint.Dispose();
        }
    }
}