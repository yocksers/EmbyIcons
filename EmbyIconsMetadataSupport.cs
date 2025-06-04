using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    // Partial class for EmbyIconsEnhancer, focusing on metadata and configuration support.
    public partial class EmbyIconsEnhancer
    {
        /// <summary>
        /// Checks if the given item's library is allowed for icon overlays based on plugin settings.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <returns>True if the library is allowed, false otherwise.</returns>
        private bool IsLibraryAllowed(BaseItem item)
        {
            var allowedLibs = Plugin.Instance?.AllowedLibraryIds ?? new HashSet<string>();
            var libraryId = Helpers.FileUtils.GetLibraryIdForItem(_libraryManager, item);
            // If no libraries are selected (allowedLibs is empty), all libraries are allowed.
            // Otherwise, check if the item's library ID is in the allowed list.
            return allowedLibs.Count == 0 || (libraryId != null && allowedLibs.Contains(libraryId));
        }

        /// <summary>
        /// Gets the priority of this image enhancer.
        /// </summary>
        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        /// <summary>
        /// Determines if this enhancer supports the given item and image type.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="imageType">The type of the image.</param>
        /// <returns>True if supported, false otherwise.</returns>
        public bool Supports(BaseItem? item, ImageType imageType)
        {
            // Only support primary image types and non-Person items.
            if (item == null || imageType != ImageType.Primary) return false;
            if (item is Person) return false;
            // Check if the item's library is allowed.
            if (!IsLibraryAllowed(item)) return false;

            var options = Plugin.Instance?.GetConfiguredOptions();
            if (options == null) return false; // Ensure options are available.

            bool showEpisodes = options.ShowOverlaysForEpisodes;

            // Handle episode-specific support.
            if (item is Episode)
                return showEpisodes;

            // Check if any icon type is enabled for general items (movies, series, etc.).
            if (!options.ShowAudioIcons && !options.ShowSubtitleIcons && !options.ShowAudioChannelIcons)
                return false;

            // Support for various media item types.
            return item is Movie
                || item is Series
                || item is Season
                || item is BoxSet
                || item is MusicVideo;
        }

        /// <summary>
        /// Generates a unique cache key for the enhanced image based on item, image type, and plugin options.
        /// This ensures that the image is re-enhanced only when relevant settings or media streams change.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="imageType">The type of the image.</param>
        /// <returns>A string representing the configuration cache key.</returns>
        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            // If the library is not allowed, return an empty key to prevent caching.
            if (!IsLibraryAllowed(item))
                return "";

            var options = Plugin.Instance?.GetConfiguredOptions();
            if (options == null) return "";

            // Sanitize selected libraries for the cache key.
            var libs = (options.SelectedLibraries ?? "")
                        .Replace(',', '-')
                        .Replace(" ", "");

            // Include various plugin options in the cache key.
            var aAlign = options.AudioIconAlignment.ToString();
            var sAlign = options.SubtitleIconAlignment.ToString();
            var cAlign = options.ChannelIconAlignment.ToString();

            var showA = options.ShowAudioIcons ? "1" : "0";
            var showS = options.ShowSubtitleIcons ? "1" : "0";
            var showC = options.ShowAudioChannelIcons ? "1" : "0";

            var seriesLangOption = options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? "1" : "0";
            var seriesChannelOption = options.ShowChannelIconsIfAllEpisodesHaveChannelInfo ? "1" : "0";

            var aVertOffset = options.AudioIconVerticalOffset.ToString();
            var sVertOffset = options.SubtitleIconVerticalOffset.ToString();
            var cVertOffset = options.ChannelIconVerticalOffset.ToString();

            var jpegQuality = options.JpegQuality.ToString();
            var smoothing = options.EnableImageSmoothing ? "1" : "0";

            // Include a hash of the item's media streams (languages, channels) for individual items.
            string itemMediaStreamHash = GetItemMediaStreamHash(item); // This call is now cached internally.

            string combinedChildrenMediaHash = ""; // For aggregated languages in series/seasons.
            string combinedChildrenChannelHash = ""; // For aggregated channels in series/seasons.

            // If series aggregation options are enabled, calculate hashes based on episodes.
            if ((options.ShowSeriesIconsIfAllEpisodesHaveLanguage || options.ShowChannelIconsIfAllEpisodesHaveChannelInfo) && (item is Series || item is Season))
            {
                var query = new InternalItemsQuery
                {
                    Parent = item,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" }
                };
                var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();

                if (options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
                {
                    // Generate a hash based on each episode's media stream hash.
                    var episodeStreamHashes = episodes
                        .OrderBy(e => e.Id)
                        .Select(e => $"{e.Id}:{GetItemMediaStreamHash(e)}"); // Uses cached hash.

                    var combinedHashString = string.Join(";", episodeStreamHashes);
                    using (var sha = SHA256.Create())
                    {
                        var bytes = Encoding.UTF8.GetBytes(combinedHashString);
                        var hashBytes = sha.ComputeHash(bytes);
                        combinedChildrenMediaHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
                    }
                }

                if (options.ShowAudioChannelIcons && options.ShowChannelIconsIfAllEpisodesHaveChannelInfo)
                {
                    // Generate a hash based on each episode's highest channel count.
                    var episodeChannelCounts = episodes
                        .OrderBy(e => e.Id)
                        .Select(e =>
                        {
                            var episodeMaxChannels = 0;
                            var streams = e.GetMediaStreams() ?? new List<MediaStream>();
                            foreach (var stream in streams)
                            {
                                if (stream?.Type == MediaStreamType.Audio && stream.Channels.HasValue)
                                {
                                    episodeMaxChannels = Math.Max(episodeMaxChannels, stream.Channels.Value);
                                }
                            }
                            return $"{e.Id}:{episodeMaxChannels}";
                        });
                    var combinedChannelHashString = string.Join(";", episodeChannelCounts);
                    using (var sha = SHA256.Create())
                    {
                        var bytes = Encoding.UTF8.GetBytes(combinedChannelHashString);
                        var hashBytes = sha.ComputeHash(bytes);
                        combinedChildrenChannelHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
                    }
                }
            }

            string cacheBusterVal = "0"; // A general cache buster, can be incremented for forced cache clears.

            // Construct the final cache key string.
            return
              $"embyicons_{item.Id}_{imageType}" +
              $"_sz{options.IconSize}" +
              $"_libs{libs}" +
              $"_aAlign{aAlign}" +
              $"_sAlign{sAlign}" +
              $"_cAlign{cAlign}" +
              $"_showA{showA}" +
              $"_showS{showS}" +
              $"_showC{showC}" +
              $"_seriesLangOpt{seriesLangOption}" +
              $"_seriesChanOpt{seriesChannelOption}" +
              $"_jpegq{jpegQuality}" +
              $"_smoothing{smoothing}" +
              $"_aVertOffset{aVertOffset}" +
              $"_sVertOffset{sVertOffset}" +
              $"_cVertOffset{cVertOffset}" +
              $"_iconVer{_iconCacheVersion}" + // Include icon cache version to invalidate if icons change.
              $"_itemMediaHash{itemMediaStreamHash}" +
              $"_childrenMediaHash{combinedChildrenMediaHash}" +
              $"_childrenChannelHash{combinedChildrenChannelHash}" +
              $"_cacheBuster{cacheBusterVal}";
        }

        /// <summary>
        /// Gets information about the enhanced image, such as whether it requires transparency.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="inputFile">The path to the input image file.</param>
        /// <param name="imageType">The type of the image.</param>
        /// <param name="imageIndex">The index of the image.</param>
        /// <returns>An <see cref="EnhancedImageInfo"/> object, or null if not supported.</returns>
        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            if (!IsLibraryAllowed(item)) return null;
            return new() { RequiresTransparency = false }; // Our overlays don't introduce transparency requirements.
        }

        /// <summary>
        /// Gets the size of the enhanced image. In this case, it remains the original size.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <param name="imageType">The type of the image.</param>
        /// <param name="imageIndex">The index of the image.</param>
        /// <param name="originalSize">The original size of the image.</param>
        /// <returns>The original image size.</returns>
        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize)
            => originalSize;

        /// <summary>
        /// Refreshes the icon cache on demand.
        /// </summary>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <param name="force">If true, forces a refresh even if the cache TTL has not expired.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        public Task RefreshIconCacheAsync(CancellationToken cancellationToken, bool force = false)
        {
            return _iconCacheManager.RefreshCacheOnDemandAsync(cancellationToken, force);
        }
    }
}
