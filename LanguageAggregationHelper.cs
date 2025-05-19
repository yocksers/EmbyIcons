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
        internal async Task<(HashSet<string>, HashSet<string>)> GetAggregatedLanguagesForSeriesAsync(Series series, PluginOptions options, CancellationToken cancellationToken)
        {
            var audioLangsAllowed = Helpers.LanguageHelper.ParseLanguageList(options.AudioLanguages)
                .Select(Helpers.LanguageHelper.NormalizeLangCode).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

            var subtitleLangsAllowed = Helpers.LanguageHelper.ParseLanguageList(options.SubtitleLanguages)
                .Select(Helpers.LanguageHelper.NormalizeLangCode).ToHashSet(System.StringComparer.OrdinalIgnoreCase);

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

                await Helpers.MediaInfoDetector.DetectLanguagesFromMediaAsync(ep.Path!, epAudioLangs, epSubtitleLangs, options.EnableLogging);

                Helpers.SubtitleScanner.ScanExternalSubtitles(
                    ep.Path!,
                    epSubtitleLangs,
                    options.EnableLogging,
                    options.SubtitleFileExtensions?.Split(',', System.StringSplitOptions.RemoveEmptyEntries) ?? new[] { ".srt" });

                episodeAudioLangCache[ep.Id] = epAudioLangs;
                episodeSubtitleLangCache[ep.Id] = epSubtitleLangs;

                cancellationToken.ThrowIfCancellationRequested();
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

            if (!options.ShowAudioIcons)
                audioLangsDetected.Clear();

            if (!options.ShowSubtitleIcons)
                subtitleLangsDetected.Clear();

            return (audioLangsDetected, subtitleLangsDetected);
        }
    }
}