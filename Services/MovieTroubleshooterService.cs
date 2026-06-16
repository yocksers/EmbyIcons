using EmbyIcons.Api;
using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Services
{
    [Authenticated]
    [Route(ApiRoutes.MovieTroubleshooter, "GET", Summary = "Reports missing icons for movies in the library")]
    public class GetMovieTroubleshooterReport : IReturn<MovieTroubleshooterResponse>
    {
        [ApiMember(Name = "MovieId", Description = "The ID of a specific movie to check. If omitted, all movies will be checked.", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? MovieId { get; set; }

        [ApiMember(Name = "ChecksToRun", Description = "A comma-separated list of checks to perform (e.g., AudioLanguage,Resolution). If omitted, all checks are run.", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? ChecksToRun { get; set; }
    }

    #region Report Models
    public class MovieTroubleshooterResponse
    {
        public bool IsSingleMovieScan { get; set; }
        public int TotalMoviesScanned { get; set; }
        public string MovieName { get; set; } = string.Empty;
        public string MovieId { get; set; } = string.Empty;
        public List<SingleMovieCheck> Checks { get; set; } = new List<SingleMovieCheck>();
        public List<LibraryCheckGroup> LibraryGroups { get; set; } = new List<LibraryCheckGroup>();
    }

    public class SingleMovieCheck
    {
        public string CheckName { get; set; } = string.Empty;
        public string? Value { get; set; }
        public bool HasIcon { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class LibraryCheckGroup
    {
        public string CheckName { get; set; } = string.Empty;
        public List<MissingIconEntry> Missing { get; set; } = new List<MissingIconEntry>();
        public List<string> Covered { get; set; } = new List<string>();
    }

    public class MissingIconEntry
    {
        public string Value { get; set; } = string.Empty;
        public int MovieCount { get; set; }
        public List<AffectedMovieInfo> SampleMovies { get; set; } = new List<AffectedMovieInfo>();
    }

    public class AffectedMovieInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
    }
    #endregion

    public class MovieTroubleshooterService : IService
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
            public const string FrameRate = "FrameRate";
        }

        private static readonly List<string> AllCheckNames = new List<string>
        {
            CheckNames.AudioLanguage, CheckNames.Subtitles, CheckNames.AudioCodec,
            CheckNames.VideoCodec, CheckNames.AudioChannels, CheckNames.Resolution,
            CheckNames.AspectRatio, CheckNames.VideoFormat, CheckNames.FrameRate
        };

        private static readonly Dictionary<string, (string DisplayName, string IconTypeKey)> CheckMeta =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                [CheckNames.AudioLanguage]  = ("Audio Language",       "Language"),
                [CheckNames.Subtitles]      = ("Subtitles",            "Subtitle"),
                [CheckNames.AudioCodec]     = ("Audio Codec",          "AudioCodec"),
                [CheckNames.VideoCodec]     = ("Video Codec",          "VideoCodec"),
                [CheckNames.AudioChannels]  = ("Audio Channels",       "Channel"),
                [CheckNames.Resolution]     = ("Resolution",           "Resolution"),
                [CheckNames.AspectRatio]    = ("Aspect Ratio",         "AspectRatio"),
                [CheckNames.VideoFormat]    = ("Video Format (HDR)",   "VideoFormat"),
                [CheckNames.FrameRate]      = ("Frame Rate (FPS)",     "FrameRate"),
            };

        public MovieTroubleshooterService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
            _iconCacheManager = Plugin.Instance?.Enhancer._iconCacheManager ?? throw new InvalidOperationException("IconCacheManager not available");
        }

        public object Get(GetMovieTroubleshooterReport request)
        {
            var requestedChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(request.ChecksToRun))
                requestedChecks.UnionWith(request.ChecksToRun.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            bool runAllChecks = !requestedChecks.Any();

            var config = Plugin.Instance?.Configuration ?? new PluginOptions();
            var availableIcons = BuildAvailableIconSet(config);
            var knownResolutions = availableIcons.TryGetValue("Resolution", out var resSet)
                ? (IList<string>)resSet.ToList()
                : new List<string>();

            if (!string.IsNullOrEmpty(request.MovieId)
                && _libraryManager.GetItemById(request.MovieId) is Movie movieItem)
            {
                return GenerateSingleMovieReport(movieItem, requestedChecks, runAllChecks, availableIcons, knownResolutions);
            }

            return GenerateLibraryReport(requestedChecks, runAllChecks, availableIcons, knownResolutions);
        }

        private Dictionary<string, HashSet<string>> BuildAvailableIconSet(PluginOptions config)
        {
            var customIcons   = _iconCacheManager.GetAllAvailableIconKeys(config.IconsFolder);
            var embeddedIcons = _iconCacheManager.GetAllAvailableEmbeddedIconKeys();

            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (IconCacheManager.IconType iconType in Enum.GetValues(typeof(IconCacheManager.IconType)))
            {
                var customKeys   = customIcons.GetValueOrDefault(iconType, new List<string>());
                var embeddedKeys = embeddedIcons.GetValueOrDefault(iconType, new List<string>());

                var combined = config.IconLoadingMode switch
                {
                    IconLoadingMode.CustomOnly  => customKeys,
                    IconLoadingMode.BuiltInOnly => embeddedKeys,
                    _                           => customKeys.Union(embeddedKeys, StringComparer.OrdinalIgnoreCase).ToList()
                };

                result[iconType.ToString()] = new HashSet<string>(combined, StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private MovieTroubleshooterResponse GenerateSingleMovieReport(
            BaseItem movie,
            HashSet<string> requestedChecks,
            bool runAllChecks,
            Dictionary<string, HashSet<string>> availableIcons,
            IList<string> knownResolutions)
        {
            var response = new MovieTroubleshooterResponse
            {
                IsSingleMovieScan = true,
                TotalMoviesScanned = 1,
                MovieName = movie.Name,
                MovieId = movie.Id.ToString()
            };

            var streams      = movie.GetMediaStreams() ?? new List<MediaStream>();
            var videoStream  = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio)
                                      .OrderByDescending(s => s.Channels)
                                      .FirstOrDefault();

            var activeChecks = runAllChecks ? AllCheckNames : AllCheckNames.Where(c => requestedChecks.Contains(c)).ToList();

            foreach (var checkKey in activeChecks)
            {
                if (!CheckMeta.TryGetValue(checkKey, out var meta)) continue;
                var (displayName, iconTypeKey) = meta;
                var iconSet = availableIcons.TryGetValue(iconTypeKey, out var s) ? s : new HashSet<string>();
                var values  = GetMovieValues(checkKey, movie, streams, videoStream, primaryAudio, knownResolutions);

                if (!values.Any())
                {
                    response.Checks.Add(new SingleMovieCheck
                    {
                        CheckName = displayName,
                        Value     = null,
                        HasIcon   = false,
                        Status    = "NoValue"
                    });
                    continue;
                }

                foreach (var value in values)
                {
                    var hasIcon = iconSet.Contains(value);
                    response.Checks.Add(new SingleMovieCheck
                    {
                        CheckName = displayName,
                        Value     = value,
                        HasIcon   = hasIcon,
                        Status    = hasIcon ? "OK" : "MissingIcon"
                    });
                }
            }

            return response;
        }

        private MovieTroubleshooterResponse GenerateLibraryReport(
            HashSet<string> requestedChecks,
            bool runAllChecks,
            Dictionary<string, HashSet<string>> availableIcons,
            IList<string> knownResolutions)
        {
            const int MaxMovies       = 25000;
            const int MaxSampleMovies = 10;

            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                IsVirtualItem    = false,
                Recursive        = true,
                Limit            = MaxMovies
            }).ToList();

            var response = new MovieTroubleshooterResponse
            {
                IsSingleMovieScan   = false,
                TotalMoviesScanned  = movies.Count
            };

            var activeChecks = runAllChecks ? AllCheckNames : AllCheckNames.Where(c => requestedChecks.Contains(c)).ToList();

            var valueMap = activeChecks.ToDictionary(
                c => c,
                _ => new Dictionary<string, List<(string Name, string Id)>>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            foreach (var movie in movies)
            {
                var streams      = movie.GetMediaStreams() ?? new List<MediaStream>();
                var videoStream  = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio)
                                          .OrderByDescending(s => s.Channels)
                                          .FirstOrDefault();
                var movieInfo = (movie.Name, movie.Id.ToString());

                foreach (var checkKey in activeChecks)
                {
                    var values = GetMovieValues(checkKey, movie, streams, videoStream, primaryAudio, knownResolutions);
                    foreach (var value in values)
                    {
                        if (!valueMap[checkKey].TryGetValue(value, out var list))
                        {
                            list = new List<(string, string)>();
                            valueMap[checkKey][value] = list;
                        }
                        list.Add(movieInfo);
                    }
                }
            }

            foreach (var checkKey in activeChecks)
            {
                if (!CheckMeta.TryGetValue(checkKey, out var meta)) continue;
                var (displayName, iconTypeKey) = meta;
                var iconSet = availableIcons.TryGetValue(iconTypeKey, out var s) ? s : new HashSet<string>();
                var perValueMap = valueMap[checkKey];

                if (!perValueMap.Any()) continue;

                var group = new LibraryCheckGroup { CheckName = displayName };

                foreach (var kvp in perValueMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (iconSet.Contains(kvp.Key))
                    {
                        group.Covered.Add(kvp.Key);
                    }
                    else
                    {
                        group.Missing.Add(new MissingIconEntry
                        {
                            Value       = kvp.Key,
                            MovieCount  = kvp.Value.Count,
                            SampleMovies = kvp.Value
                                .Take(MaxSampleMovies)
                                .Select(m => new AffectedMovieInfo { Name = m.Name, Id = m.Id })
                                .ToList()
                        });
                    }
                }

                if (group.Missing.Any() || group.Covered.Any())
                    response.LibraryGroups.Add(group);
            }

            return response;
        }

        private List<string> GetMovieValues(
            string checkKey,
            BaseItem movie,
            IReadOnlyList<MediaStream> streams,
            MediaStream? videoStream,
            MediaStream? primaryAudio,
            IList<string> knownResolutions)
        {
            switch (checkKey)
            {
                case CheckNames.AudioLanguage:
                    return streams
                        .Where(s => s.Type == MediaStreamType.Audio
                                    && (!string.IsNullOrEmpty(s.DisplayLanguage) || !string.IsNullOrEmpty(s.Language)))
                        .Select(s => LanguageHelper.NormalizeLangCode(
                            !string.IsNullOrEmpty(s.DisplayLanguage) ? s.DisplayLanguage : s.Language))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                case CheckNames.Subtitles:
                    return streams
                        .Where(s => s.Type == MediaStreamType.Subtitle
                                    && (!string.IsNullOrEmpty(s.DisplayLanguage) || !string.IsNullOrEmpty(s.Language)))
                        .Select(s => LanguageHelper.NormalizeLangCode(
                            !string.IsNullOrEmpty(s.DisplayLanguage) ? s.DisplayLanguage : s.Language))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                case CheckNames.AudioCodec:
                    return streams
                        .Where(s => s.Type == MediaStreamType.Audio)
                        .Select(MediaStreamHelper.GetAudioCodecIconName)
                        .Where(c => c != null)
                        .Select(c => c!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                case CheckNames.VideoCodec:
                    return streams
                        .Where(s => s.Type == MediaStreamType.Video)
                        .Select(MediaStreamHelper.GetVideoCodecIconName)
                        .Where(c => c != null)
                        .Select(c => c!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                case CheckNames.AudioChannels:
                {
                    var ch = primaryAudio != null ? MediaStreamHelper.GetChannelIconName(primaryAudio) : null;
                    return ch != null ? new List<string> { ch } : new List<string>();
                }

                case CheckNames.Resolution:
                {
                    var res = videoStream != null
                        ? MediaStreamHelper.GetResolutionIconNameFromStream(videoStream, knownResolutions, movie)
                        : null;
                    return res != null ? new List<string> { res } : new List<string>();
                }

                case CheckNames.AspectRatio:
                {
                    var ar = MediaStreamHelper.GetAspectRatioIconName(videoStream, true);
                    return ar != null ? new List<string> { ar } : new List<string>();
                }

                case CheckNames.VideoFormat:
                {
                    var fmt = MediaStreamHelper.GetVideoFormatIconName(movie, streams);
                    return fmt != null ? new List<string> { fmt } : new List<string>();
                }

                case CheckNames.FrameRate:
                {
                    var fps = MediaStreamHelper.GetFrameRateIconName(videoStream);
                    return fps != null ? new List<string> { fps } : new List<string>();
                }

                default:
                    return new List<string>();
            }
        }
    }
}
