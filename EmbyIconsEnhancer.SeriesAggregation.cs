using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        private const int MaxSeriesCacheSize = 500;

        internal record AggregatedSeriesResult
        {
            public HashSet<string> AudioLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubtitleLangs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ChannelTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoCodecs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoFormats { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Resolutions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public string CombinedEpisodesHashShort { get; init; } = "";
            public DateTime Timestamp { get; init; } = DateTime.MinValue;
        }

        private void PruneSeriesAggregationCacheWithLimit()
        {
            if (_seriesAggregationCache.Count > MaxSeriesCacheSize)
            {
                var toRemove = _seriesAggregationCache.Count - MaxSeriesCacheSize;
                if (toRemove <= 0) return;

                var oldest = _seriesAggregationCache.ToArray()
                    .OrderBy(kvp => kvp.Value.Timestamp)
                    .Take(toRemove);

                foreach (var item in oldest)
                {
                    _seriesAggregationCache.TryRemove(item.Key, out _);
                }
                _logger.Info($"[EmbyIcons] Pruned {toRemove} items from the series aggregation cache.");
            }
        }

        internal AggregatedSeriesResult GetAggregatedDataForParentSync(BaseItem parent, PluginOptions options)
        {
            if (parent.Id == Guid.Empty)
            {
                _logger.Warn($"[EmbyIcons] Attempted to aggregate data for a series with an empty ID: {parent.Name}. Returning empty result.");
                return new AggregatedSeriesResult();
            }

            if (_seriesAggregationCache.TryGetValue(parent.Id, out var cachedResult))
            {
                _logger.Debug($"[EmbyIcons] Using cached aggregated data for series '{parent.Name}' ({parent.Id}).");
                return cachedResult;
            }

            _logger.Info($"[EmbyIcons] No valid cache found. Aggregating data for series '{parent.Name}' ({parent.Id}). LiteMode: {options.UseSeriesLiteMode}.");

            var query = new InternalItemsQuery
            {
                Parent = parent,
                Recursive = true,
                IncludeItemTypes = new[] { "Episode" },
                Limit = options.UseSeriesLiteMode ? 1 : null,
                OrderBy = options.UseSeriesLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
            };

            var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
            if (episodes.Count == 0)
            {
                _logger.Debug($"[EmbyIcons] No episodes found for series '{parent.Name}'. Caching empty result.");
                var emptyResult = new AggregatedSeriesResult { Timestamp = DateTime.UtcNow };
                _seriesAggregationCache.AddOrUpdate(parent.Id, emptyResult, (_, __) => emptyResult);
                return emptyResult;
            }


            var commonAudioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commonSubtitleLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commonAudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commonVideoCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? commonChannelType = null;
            string? commonResolution = null;

            bool allHaveDV = true;
            bool allHaveHdr10Plus = true;
            bool allHaveHdr = true;
            bool isFirstEpisode = true;

            var episodeHashes = new List<string>(episodes.Count);

            foreach (var ep in episodes)
            {
                var streams = ep.GetMediaStreams() ?? new List<MediaStream>();

                var currentAudioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentSubtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentAudioCodecs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Codec)).Select(s => MediaStreamHelper.GetAudioCodecIconName(s.Codec)).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentVideoCodecs = streams.Where(s => s.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.Codec)).Select(s => MediaStreamHelper.GetVideoCodecIconName(s.Codec)).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var currentChannel = MediaStreamHelper.GetChannelIconName(streams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max());
                var currentRes = MediaStreamHelper.GetResolutionIconNameFromStream(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video));

                if (isFirstEpisode)
                {
                    commonAudioLangs.UnionWith(currentAudioLangs);
                    commonSubtitleLangs.UnionWith(currentSubtitleLangs);
                    commonAudioCodecs.UnionWith(currentAudioCodecs);
                    commonVideoCodecs.UnionWith(currentVideoCodecs);
                    commonChannelType = currentChannel;
                    commonResolution = currentRes;
                    isFirstEpisode = false;
                }
                else
                {
                    commonAudioLangs.IntersectWith(currentAudioLangs);
                    commonSubtitleLangs.IntersectWith(currentSubtitleLangs);
                    commonAudioCodecs.IntersectWith(currentAudioCodecs);
                    commonVideoCodecs.IntersectWith(currentVideoCodecs);
                    if (commonChannelType != currentChannel) commonChannelType = null;
                    if (commonResolution != currentRes) commonResolution = null;
                }

                allHaveDV &= MediaStreamHelper.HasDolbyVision(ep, streams);
                allHaveHdr10Plus &= MediaStreamHelper.HasHdr10Plus(ep, streams);
                allHaveHdr &= MediaStreamHelper.HasHdr(ep, streams);

                episodeHashes.Add($"{ep.Id}:{MediaStreamHelper.GetItemMediaStreamHash(ep, streams)}");
            }


            var finalAudioLangs = (options.ShowAudioIcons && options.ShowSeriesIconsIfAllEpisodesHaveLanguage) ? commonAudioLangs : new HashSet<string>();
            var finalSubtitleLangs = (options.ShowSubtitleIcons && options.ShowSeriesIconsIfAllEpisodesHaveLanguage) ? commonSubtitleLangs : new HashSet<string>();
            var finalAudioCodecs = (options.ShowAudioCodecIcons) ? commonAudioCodecs : new HashSet<string>();
            var finalVideoCodecs = (options.ShowVideoCodecIcons) ? commonVideoCodecs : new HashSet<string>();
            var finalChannelTypes = (options.ShowAudioChannelIcons && commonChannelType != null) ? new HashSet<string> { commonChannelType } : new HashSet<string>();
            var finalResolutions = (options.ShowResolutionIcons && commonResolution != null) ? new HashSet<string> { commonResolution } : new HashSet<string>();
            var finalVideoFormats = new HashSet<string>();

            if (options.ShowVideoFormatIcons)
            {
                if (allHaveDV) finalVideoFormats.Add("dv");
                else if (allHaveHdr10Plus) finalVideoFormats.Add("hdr10plus");
                else if (allHaveHdr) finalVideoFormats.Add("hdr");
            }

            var combinedHashString = string.Join(";", episodeHashes.OrderBy(h => h));
            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(combinedHashString));

            var result = new AggregatedSeriesResult
            {
                Timestamp = DateTime.UtcNow,
                AudioLangs = finalAudioLangs,
                SubtitleLangs = finalSubtitleLangs,
                ChannelTypes = finalChannelTypes,
                AudioCodecs = finalAudioCodecs,
                VideoCodecs = finalVideoCodecs,
                Resolutions = finalResolutions,
                VideoFormats = finalVideoFormats,
                CombinedEpisodesHashShort = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 8)
            };

            _seriesAggregationCache.AddOrUpdate(parent.Id, result, (_, __) => result);
            PruneSeriesAggregationCacheWithLimit();
            return result;
        }

        internal Task<AggregatedSeriesResult> GetAggregatedDataForParentAsync(BaseItem parent, PluginOptions options, CancellationToken cancellationToken)
        {
            return Task.Run(() => GetAggregatedDataForParentSync(parent, options), cancellationToken);
        }
    }
}