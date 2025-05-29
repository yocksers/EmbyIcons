using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer
    {
        /// <summary>
        /// Aggregates common (present-in-all-episodes) audio and subtitle languages for a series.
        /// </summary>
        internal Task<(HashSet<string>, HashSet<string>)> GetAggregatedLanguagesForSeriesAsync(
            Series series,
            PluginOptions options,
            CancellationToken cancellationToken)
        {
            var query = new InternalItemsQuery
            {
                Parent = series,
                Recursive = true,
                IncludeItemTypes = new[] { "Episode" }
            };

            var items = _libraryManager.GetItemList(query);
            var episodes = items.OfType<Episode>().ToList();

            if (episodes.Count == 0)
                return Task.FromResult((new HashSet<string>(), new HashSet<string>()));

            // Collect per-episode language sets
            var episodeAudioSets = episodes.Select(ep => (ep.GetMediaStreams() ?? new List<MediaStream>())
                                                        .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language))
                                                        .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                                                        .ToHashSet(System.StringComparer.OrdinalIgnoreCase))
                                           .ToList();

            var episodeSubtitleSets = episodes.Select(ep => (ep.GetMediaStreams() ?? new List<MediaStream>())
                                                        .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language))
                                                        .Select(s => Helpers.LanguageHelper.NormalizeLangCode(s.Language))
                                                        .ToHashSet(System.StringComparer.OrdinalIgnoreCase))
                                             .ToList();

            // Intersect to get only languages present in ALL episodes
            var commonAudio = episodeAudioSets.Skip(1).Aggregate(
                new HashSet<string>(episodeAudioSets.First()), (h, next) => { h.IntersectWith(next); return h; });
            var commonSubs = episodeSubtitleSets.Skip(1).Aggregate(
                new HashSet<string>(episodeSubtitleSets.First()), (h, next) => { h.IntersectWith(next); return h; });

            return Task.FromResult((commonAudio, commonSubs));
        }
    }
}
