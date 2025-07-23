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
                var oldest = _seriesAggregationCache.ToArray().OrderBy(kvp => kvp.Value.Timestamp).Take(toRemove);
                foreach (var item in oldest) _seriesAggregationCache.TryRemove(item.Key, out _);
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Pruned {toRemove} items from the series aggregation cache.");
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
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Using cached aggregated data for series '{parent.Name}' ({parent.Id}).");
                return cachedResult;
            }

            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] No valid cache found. Aggregating data for series '{parent.Name}' ({parent.Id}). LiteMode: {options.UseSeriesLiteMode}.");

            var query = new InternalItemsQuery
            {
                Parent = parent,
                Recursive = true,
                IncludeItemTypes = new[] { EmbyIcons.Constants.Episode },
                Limit = options.UseSeriesLiteMode ? 1 : null,
                OrderBy = options.UseSeriesLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>()
            };

            var episodes = _libraryManager.GetItemList(query);
            if (!episodes.Any())
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] No episodes found for series '{parent.Name}'. Caching empty result.");
                var emptyResult = new AggregatedSeriesResult { Timestamp = DateTime.UtcNow };
                _seriesAggregationCache.AddOrUpdate(parent.Id, emptyResult, (_, __) => emptyResult);
                return emptyResult;
            }

            var episodeList = episodes.ToList();

            var firstEpisode = episodeList[0];
            var firstStreams = firstEpisode.GetMediaStreams() ?? new List<MediaStream>();

            var commonAudioLangs = firstStreams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commonSubtitleLangs = firstStreams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commonAudioCodecs = firstStreams.Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commonVideoCodecs = firstStreams.Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(name => name != null).Select(name => name!).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var primaryAudioStream = firstStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
            var commonChannelType = primaryAudioStream != null ? MediaStreamHelper.GetChannelIconName(primaryAudioStream) : null;

            _iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder).TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutionKeys);
            var commonResolution = MediaStreamHelper.GetResolutionIconNameFromStream(firstStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video), knownResolutionKeys ?? new List<string>());

            var episodeHashes = new List<string>(episodeList.Count) { $"{firstEpisode.Id}:{MediaStreamHelper.GetItemMediaStreamHash(firstEpisode, firstStreams)}" };

            for (int i = 1; i < episodeList.Count; i++)
            {
                var ep = episodeList[i];
                var streams = ep.GetMediaStreams() ?? new List<MediaStream>();

                commonAudioLangs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage)));
                commonSubtitleLangs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage)));
                commonAudioCodecs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(name => name != null).Select(name => name!));
                commonVideoCodecs.IntersectWith(streams.Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(name => name != null).Select(name => name!));

                var currentPrimaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
                if (commonChannelType != (currentPrimaryAudio != null ? MediaStreamHelper.GetChannelIconName(currentPrimaryAudio) : null)) commonChannelType = null;

                var currentRes = MediaStreamHelper.GetResolutionIconNameFromStream(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video), knownResolutionKeys ?? new List<string>());
                if (commonResolution != currentRes) commonResolution = null;

                episodeHashes.Add($"{ep.Id}:{MediaStreamHelper.GetItemMediaStreamHash(ep, streams)}");
            }

            var finalAudioLangs = (options.ShowAudioIcons && options.ShowSeriesIconsIfAllEpisodesHaveLanguage) ? commonAudioLangs : new HashSet<string>();
            var finalSubtitleLangs = (options.ShowSubtitleIcons && options.ShowSeriesIconsIfAllEpisodesHaveLanguage) ? commonSubtitleLangs : new HashSet<string>();
            var finalAudioCodecs = (options.ShowAudioCodecIcons) ? commonAudioCodecs : new HashSet<string>();
            var finalVideoCodecs = (options.ShowVideoCodecIcons) ? commonVideoCodecs : new HashSet<string>();
            var finalChannelTypes = (options.ShowAudioChannelIcons && commonChannelType != null) ? new HashSet<string> { commonChannelType } : new HashSet<string>();
            var finalResolutions = (options.ShowResolutionIcons && commonResolution != null) ? new HashSet<string> { commonResolution } : new HashSet<string>();

            var finalVideoFormats = new HashSet<string>();
            if (options.ShowVideoFormatIcons && episodeList.Any())
            {
                var episodeHdrStates = episodeList.Select(ep =>
                {
                    var streams = ep.GetMediaStreams() ?? new List<MediaStream>();
                    return MediaStreamHelper.GetVideoFormatIconName(ep, streams);
                }).ToList();

                if (!episodeHdrStates.Contains(null))
                {
                    var distinctFormats = episodeHdrStates.Where(s => s != null).Distinct().ToList();
                    if (distinctFormats.Count > 1)
                    {
                        finalVideoFormats.Add("hdr");
                    }
                    else if (distinctFormats.Count == 1)
                    {
                        finalVideoFormats.Add(distinctFormats.First()!);
                    }
                }
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
    }
}