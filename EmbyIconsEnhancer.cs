using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
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

        private static string _iconCacheVersion = string.Empty;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, IUserViewManager userViewManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new ArgumentNullException(nameof(userViewManager));
            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30), maxParallelism: 4);
            _iconCacheManager.CacheRefreshedWithVersion += (sender, version) =>
            {
                _iconCacheVersion = version ?? string.Empty;
            };
        }

        // Uses the cached library ID set ONLY - never queries libraries directly.
        private bool IsLibraryAllowed(BaseItem item)
        {
            // This is ALWAYS safe in Emby 4.9+, because it's cached on config save.
            var allowedLibs = Plugin.Instance?.AllowedLibraryIds ?? new HashSet<string>();
            var libraryId = Helpers.FileUtils.GetLibraryIdForItem(_libraryManager, item);
            return allowedLibs.Count == 0 || (libraryId != null && allowedLibs.Contains(libraryId));
        }

        public Task RefreshIconCacheAsync(CancellationToken cancellationToken, bool force = false)
        {
            return _iconCacheManager.RefreshCacheOnDemandAsync(cancellationToken, force);
        }

        // ✅ Lower priority so CoverArt can take precedence
        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary) return false;
            if (item is Person) return false;
            // Only use cached allowedLibs!
            if (!IsLibraryAllowed(item)) return false;
            return item is Movie || item is Episode || item is Series ||
                   item is Season || item is BoxSet || item is MusicVideo;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            if (!IsLibraryAllowed(item))
                return ""; // No cache/fallback to default

            var options = Plugin.Instance?.GetConfiguredOptions();
            if (options == null) return "";

            var libs = (options.SelectedLibraries ?? "")
                        .Replace(',', '-')
                        .Replace(" ", "");

            string Norm(string s) => (s ?? "")
                                        .Replace(",", "-")
                                        .Replace(" ", "")
                                        .ToLowerInvariant();

            var aLangs = Norm(options.AudioLanguages);
            var sLangs = Norm(options.SubtitleLanguages);

            var aAlign = options.AudioIconAlignment.ToString();
            var sAlign = options.SubtitleIconAlignment.ToString();

            var showA = options.ShowAudioIcons ? "1" : "0";
            var showS = options.ShowSubtitleIcons ? "1" : "0";

            var series = options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? "1" : "0";

            var aVertOffset = options.AudioIconVerticalOffset.ToString();
            var sVertOffset = options.SubtitleIconVerticalOffset.ToString();

            string mediaFileTimestamp = "";
            string subtitleTimestamp = "";

            try
            {
                var path = item.Path;

                if (!string.IsNullOrEmpty(path))
                {
                    if (File.Exists(path))
                    {
                        mediaFileTimestamp = File.GetLastWriteTimeUtc(path).Ticks.ToString();
                    }
                    subtitleTimestamp = "";
                }
            }
            catch (Exception)
            {
            }

            // --------- PER-SERIES/SEASON CHILD LANGUAGES HASH ----------
            string childLangsHash = "";

            // This block is safe, as it is only executed for overlays
            if (item is Series || item is Season)
            {
                try
                {
                    var query = new InternalItemsQuery
                    {
                        Parent = item,
                        Recursive = true,
                        IncludeItemTypes = new[] { "Episode" }
                    };
                    var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();

                    var langs = episodes
                        .AsParallel().OrderBy(e => e.Id)
                        .Select(e =>
                        {
                            var audio = string.Join(",", (e.GetMediaStreams() ?? new List<MediaBrowser.Model.Entities.MediaStream>())
                                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                                .Select(s => s.Language));
                            var subs = string.Join(",", (e.GetMediaStreams() ?? new List<MediaBrowser.Model.Entities.MediaStream>())
                                .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                                .Select(s => s.Language));
                            return $"{e.Id}:{audio}|{subs}";
                        });

                    var combined = string.Join(";", langs);

                    using (var sha = SHA256.Create())
                    {
                        var bytes = Encoding.UTF8.GetBytes(combined);
                        var hashBytes = sha.ComputeHash(bytes);
                        childLangsHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
                    }
                }
                catch (Exception ex)
                {
                    childLangsHash = "err";
                    Plugin.Instance?.Logger?.ErrorException("Error hashing child languages", ex);
                }
            }

            string cacheBusterVal = "0";

            return
              $"embyicons_{item.Id}_{imageType}" +
              $"_sz{options.IconSize}" +
              $"_libs{libs}" +
              $"_aLangs{aLangs}" +
              $"_sLangs{sLangs}" +
              $"_aAlign{aAlign}" +
              $"_sAlign{sAlign}" +
              $"_showA{showA}" +
              $"_showS{showS}" +
              $"_series{series}" +
              $"_aVertOffset{aVertOffset}" +
              $"_sVertOffset{sVertOffset}" +
              $"_mediaTS{mediaFileTimestamp}" +
              $"_subsTS{subtitleTimestamp}" +
              $"_iconVer{_iconCacheVersion}" +
              $"_childLangs{childLangsHash}" +
              $"_cacheBuster{cacheBusterVal}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            if (!IsLibraryAllowed(item)) return null;
            return new() { RequiresTransparency = true };
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
                // Copy the original image, or do nothing, to ensure fallback works gracefully.
                // await Helpers.FileUtils.SafeCopyAsync(inputFile!, outputFile); // You may want to enable this if needed.
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
