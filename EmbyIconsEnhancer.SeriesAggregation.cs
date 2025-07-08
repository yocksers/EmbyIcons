﻿using EmbyIcons.Helpers;
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

        internal AggregatedSeriesResult GetAggregatedDataForParentSync(BaseItem parent, PluginOptions options)
        {
            if (parent is Series series && series.Id == Guid.Empty && series.InternalId > 0)
            {
                var fullSeries = _libraryManager.GetItemById(series.InternalId);
                if (fullSeries != null)
                {
                    parent = fullSeries;
                }
            }

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

            _logger.Debug($"[EmbyIcons] No valid cache found. Aggregating data for series '{parent.Name}' ({parent.Id}). LiteMode: {options.UseSeriesLiteMode}.");

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

            var firstEpisode = episodes[0];
            var firstStreams = firstEpisode.GetMediaStreams() ?? new List<MediaStream>();

            var commonAudioLangs = firstStreams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                                               .Select(s => LanguageHelper.NormalizeLangCode(s.Language))
                                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var commonSubtitleLangs = firstStreams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                                                  .Select(s => LanguageHelper.NormalizeLangCode(s.Language))
                                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var commonAudioCodecs = firstStreams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Codec))
                                                .Select(s => GetAudioCodecIconName(s.Codec)).Where(name => name != null).Select(name => name!)
                                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var commonVideoCodecs = firstStreams.Where(s => s.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.Codec))
                                                .Select(s => GetVideoCodecIconName(s.Codec)).Where(name => name != null).Select(name => name!)
                                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var commonChannelType = GetChannelIconName(firstStreams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max());
            var commonResolution = GetResolutionIconNameFromStream(firstStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video));

            bool allHaveDV = HasDolbyVision(firstEpisode, firstStreams);
            bool allHaveHdr10Plus = HasHdr10Plus(firstEpisode, firstStreams);
            bool allHaveHdr = HasHdr(firstEpisode, firstStreams);

            var episodeHashes = new List<string>(episodes.Count);

            for (int i = 0; i < episodes.Count; i++)
            {
                var ep = episodes[i];
                var streams = (i == 0) ? firstStreams : (ep.GetMediaStreams() ?? new List<MediaStream>());

                if (i > 0)
                {
                    if (commonAudioLangs.Count > 0)
                    {
                        var currentAudio = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language));
                        commonAudioLangs.IntersectWith(currentAudio);
                    }
                    if (commonSubtitleLangs.Count > 0)
                    {
                        var currentSubs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language));
                        commonSubtitleLangs.IntersectWith(currentSubs);
                    }
                    if (commonAudioCodecs.Count > 0)
                    {
                        var currentCodecs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Codec)).Select(s => GetAudioCodecIconName(s.Codec)).Where(name => name != null).Select(name => name!);
                        commonAudioCodecs.IntersectWith(currentCodecs);
                    }
                    if (commonVideoCodecs.Count > 0)
                    {
                        var currentCodecs = streams.Where(s => s.Type == MediaStreamType.Video && !string.IsNullOrEmpty(s.Codec)).Select(s => GetVideoCodecIconName(s.Codec)).Where(name => name != null).Select(name => name!);
                        commonVideoCodecs.IntersectWith(currentCodecs);
                    }
                    if (commonChannelType != null)
                    {
                        var currentChannel = GetChannelIconName(streams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max());
                        if (commonChannelType != currentChannel) commonChannelType = null;
                    }
                    if (commonResolution != null)
                    {
                        var currentRes = GetResolutionIconNameFromStream(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video));
                        if (commonResolution != currentRes) commonResolution = null;
                    }

                    allHaveDV &= HasDolbyVision(ep, streams);
                    allHaveHdr10Plus &= HasHdr10Plus(ep, streams);
                    allHaveHdr &= HasHdr(ep, streams);
                }

                episodeHashes.Add($"{ep.Id}:{GetItemMediaStreamHash(ep, streams)}");
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
            return result;
        }

        internal async Task<(HashSet<string> AudioLangs, HashSet<string> SubtitleLangs, HashSet<string> ChannelTypes, HashSet<string> VideoFormats, HashSet<string> Resolutions, HashSet<string> AudioCodecs, HashSet<string> VideoCodecs)>
            GetAggregatedDataForParentAsync(BaseItem parent, PluginOptions options, CancellationToken cancellationToken)
        {
            var result = await Task.Run(() => GetAggregatedDataForParentSync(parent, options), cancellationToken);
            return (result.AudioLangs, result.SubtitleLangs, result.ChannelTypes, result.VideoFormats, result.Resolutions, result.AudioCodecs, result.VideoCodecs);
        }
    }
}