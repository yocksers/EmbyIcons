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
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private readonly ILibraryManager _libraryManager;
        private readonly IUserViewManager _userViewManager;
        internal readonly IconCacheManager _iconCacheManager;
        private readonly ILogger _logger;

        private SKImage? _starIcon;
        private static readonly SemaphoreSlim _globalConcurrencyLock = new(Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * 0.75)), Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * 0.75)));
        private static string _iconCacheVersion = string.Empty;
        private readonly ConcurrentDictionary<Guid, AggregatedSeriesResult> _seriesAggregationCache = new();

        private static readonly TimeSpan SeriesAggregationPruneInterval = TimeSpan.FromDays(7);

        public EmbyIconsEnhancer(ILibraryManager libraryManager, IUserViewManager userViewManager, ILogManager logManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new ArgumentNullException(nameof(userViewManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));
            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30), _logger, 4);
            _iconCacheManager.CacheRefreshedWithVersion += (sender, version) => { _iconCacheVersion = version ?? string.Empty; };

            PruneSeriesAggregationCache();
        }

        public void PruneSeriesAggregationCache()
        {
            var now = DateTime.UtcNow;
            int removed = 0;
            foreach (var kvp in _seriesAggregationCache.ToList())
            {
                var age = now - kvp.Value.Timestamp;
                if (age > SeriesAggregationPruneInterval)
                {
                    if (_seriesAggregationCache.TryRemove(kvp.Key, out _))
                        removed++;
                }
            }
            if (removed > 0)
                _logger.Info($"[EmbyIcons] Pruned {removed} stale entries from the series overlay aggregation cache.");
        }

        public Task RefreshIconCacheAsync(CancellationToken cancellationToken, bool force = false) => _iconCacheManager.RefreshCacheOnDemandAsync(cancellationToken, force);
        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            // --- BEGIN REWRITE: Call new logic in Plugin.cs ---
            if (item == null || imageType != ImageType.Primary || item is Person || !(Plugin.Instance?.IsLibraryAllowed(item) ?? false)) return false;
            // --- END REWRITE ---

            var options = Plugin.Instance?.GetConfiguredOptions();
            if (item is Episode && !(options?.ShowOverlaysForEpisodes ?? true)) return false;

            return (options?.ShowAudioIcons ?? false) || (options?.ShowSubtitleIcons ?? false) || (options?.ShowAudioChannelIcons ?? false) || (options?.ShowVideoFormatIcons ?? false) || (options?.ShowResolutionIcons ?? false) || (options?.ShowCommunityScoreIcon ?? false);
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            try
            {
                // --- BEGIN REWRITE: Call new logic in Plugin.cs ---
                if (!(Plugin.Instance?.IsLibraryAllowed(item) ?? false)) return "";
                // --- END REWRITE ---

                var options = Plugin.Instance?.GetConfiguredOptions();
                if (options == null) return "";

                var sb = new StringBuilder();
                sb.Append("embyicons_").Append(item.Id).Append('_').Append(imageType)
                  .Append("_sz").Append(options.IconSize)
                  .Append("_libs").Append((options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", ""))
                  .Append("_aAlign").Append(options.AudioIconAlignment)
                  .Append("_sAlign").Append(options.SubtitleIconAlignment)
                  .Append("_cAlign").Append(options.ChannelIconAlignment)
                  .Append("_vfAlign").Append(options.VideoFormatIconAlignment)
                  .Append("_resAlign").Append(options.ResolutionIconAlignment)
                  .Append("_scoreAlign").Append(options.CommunityScoreIconAlignment)
                  .Append("_showA").Append(options.ShowAudioIcons ? "1" : "0")
                  .Append("_showS").Append(options.ShowSubtitleIcons ? "1" : "0")
                  .Append("_showC").Append(options.ShowAudioChannelIcons ? "1" : "0")
                  .Append("_showVF").Append(options.ShowVideoFormatIcons ? "1" : "0")
                  .Append("_showRes").Append(options.ShowResolutionIcons ? "1" : "0")
                  .Append("_showScore").Append(options.ShowCommunityScoreIcon ? "1" : "0")
                  .Append("_seriesOpt").Append(options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? "1" : "0")
                  .Append("_seriesLite").Append(options.UseSeriesLiteMode ? "1" : "0")
                  .Append("_seriesTTL").Append(options.SeriesAggregationCacheTTLMinutes)
                  .Append("_seriesCacheOff").Append(options.DisableSeriesAggregationCache ? "1" : "0")
                  .Append("_jpegq").Append(options.JpegQuality)
                  .Append("_smoothing").Append(options.EnableImageSmoothing ? "1" : "0")
                  .Append("_aHoriz").Append(options.AudioOverlayHorizontal ? "1" : "0")
                  .Append("_sHoriz").Append(options.SubtitleOverlayHorizontal ? "1" : "0")
                  .Append("_cHoriz").Append(options.ChannelOverlayHorizontal ? "1" : "0")
                  .Append("_vfHoriz").Append(options.VideoFormatOverlayHorizontal ? "1" : "0")
                  .Append("_resHoriz").Append(options.ResolutionOverlayHorizontal ? "1" : "0")
                  .Append("_scoreHoriz").Append(options.CommunityScoreOverlayHorizontal ? "1" : "0")
                  .Append("_iconVer").Append(_iconCacheVersion)
                  .Append("_itemMediaHash").Append(GetItemMediaStreamHash(item, item.GetMediaStreams()));

                if (item is Series series && (options.ShowSeriesIconsIfAllEpisodesHaveLanguage || options.ShowAudioChannelIcons || options.ShowVideoFormatIcons || options.ShowResolutionIcons))
                {
                    if (series.Id == Guid.Empty && series.InternalId > 0)
                    {
                        _logger.Debug($"[EmbyIcons] Detected lightweight Series object for {series.Name}. Fetching full item by InternalId {series.InternalId}.");
                        var fullSeries = _libraryManager.GetItemById(series.InternalId);
                        if (fullSeries is Series s) series = s;
                    }
                    var aggResult = GetAggregatedDataForParentSync(series, options, ignoreCache: false);
                    sb.Append("_childrenMediaHash").Append(aggResult.CombinedEpisodesHashShort);
                }
                else
                {
                    sb.Append("_rating").Append(item.CommunityRating.HasValue ? item.CommunityRating.Value.ToString("F1") : "none");
                    sb.Append("_dateMod").Append(item.DateModified.Ticks);
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Failed to generate configuration cache key for item {item.Name} ({item.Id}).", ex);
                return $"{item.Id}_{imageType}";
            }
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            // --- BEGIN REWRITE: Call new logic in Plugin.cs ---
            return (Plugin.Instance?.IsLibraryAllowed(item) ?? false) ? new() { RequiresTransparency = false } : null;
            // --- END REWRITE ---
        }

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) => originalSize;

        [Obsolete]
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) => EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, CancellationToken cancellationToken)
        {
            // --- BEGIN REWRITE: Call new logic in Plugin.cs ---
            if (!(Plugin.Instance?.IsLibraryAllowed(item) ?? false))
            // --- END REWRITE ---
            {
                await FileUtils.SafeCopyAsync(inputFile!, outputFile, cancellationToken);
                return;
            }

            float? communityRating = null;
            var options = Plugin.Instance?.GetConfiguredOptions();

            if (options?.ShowCommunityScoreIcon == true && (item is Movie || item is Series))
            {
                for (int i = 0; i < 3; i++)
                {
                    var freshItem = _libraryManager.GetItemById(item.Id);
                    if (freshItem != null && freshItem.CommunityRating.HasValue && freshItem.CommunityRating.Value > 0)
                    {
                        _logger.Info($"[EmbyIcons] Found rating {freshItem.CommunityRating.Value} for {item.Name} on attempt {i + 1}.");
                        communityRating = freshItem.CommunityRating.Value;
                        break;
                    }

                    if (i < 2)
                    {
                        _logger.Warn($"[EmbyIcons] Rating not found for {item.Name} on attempt {i + 1}. Retrying in 750ms.");
                        await Task.Delay(750, cancellationToken);
                    }
                }

                if (!communityRating.HasValue)
                {
                    _logger.Error($"[EmbyIcons] Could not find rating for {item.Name} after all attempts.");
                }
            }

            await _globalConcurrencyLock.WaitAsync(cancellationToken);
            try
            {
                var key = item.Id.ToString();
                var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(cancellationToken);
                try
                {
                    await EnhanceImageInternalAsync(item, inputFile, outputFile, imageType, imageIndex, communityRating, cancellationToken);
                }
                finally
                {
                    sem.Release();
                }
            }
            finally
            {
                _globalConcurrencyLock.Release();
            }
        }

        public void ClearOverlayCacheForItem(BaseItem item)
        {
        }

        public void Dispose()
        {
            var semaphoresToDispose = _locks.Values.ToList();
            _locks.Clear();
            foreach (var sem in semaphoresToDispose)
            {
                sem.Dispose();
            }
            _starIcon?.Dispose();
            _starIcon = null;
            _iconCacheManager.Dispose();
            _globalConcurrencyLock.Dispose();
        }

        // --- BEGIN REWRITE: The IsLibraryAllowed method is now removed from this file and lives in Plugin.cs ---
        // --- END REWRITE ---

        public void ClearSeriesOverlayCache(BaseItem? item)
        {
            if (item == null) return;
            bool removed = _seriesAggregationCache.TryRemove(item.Id, out _);
            _logger.Debug($"[EmbyIcons] ClearSeriesOverlayCache: Series {item?.Name} ({item?.Id}) removed={removed}");
        }

        internal SKImage? GetStarIcon()
        {
            if (_starIcon != null) return _starIcon;

            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var name = $"{typeof(Plugin).Namespace}.Images.star.png";
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null || stream.Length == 0)
                {
                    _logger.Warn($"[EmbyIcons] Embedded resource '{name}' not found or is empty. Make sure its Build Action is set to Embedded Resource.");
                    return null;
                }
                _starIcon = SKImage.FromEncodedData(stream);
                _logger.Debug($"[EmbyIcons] Successfully loaded embedded star.png icon.");
                return _starIcon;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Failed to load embedded star.png icon.", ex);
                return null;
            }
        }
    }
}