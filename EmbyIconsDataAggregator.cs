using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    // Partial class for EmbyIconsEnhancer, focusing on data aggregation and internal caching.
    public partial class EmbyIconsEnhancer
    {
        // Cache for aggregated series info (common audio languages, subtitle languages, and channel count).
        private readonly ConcurrentDictionary<Guid, (HashSet<string> audio, HashSet<string> subtitle, int? commonChannels, DateTime timestamp)> _seriesLangCache = new();
        private static readonly TimeSpan SeriesLangCacheTTL = TimeSpan.FromMinutes(2); // Time-to-live for series aggregation cache.

        // Cache for individual item media stream hashes to optimize GetItemMediaStreamHash calls.
        private readonly ConcurrentDictionary<Guid, (string hash, DateTime timestamp)> _itemMediaStreamHashCache = new();
        private static readonly TimeSpan ItemMediaStreamHashCacheTTL = TimeSpan.FromMinutes(5); // Time-to-live for item stream hash cache.

        /// <summary>
        /// Computes a hash of an item's media streams (audio languages, subtitle languages, and highest channel count).
        /// This hash is used in the cache key to invalidate cached images if media streams change.
        /// </summary>
        /// <param name="item">The media item.</param>
        /// <returns>A short SHA256 hash string representing the media stream configuration.</returns>
        private string GetItemMediaStreamHash(BaseItem item)
        {
            // Try to retrieve the hash from the cache.
            if (_itemMediaStreamHashCache.TryGetValue(item.Id, out var cachedResult) &&
                (DateTime.UtcNow - cachedResult.timestamp) < ItemMediaStreamHashCacheTTL)
            {
                //_logger.Debug($"Using cached stream hash for item {item.Name} ({item.Id})");
                return cachedResult.hash;
            }

            var streams = item.GetMediaStreams() ?? new List<MediaStream>();
            // Collect and normalize audio languages, then order them.
            var audioLangs = streams
                .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                .OrderBy(l => l);

            // Collect and normalize subtitle languages, then order them.
            var subtitleLangs = streams
                .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                .OrderBy(l => l);

            // Determine the highest audio channel count for the item.
            int maxChannels = 0;
            foreach (var stream in streams)
            {
                if (stream.Type == MediaStreamType.Audio && stream.Channels.HasValue)
                    maxChannels = Math.Max(maxChannels, stream.Channels.Value);
            }

            // Combine all relevant stream info into a single string for hashing.
            var combinedString = string.Join(",", audioLangs) + ";" + string.Join(",", subtitleLangs) + $";{maxChannels}ch";

            // If no streams are found, use a default string to ensure a consistent hash.
            if (string.IsNullOrEmpty(combinedString))
                combinedString = "no_streams";

            // Compute and cache the SHA256 hash.
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(combinedString);
                var hashBytes = sha.ComputeHash(bytes);
                // Convert to hex string and take the first 8 characters for a shorter hash.
                var hash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8);
                _itemMediaStreamHashCache.AddOrUpdate(item.Id, (hash, DateTime.UtcNow), (key, old) => (hash, DateTime.UtcNow));
                return hash;
            }
        }

        /// <summary>
        /// Aggregates common audio languages, subtitle languages, and audio channel information for a series or season.
        /// This is used to display consistent icons on series/season posters.
        /// </summary>
        /// <param name="seriesOrSeason">The series or season item.</param>
        /// <param name="options">The plugin options.</param>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        /// <returns>A tuple containing common audio languages, common subtitle languages, and the common channel count.</returns>
        internal async Task<(HashSet<string> audio, HashSet<string> subtitle, int? commonChannels)> GetAggregatedInfoForSeriesAsync(
            BaseItem seriesOrSeason,
            PluginOptions options,
            CancellationToken cancellationToken)
        {
            await Task.Yield(); // Allow asynchronous execution.

            // Ensure we are working with a Series object, even if a Season is passed.
            Series? series = seriesOrSeason as Series ?? (seriesOrSeason as Season)?.Series;
            if (series == null)
            {
                _logger.Warn($"[EmbyIconsEnhancer] GetAggregatedInfoForSeriesAsync called with non-series/season item: {seriesOrSeason?.Name} ({seriesOrSeason?.Id}). Returning empty aggregation.");
                return (new HashSet<string>(), new HashSet<string>(), null);
            }

            // Check if aggregated info is already in cache and not expired.
            if (_seriesLangCache.TryGetValue(series.Id, out var cachedResult) &&
                (DateTime.UtcNow - cachedResult.timestamp) < SeriesLangCacheTTL)
            {
                _logger.Info($"[EmbyIconsEnhancer] Using cached aggregated info for series {series.Name} ({series.Id})");
                return (cachedResult.audio, cachedResult.subtitle, cachedResult.commonChannels);
            }

            _logger.Info($"[EmbyIconsEnhancer] Aggregating info for series {series.Name} ({series.Id})");

            // Query all episodes belonging to the series.
            var query = new InternalItemsQuery
            {
                Parent = series,
                Recursive = true,
                IncludeItemTypes = new[] { "Episode" }
            };

            var items = _libraryManager.GetItemList(query);
            var episodes = items.OfType<Episode>().ToList();

            HashSet<string> commonAudio = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> commonSubs = new(StringComparer.OrdinalIgnoreCase);
            int? commonChannels = null;

            // If no episodes are found, return empty results and cache them.
            if (episodes.Count == 0)
            {
                var resultEmpty = (commonAudio, commonSubs, commonChannels);
                _seriesLangCache.AddOrUpdate(series.Id, (resultEmpty.commonAudio, resultEmpty.commonSubs, resultEmpty.commonChannels, DateTime.UtcNow),
                    (key, old) => (resultEmpty.commonAudio, resultEmpty.commonSubs, resultEmpty.commonChannels, DateTime.UtcNow));
                return resultEmpty;
            }

            List<HashSet<string>> episodeAudioSets = new();
            List<HashSet<string>> episodeSubtitleSets = new();
            HashSet<int> highestChannelsPerEpisode = new();
            bool allEpisodesHaveAudioChannelInfo = true; // Flag to track if all episodes have channel info.

            // Iterate through each episode to collect its media stream information.
            foreach (var episode in episodes)
            {
                var episodeStreams = episode.GetMediaStreams() ?? new List<MediaStream>();

                var currentAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var currentSubtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                int episodeMaxChannels = 0;
                bool episodeHasAudioChannelInfo = false;

                foreach (var stream in episodeStreams)
                {
                    if (stream?.Type == MediaStreamType.Audio)
                    {
                        if (!string.IsNullOrEmpty(stream.Language))
                        {
                            currentAudioLangs.Add(Helpers.LanguageHelper.NormalizeLangCode(stream.Language));
                        }
                        if (stream.Channels.HasValue)
                        {
                            episodeMaxChannels = Math.Max(episodeMaxChannels, stream.Channels.Value);
                            episodeHasAudioChannelInfo = true;
                        }
                    }

                    if (stream?.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                    {
                        currentSubtitleLangs.Add(Helpers.LanguageHelper.NormalizeLangCode(stream.Language));
                    }
                }

                episodeAudioSets.Add(currentAudioLangs);
                episodeSubtitleSets.Add(currentSubtitleLangs);

                // If channel icons are enabled for series, track channel info per episode.
                if (options.ShowAudioChannelIcons && options.ShowChannelIconsIfAllEpisodesHaveChannelInfo)
                {
                    if (episodeMaxChannels == 0 && !episodeHasAudioChannelInfo)
                    {
                        allEpisodesHaveAudioChannelInfo = false; // Mark if any episode lacks channel info.
                    }
                    highestChannelsPerEpisode.Add(episodeMaxChannels);
                }
            }

            // Aggregate common audio languages if enabled.
            if (options.ShowAudioIcons || options.ShowSubtitleIcons || options.ShowSeriesIconsIfAllEpisodesHaveLanguage)
            {
                if (episodeAudioSets.Any())
                {
                    commonAudio = new HashSet<string>(episodeAudioSets.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (var next in episodeAudioSets.Skip(1))
                    {
                        commonAudio.IntersectWith(next); // Find common languages across all episodes.
                    }
                }

                // Aggregate common subtitle languages if enabled.
                if (episodeSubtitleSets.Any())
                {
                    commonSubs = new HashSet<string>(episodeSubtitleSets.First(), StringComparer.OrdinalIgnoreCase);
                    foreach (var next in episodeSubtitleSets.Skip(1))
                    {
                        commonSubs.IntersectWith(next); // Find common subtitles across all episodes.
                    }
                }
            }

            // Aggregate common audio channel count if enabled.
            if (options.ShowAudioChannelIcons && options.ShowChannelIconsIfAllEpisodesHaveChannelInfo)
            {
                // If all episodes have channel info and there's only one unique highest channel count, it's common.
                if (allEpisodesHaveAudioChannelInfo && highestChannelsPerEpisode.Count == 1)
                {
                    commonChannels = highestChannelsPerEpisode.First();
                }
            }

            var result = (commonAudio, commonSubs, commonChannels);
            // Cache the aggregated result for future use.
            _seriesLangCache.AddOrUpdate(series.Id, (result.commonAudio, result.commonSubs, result.commonChannels, DateTime.UtcNow),
                (key, old) => (result.commonAudio, result.commonSubs, result.commonChannels, DateTime.UtcNow));

            return result;
        }

        /// <summary>
        /// Clears the overlay cache for a specific item. (Currently not fully implemented here, handled elsewhere if needed).
        /// </summary>
        /// <param name="item">The media item.</param>
        public void ClearOverlayCacheForItem(BaseItem item)
        {
            // Optional: implemented elsewhere in plugin
        }
    }
}
