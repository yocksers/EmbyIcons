using EmbyIcons.Api;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.SeriesTroubleshooter, "GET", Summary = "Finds inconsistencies in episodes of a series")]
    public class GetSeriesTroubleshooterReport : IReturn<List<SeriesTroubleshooterReport>>
    {
        [ApiMember(Name = "SeriesId", Description = "The ID of the series to check. If omitted, all series will be checked.", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? SeriesId { get; set; }

        [ApiMember(Name = "ChecksToRun", Description = "A comma-separated list of checks to perform (e.g., AudioLanguage,Resolution). If omitted, all checks are run.", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? ChecksToRun { get; set; }
    }

    #region Report Models
    public class SeriesTroubleshooterReport
    {
        public string SeriesName { get; set; } = string.Empty;
        public string SeriesId { get; set; } = string.Empty;
        public int TotalEpisodes { get; set; }
        public List<CheckResult> Checks { get; set; } = new List<CheckResult>();
    }

    public class CheckResult
    {
        public string CheckName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<string> DominantValues { get; set; } = new List<string>();
        public List<MismatchedEpisodeInfo> MismatchedEpisodes { get; set; } = new List<MismatchedEpisodeInfo>();
    }

    public class MismatchedEpisodeInfo
    {
        public string EpisodeName { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public List<string> Actual { get; set; } = new List<string>();
    }
    #endregion

    public class SeriesTroubleshooterService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IconCacheManager _iconCacheManager;

        private static class CheckNames
        {
            public const string AudioLanguage = "AudioLanguage";
            public const string Subtitles = "Subtitles";
            public const string AudioCodec = "AudioCodec";
            public const string VideoCodec = "VideoCodec";
            public const string AudioChannels = "AudioChannels";
            public const string Resolution = "Resolution";
            public const string AspectRatio = "AspectRatio";
            public const string VideoFormat = "VideoFormat";
        }

        public SeriesTroubleshooterService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
            _iconCacheManager = Plugin.Instance?.Enhancer._iconCacheManager ?? throw new InvalidOperationException("IconCacheManager not available");
        }

        public object Get(GetSeriesTroubleshooterReport request)
        {
            var reports = new List<SeriesTroubleshooterReport>();

            var requestedChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(request.ChecksToRun))
            {
                requestedChecks.UnionWith(request.ChecksToRun.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
            bool runAllChecks = !requestedChecks.Any();

            if (!string.IsNullOrEmpty(request.SeriesId) && _libraryManager.GetItemById(request.SeriesId) is Series seriesItem)
            {
                var report = GenerateReportForSeries(seriesItem, requestedChecks, runAllChecks);
                if (report.Checks.Any(c => c.Status == "Mismatch"))
                {
                    reports.Add(report);
                }
                return reports;
            }

            var allSeries = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Series" }, Recursive = true })
                                           .OfType<Series>()
                                           .ToDictionary(s => s.InternalId);

            var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Episode" }, Recursive = true })
                                             .OfType<Episode>();

            var episodesBySeries = allEpisodes.GroupBy(e => e.SeriesId);

            foreach (var seriesGroup in episodesBySeries)
            {
                if (allSeries.TryGetValue(seriesGroup.Key, out var series))
                {
                    var report = GenerateReportForSeries(series, requestedChecks, runAllChecks, seriesGroup.ToList());
                    if (report.Checks.Any(c => c.Status == "Mismatch"))
                    {
                        reports.Add(report);
                    }
                }
            }

            return reports.OrderBy(r => r.SeriesName, StringComparer.CurrentCulture).ToList();
        }

        private SeriesTroubleshooterReport GenerateReportForSeries(Series series, HashSet<string> requestedChecks, bool runAllChecks, List<Episode>? episodes = null)
        {
            var report = new SeriesTroubleshooterReport
            {
                SeriesName = series.Name,
                SeriesId = series.Id.ToString()
            };

            episodes ??= _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = series,
                Recursive = true,
                IncludeItemTypes = new[] { "Episode" }
            }).OfType<Episode>().ToList();

            report.TotalEpisodes = episodes.Count;
            if (episodes.Count == 0) return report;

            var config = Plugin.Instance?.Configuration ?? new PluginOptions();
            var knownResolutions = _iconCacheManager.GetAllAvailableIconKeys(config.IconsFolder)
                                    .GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());

            var baseItems = episodes.Cast<BaseItem>().ToList();

            if (runAllChecks || requestedChecks.Contains(CheckNames.AudioLanguage))
                report.Checks.Add(CheckProperty(baseItems, "Audio Language", ep => ep.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage)).ToList()));

            if (runAllChecks || requestedChecks.Contains(CheckNames.Subtitles))
                report.Checks.Add(CheckProperty(baseItems, "Subtitles", ep => ep.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage)).Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage)).ToList()));

            if (runAllChecks || requestedChecks.Contains(CheckNames.AudioCodec))
                report.Checks.Add(CheckProperty(baseItems, "Audio Codec", ep => ep.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).Select(MediaStreamHelper.GetAudioCodecIconName).Where(c => c != null).Select(c => c!).Distinct().ToList()));

            if (runAllChecks || requestedChecks.Contains(CheckNames.VideoCodec))
                report.Checks.Add(CheckProperty(baseItems, "Video Codec", ep => ep.GetMediaStreams().Where(s => s.Type == MediaStreamType.Video).Select(MediaStreamHelper.GetVideoCodecIconName).Where(c => c != null).Select(c => c!).Distinct().ToList()));

            if (runAllChecks || requestedChecks.Contains(CheckNames.AudioChannels))
                report.Checks.Add(CheckProperty(baseItems, "Audio Channels", ep => {
                    var stream = ep.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
                    var channelName = stream != null ? MediaStreamHelper.GetChannelIconName(stream) : null;
                    return channelName != null ? new List<string> { channelName } : new List<string>();
                }));

            if (runAllChecks || requestedChecks.Contains(CheckNames.Resolution))
                report.Checks.Add(CheckProperty(baseItems, "Resolution", ep => {
                    var stream = ep.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
                    var resName = stream != null ? MediaStreamHelper.GetResolutionIconNameFromStream(stream, knownResolutions) : null;
                    return resName != null ? new List<string> { resName } : new List<string>();
                }));

            if (runAllChecks || requestedChecks.Contains(CheckNames.AspectRatio))
                report.Checks.Add(CheckProperty(baseItems, "Aspect Ratio", ep => {
                    var stream = ep.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
                    var arName = stream != null ? MediaStreamHelper.GetAspectRatioIconName(stream, true) : null;
                    return arName != null ? new List<string> { arName } : new List<string>();
                }));

            if (runAllChecks || requestedChecks.Contains(CheckNames.VideoFormat))
                report.Checks.Add(CheckProperty(baseItems, "Video Format (HDR)", ep => {
                    var formatName = MediaStreamHelper.GetVideoFormatIconName(ep, ep.GetMediaStreams());
                    return formatName != null ? new List<string> { formatName } : new List<string>();
                }));

            return report;
        }

        private CheckResult CheckProperty(List<BaseItem> episodes, string checkName, Func<BaseItem, List<string>> valueExtractor)
        {
            var checkResult = new CheckResult { CheckName = checkName };

            var valuesByEpisode = episodes
                .Select(ep => new { Episode = ep, Values = valueExtractor(ep)?.Where(v => v != null).Select(v => v.ToLowerInvariant()).OrderBy(v => v).ToList() ?? new List<string>() })
                .ToList();

            if (!valuesByEpisode.Any())
            {
                checkResult.Status = "OK";
                checkResult.Message = "No episodes found to check.";
                return checkResult;
            }

            var valueGroups = valuesByEpisode
                .GroupBy(x => string.Join(",", x.Values))
                .OrderByDescending(g => g.Count())
                .ToList();

            if (valueGroups.Count <= 1)
            {
                checkResult.Status = "OK";
                checkResult.DominantValues = valueGroups.FirstOrDefault()?.Key.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string>();
                checkResult.Message = $"All {episodes.Count} episodes are consistent.";
                return checkResult;
            }

            var dominantGroup = valueGroups.First();
            checkResult.DominantValues = dominantGroup.Key.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

            var mismatchedEpisodes = valuesByEpisode.Except(dominantGroup).ToList();

            checkResult.Status = "Mismatch";
            checkResult.Message = $"{dominantGroup.Count()} of {episodes.Count} episodes have the dominant value(s); {mismatchedEpisodes.Count} are different.";
            checkResult.MismatchedEpisodes = mismatchedEpisodes.Select(me =>
            {
                var episodeItem = me.Episode as Episode;
                return new MismatchedEpisodeInfo
                {
                    EpisodeId = me.Episode.Id.ToString(),
                    EpisodeName = episodeItem != null ? GetEpisodeName(episodeItem) : "Unknown Episode",
                    Actual = me.Values
                };
            }).ToList();

            return checkResult;
        }

        private string GetEpisodeName(Episode ep)
        {
            var season = ep.Parent as Season;
            string name = "";

            if (ep.IndexNumber.HasValue && season?.IndexNumber.HasValue == true)
            {
                name = $"S{season.IndexNumber:D2}E{ep.IndexNumber:D2}";
            }
            else if (ep.IndexNumber.HasValue)
            {
                name = $"Episode {ep.IndexNumber}";
            }

            if (!string.IsNullOrEmpty(ep.Name) && ep.Name != name)
            {
                name = name != "" ? $"{name} - {ep.Name}" : ep.Name;
            }

            return string.IsNullOrEmpty(name) ? $"Item ID: {ep.Id}" : name;
        }
    }
}