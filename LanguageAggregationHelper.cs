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
        /// Aggregates detected languages for all episodes in a series.
        /// NOTE: This is called only during actual overlay/image calculation,
        /// NOT during plugin hot-path checks, so library lookups are safe here.
        /// </summary>
        internal Task<(HashSet<string>, HashSet<string>)> GetAggregatedLanguagesForSeriesAsync(
            Series series,
            PluginOptions options,
            CancellationToken cancellationToken)
        {
            var audioLangsAllowed = Helpers.LanguageHelper.ParseLanguageList(options.AudioLanguages)
                .Select(Helpers.LanguageHelper.NormalizeLangCode)
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            var subtitleLangsAllowed = Helpers.LanguageHelper.ParseLanguageList(options.SubtitleLanguages)
                .Select(Helpers.LanguageHelper.NormalizeLangCode)
                .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            var query = new InternalItemsQuery
            {
                Parent = series,
                Recursive = true,
                IncludeItemTypes = new[] { "Episode" }
            };

            // This _libraryManager call is safe ONLY because this method is NOT used in plugin hot-path checks
            var items = _libraryManager.GetItemList(query);
            var episodes = items.OfType<Episode>().ToList();

            if (episodes.Count == 0)
                return Task.FromResult((new HashSet<string>(), new HashSet<string>()));

            var audioLangsDetected = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var subtitleLangsDetected = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            var episodeAudioLangCache = new Dictionary<System.Guid, HashSet<string>>();
            var episodeSubtitleLangCache = new Dictionary<System.Guid, HashSet<string>>();

            foreach (var ep in episodes)
            {
                if (string.IsNullOrEmpty(ep.Path) || !System.IO.File.Exists(ep.Path))
                    continue;

                var epAudioLangs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                var epSubtitleLangs = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

                // Use only Emby's internal media stream info!
                var streams = ep.GetMediaStreams() ?? new List<MediaStream>();

                foreach (var stream in streams)
                {
                    if (stream.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = Helpers.LanguageHelper.NormalizeLangCode(stream.Language);
                        epAudioLangs.Add(norm);
                    }
                    if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.Language))
                    {
                        var norm = Helpers.LanguageHelper.NormalizeLangCode(stream.Language);
                        epSubtitleLangs.Add(norm);
                    }
                }

                episodeAudioLangCache[ep.Id] = epAudioLangs;
                episodeSubtitleLangCache[ep.Id] = epSubtitleLangs;

                cancellationToken.ThrowIfCancellationRequested();
            }

            // --- LOGGING PATCH: Print detected audio/subtitle languages per episode ---
            if (Plugin.Instance?.Logger != null)
            {
                foreach (var ep in episodes)
                {
                    var audios = episodeAudioLangCache.TryGetValue(ep.Id, out var a) ? string.Join(",", a) : "none";
                    var subs = episodeSubtitleLangCache.TryGetValue(ep.Id, out var s) ? string.Join(",", s) : "none";
                    // Logging removed for brevity, add if needed
                }
            }

            foreach (var lang in audioLangsAllowed)
            {
                bool allHaveLanguage = episodes.All(ep =>
                    episodeAudioLangCache.TryGetValue(ep.Id, out var langs) && langs.Contains(lang));

                if (allHaveLanguage)
                    audioLangsDetected.Add(lang);
            }

            foreach (var lang in subtitleLangsAllowed)
            {
                bool allHaveLanguage = episodes.All(ep =>
                    episodeSubtitleLangCache.TryGetValue(ep.Id, out var langs) && langs.Contains(lang));

                if (allHaveLanguage)
                    subtitleLangsDetected.Add(lang);
            }

            return Task.FromResult((audioLangsDetected, subtitleLangsDetected));
        }
    }
}
