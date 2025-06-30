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
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        internal class AggregatedSeriesResult
        {
            public HashSet<string> AudioLangs = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SubtitleLangs = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ChannelTypes = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoFormats = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Resolutions = new(StringComparer.OrdinalIgnoreCase);
            public string CombinedEpisodesHashShort = "";
            public DateTime Timestamp = DateTime.MinValue;
            public DateTimeOffset ParentDateModified = DateTimeOffset.MinValue;
        }

        internal AggregatedSeriesResult GetAggregatedDataForParentSync(BaseItem parent, PluginOptions options, bool ignoreCache = false)
        {
            if (!ignoreCache && _seriesAggregationCache.TryGetValue(parent.Id, out var cachedResult))
            {
                _logger.Debug($"[EmbyIcons] Using in-memory aggregated data for parent item {parent.Name} ({parent.Id}).");
                return cachedResult;
            }

            _logger.Debug($"[EmbyIcons] Re-aggregating data for series '{parent.Name}' ({parent.Id}).");

            List<Episode> episodes;
            if (options.UseSeriesLiteMode)
            {
                _logger.Debug($"[EmbyIcons] LITE MODE: Fetching first episode only for {parent.Name} ({parent.Id}).");
                var query = new InternalItemsQuery
                {
                    Parent = parent,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Episode" },
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                    Limit = 1
                };
                episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
            }
            else
            {
                _logger.Debug($"[EmbyIcons] FULL MODE: Aggregating data from all episodes for {parent.Name} ({parent.Id}).");
                var query = new InternalItemsQuery { Parent = parent, Recursive = true, IncludeItemTypes = new[] { "Episode" } };
                episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
            }
            var episodeCount = episodes.Count;

            if (episodeCount == 0)
            {
                var empty = new AggregatedSeriesResult { Timestamp = DateTime.UtcNow, ParentDateModified = parent.DateModified };
                _seriesAggregationCache.AddOrUpdate(parent.Id, empty, (_, __) => empty);
                return empty;
            }

            HashSet<string>? commonAudioLangs = null;
            HashSet<string>? commonSubtitleLangs = null;
            string? commonChannelType = null;
            string? commonResolution = null;

            bool allEpisodesHaveDV = true;
            bool allEpisodesHaveHDR10Plus = true;
            bool allEpisodesHaveHDR = true;
            bool isFirstEpisode = true;

            bool audioCheckActive = true;
            bool subtitleCheckActive = true;
            bool channelCheckActive = true;
            bool resolutionCheckActive = true;

            var episodeHashes = new List<string>(episodeCount);

            using var sha = SHA256.Create();

            foreach (var ep in episodes)
            {
                var streams = ep.GetMediaStreams() ?? new List<MediaStream>();
                var audioStreams = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();

                var audioLangs = audioStreams.Where(s => !string.IsNullOrEmpty(s.Language))
                                             .Select(s => LanguageHelper.NormalizeLangCode(s.Language))
                                             .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var subtitleLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                                           .Select(s => LanguageHelper.NormalizeLangCode(s.Language))
                                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var channelType = GetChannelIconName(audioStreams.Where(s => s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max()) ?? "none";
                var hasDV = HasDolbyVision(ep, streams);
                var hasHDR10Plus = HasHdr10Plus(ep);
                var hasHDR = HasHdr(ep, streams);
                var resolution = GetResolutionIconName(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Width, streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Height) ?? "none";

                string videoFormat;
                if (hasDV) videoFormat = "dv";
                else if (hasHDR10Plus) videoFormat = "hdr10plus";
                else if (hasHDR) videoFormat = "hdr";
                else videoFormat = "none";

                var hashString = $"{string.Join(",", audioLangs.OrderBy(x => x))};{string.Join(",", subtitleLangs.OrderBy(x => x))};{channelType};{videoFormat};{resolution}";
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(hashString))).Replace("-", "").Substring(0, 8);
                episodeHashes.Add($"{ep.Id}:{hash}");

                if (isFirstEpisode)
                {
                    commonAudioLangs = new HashSet<string>(audioLangs, StringComparer.OrdinalIgnoreCase);
                    commonSubtitleLangs = new HashSet<string>(subtitleLangs, StringComparer.OrdinalIgnoreCase);
                    commonChannelType = channelType;
                    commonResolution = resolution;
                    isFirstEpisode = false;
                }
                else
                {
                    if (audioCheckActive)
                    {
                        commonAudioLangs!.IntersectWith(audioLangs);
                        if (commonAudioLangs.Count == 0) audioCheckActive = false;
                    }
                    if (subtitleCheckActive)
                    {
                        commonSubtitleLangs!.IntersectWith(subtitleLangs);
                        if (commonSubtitleLangs.Count == 0) subtitleCheckActive = false;
                    }
                    if (channelCheckActive && commonChannelType != channelType)
                    {
                        commonChannelType = "none";
                        channelCheckActive = false;
                    }
                    if (resolutionCheckActive && commonResolution != resolution)
                    {
                        commonResolution = "none";
                        resolutionCheckActive = false;
                    }
                }

                allEpisodesHaveDV &= hasDV;
                allEpisodesHaveHDR10Plus &= hasHDR10Plus;
                allEpisodesHaveHDR &= hasHDR;
            }

            var result = new AggregatedSeriesResult();
            if (options.ShowAudioIcons && commonAudioLangs != null) result.AudioLangs = commonAudioLangs;
            if (options.ShowSubtitleIcons && commonSubtitleLangs != null) result.SubtitleLangs = commonSubtitleLangs;
            if (options.ShowAudioChannelIcons && commonChannelType != "none") result.ChannelTypes.Add(commonChannelType!);
            if (options.ShowResolutionIcons && commonResolution != "none") result.Resolutions.Add(commonResolution!);

            if (options.ShowVideoFormatIcons)
            {
                if (allEpisodesHaveDV) result.VideoFormats.Add("dv");
                else if (allEpisodesHaveHDR10Plus) result.VideoFormats.Add("hdr10plus");
                else if (allEpisodesHaveHDR) result.VideoFormats.Add("hdr");
            }

            var combinedHashString = string.Join(";", episodeHashes.OrderBy(h => h));
            var bytes = Encoding.UTF8.GetBytes(combinedHashString);
            result.CombinedEpisodesHashShort = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 8);
            result.Timestamp = DateTime.UtcNow;
            result.ParentDateModified = parent.DateModified;

            _seriesAggregationCache.AddOrUpdate(parent.Id, result, (_, __) => result);
            return result;
        }

        internal Task<(HashSet<string> AudioLangs, HashSet<string> SubtitleLangs, HashSet<string> ChannelTypes, HashSet<string> VideoFormats, HashSet<string> Resolutions)>
            GetAggregatedDataForParentAsync(BaseItem parent, PluginOptions options, System.Threading.CancellationToken cancellationToken)
        {
            var result = GetAggregatedDataForParentSync(parent, options, ignoreCache: false);
            return Task.FromResult((result.AudioLangs, result.SubtitleLangs, result.ChannelTypes, result.VideoFormats, result.Resolutions));
        }
    }
}