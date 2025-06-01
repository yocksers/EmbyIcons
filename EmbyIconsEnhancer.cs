using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging; // Added for ILogManager and ILogger
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly ILogger _logger; // Injected ILogger

        private static string _iconCacheVersion = string.Empty;

        private readonly ConcurrentDictionary<Guid, (HashSet<string> audio, HashSet<string> subtitle, DateTime timestamp)> _seriesLangCache = new();
        private static readonly TimeSpan SeriesLangCacheTTL = TimeSpan.FromSeconds(10);

        // Corrected constructor: accept ILogManager, then get ILogger for this class
        public EmbyIconsEnhancer(ILibraryManager libraryManager, IUserViewManager userViewManager, ILogManager logManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new InvalidOperationException("IUserViewManager not initialized");
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer)); // Get logger for EmbyIconsEnhancer
            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30), _logger, 4); // maxParallelism is 4 by default
            _iconCacheManager.CacheRefreshedWithVersion += (sender, version) =>
            {
                _iconCacheVersion = version ?? string.Empty;
            };
        }

        private bool IsLibraryAllowed(BaseItem item)
        {
            var allowedLibs = Plugin.Instance?.AllowedLibraryIds ?? new HashSet<string>();
            var libraryId = Helpers.FileUtils.GetLibraryIdForItem(_libraryManager, item);
            return allowedLibs.Count == 0 || (libraryId != null && allowedLibs.Contains(libraryId));
        }

        public Task RefreshIconCacheAsync(CancellationToken cancellationToken, bool force = false)
        {
            return _iconCacheManager.RefreshCacheOnDemandAsync(cancellationToken, force);
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary) return false;
            if (item is Person) return false;
            if (!IsLibraryAllowed(item)) return false;

            var options = Plugin.Instance?.GetConfiguredOptions();
            bool showEpisodes = options?.ShowOverlaysForEpisodes ?? true;

            if (item is Episode)
                return showEpisodes;

            if (!(options?.ShowAudioIcons ?? false) && !(options?.ShowSubtitleIcons ?? false))
                return false;

            return item is Movie
                || item is Series
                || item is Season
                || item is BoxSet
                || item is MusicVideo;
        }

        private string GetItemMediaStreamHash(BaseItem item)
        {
            var streams = item.GetMediaStreams() ?? new List<MediaStream>();
            var audioLangs = streams
                .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                .OrderBy(l => l);

            var subtitleLangs = streams
                .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                .OrderBy(l => l);

            var combinedString = string.Join(",", audioLangs) + ";" + string.Join(",", subtitleLangs);

            if (string.IsNullOrEmpty(combinedString))
                return "no_streams";

            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(combinedString);
                var hashBytes = sha.ComputeHash(bytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
            }
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            if (!IsLibraryAllowed(item))
                return "";

            var options = Plugin.Instance?.GetConfiguredOptions();
            if (options == null) return "";

            var libs = (options.SelectedLibraries ?? "")
                        .Replace(',', '-')
                        .Replace(" ", "");

            var aAlign = options.AudioIconAlignment.ToString();
            var sAlign = options.SubtitleIconAlignment.ToString();

            var showA = options.ShowAudioIcons ? "1" : "0";
            var showS = options.ShowSubtitleIcons ? "1" : "0";

            var seriesOption = options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? "1" : "0";

            var aVertOffset = options.AudioIconVerticalOffset.ToString();
            var sVertOffset = options.SubtitleIconVerticalOffset.ToString();

            // NEW: Include JPEG quality and image smoothing in cache key!
            var jpegQuality = options.JpegQuality.ToString();
            var smoothing = options.EnableImageSmoothing ? "1" : "0";

            string itemMediaStreamHash = GetItemMediaStreamHash(item);

            string combinedChildrenMediaHash = "";
            if (options.ShowSeriesIconsIfAllEpisodesHaveLanguage && (item is Series || item is Season))
            {
                var query = new InternalItemsQuery
                {
                    Parent = item,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" }
                };
                var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();

                var episodeStreamHashes = episodes
                    .OrderBy(e => e.Id)
                    .Select(e => $"{e.Id}:{GetItemMediaStreamHash(e)}");

                var combinedHashString = string.Join(";", episodeStreamHashes);

                using (var sha = SHA256.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes(combinedHashString);
                    var hashBytes = sha.ComputeHash(bytes);
                    combinedChildrenMediaHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
                }
            }

            string cacheBusterVal = "0";

            return
              $"embyicons_{item.Id}_{imageType}" +
              $"_sz{options.IconSize}" +
              $"_libs{libs}" +
              $"_aAlign{aAlign}" +
              $"_sAlign{sAlign}" +
              $"_showA{showA}" +
              $"_showS{showS}" +
              $"_seriesOpt{seriesOption}" +
              $"_jpegq{jpegQuality}" +              // <--- JPEG Quality
              $"_smoothing{smoothing}" +            // <--- Image Smoothing
              $"_aVertOffset{aVertOffset}" +
              $"_sVertOffset{sVertOffset}" +
              $"_iconVer{_iconCacheVersion}" +
              $"_itemMediaHash{itemMediaStreamHash}" +
              $"_childrenMediaHash{combinedChildrenMediaHash}" +
              $"_cacheBuster{cacheBusterVal}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            if (!IsLibraryAllowed(item)) return null;
            return new() { RequiresTransparency = false };
        }

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize)
            => originalSize;

        Task IImageEnhancer.EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                              ImageType imageType, int imageIndex)
            => EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                            ImageType imageType, int imageIndex,
                                            CancellationToken cancellationToken)
        {
            if (!IsLibraryAllowed(item))
            {
                await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile);
                return;
            }

            var key = item.Id.ToString();

            var sem = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken);

            try
            {
                await EnhanceImageInternalAsync(item, inputFile, outputFile, imageType, imageIndex, cancellationToken);
            }
            finally
            {
                sem.Release();
            }
        }

        internal async Task<(HashSet<string>, HashSet<string>)> GetAggregatedLanguagesForSeriesAsync(
            Series series,
            PluginOptions options,
            CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (_seriesLangCache.TryGetValue(series.Id, out var cachedResult) &&
                (DateTime.UtcNow - cachedResult.timestamp) < SeriesLangCacheTTL)
            {
                _logger.Info($"[EmbyIconsEnhancer] Using cached aggregated languages for series {series.Name} ({series.Id})");
                return (cachedResult.audio, cachedResult.subtitle);
            }

            _logger.Info($"[EmbyIconsEnhancer] Aggregating languages for series {series.Name} ({series.Id})");

            var query = new InternalItemsQuery
            {
                Parent = series,
                Recursive = true,
                IncludeItemTypes = new[] { "Episode" }
            };

            var items = _libraryManager.GetItemList(query);
            var episodes = items.OfType<Episode>().ToList();

            if (episodes.Count == 0)
                return (new HashSet<string>(), new HashSet<string>());

            var episodeAudioSets = episodes.Select(ep => (ep.GetMediaStreams() ?? new List<MediaStream>())
                                                        .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                                                        .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                                                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
                                           .ToList();

            var episodeSubtitleSets = episodes.Select(ep => (ep.GetMediaStreams() ?? new List<MediaStream>())
                                                        .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                                                        .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                                                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
                                             .ToList();

            if (episodeAudioSets.Count == 0 && episodeSubtitleSets.Count == 0)
            {
                return (new HashSet<string>(), new HashSet<string>());
            }

            HashSet<string> commonAudio = episodeAudioSets.Any() ? new HashSet<string>(episodeAudioSets.First(), StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> commonSubs = episodeSubtitleSets.Any() ? new HashSet<string>(episodeSubtitleSets.First(), StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (episodeAudioSets.Count > 1)
            {
                foreach (var next in episodeAudioSets.Skip(1))
                {
                    commonAudio.IntersectWith(next);
                }
            }

            if (episodeSubtitleSets.Count > 1)
            {
                foreach (var next in episodeSubtitleSets.Skip(1))
                {
                    commonSubs.IntersectWith(next);
                }
            }

            var result = (commonAudio, commonSubs);
            _seriesLangCache.AddOrUpdate(series.Id, (result.commonAudio, result.commonSubs, DateTime.UtcNow),
                (key, old) => (result.commonAudio, result.commonSubs, DateTime.UtcNow));

            return result;
        }

        public void ClearOverlayCacheForItem(BaseItem item)
        {
            // Optional: implemented elsewhere in plugin
        }

        public void Dispose()
        {
            foreach (var sem in _locks.Values)
                sem.Dispose();

            _locks.Clear();
            _iconCacheManager.Dispose();
        }
    }
}
