using EmbyIcons.Api;
using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.IconManagerReport, "GET", Summary = "Generates a report of used, missing, and unused icons")]
    public class GetIconManagerReport : IReturn<IconManagerReport> { }

    public class IconManagerReport
    {
        public Dictionary<string, IconGroupReport> Groups { get; set; } = new Dictionary<string, IconGroupReport>();
        public LibraryStatistics Statistics { get; set; } = new LibraryStatistics();
        public DateTime ReportDate { get; set; }
    }

    public class IconGroupReport
    {
        public List<string> FoundInLibrary { get; set; } = new List<string>();
        public List<string> FoundInFolder { get; set; } = new List<string>();
    }

    public class LibraryStatistics
    {
        public int TotalItems { get; set; }
        public Dictionary<string, int> ResolutionCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> AudioLanguageCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> SubtitleLanguageCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> AudioCodecCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> VideoCodecCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> VideoFormatCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> AspectRatioCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> ChannelCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> FrameRateCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> OriginalLanguageCounts { get; set; } = new Dictionary<string, int>();
    }

    public class IconManagerService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IconCacheManager _iconCacheManager;

        private static IconManagerReport? _cachedReport;
        private static readonly object _cacheLock = new object();

        public IconManagerService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
            _iconCacheManager = Plugin.Instance?.Enhancer._iconCacheManager ?? throw new InvalidOperationException("IconCacheManager not available");
        }

        public object Get(GetIconManagerReport request)
        {
            lock (_cacheLock)
            {
                if (_cachedReport != null)
                {
                    return _cachedReport;
                }
            }

            ScanProgressService.ClearProgress("IconManager");
            var report = GenerateReport();

            lock (_cacheLock)
            {
                _cachedReport = report;
            }

            ScanProgressService.ClearProgress("IconManager");
            return report;
        }

        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedReport = null;
                Plugin.Instance?.Logger.Info("[EmbyIcons] Icon Manager report cache invalidated.");
            }
        }

        private class LocalItemReport
        {
            public HashSet<string> Languages { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Subtitles { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AudioCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VideoFormats { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Resolutions { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> AspectRatios { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ParentalRatings { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> FrameRates { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OriginalLanguages { get; } = new(StringComparer.OrdinalIgnoreCase);
            
            public string? Resolution { get; set; }
            public string? PrimaryLanguage { get; set; }
            public string? PrimarySubtitle { get; set; }
            public string? AudioCodec { get; set; }
            public string? VideoCodec { get; set; }
            public string? VideoFormat { get; set; }
            public string? AspectRatio { get; set; }
            public string? Channel { get; set; }
            public string? FrameRate { get; set; }
            public string? OriginalLanguage { get; set; }
        }

        private IconManagerReport GenerateReport()
        {
            var pluginInstance = Plugin.Instance;
            if (pluginInstance == null)
            {
                return new IconManagerReport { ReportDate = DateTime.UtcNow };
            }

            pluginInstance.Logger.Info("[EmbyIcons] Generating new Icon Manager report...");
            var options = pluginInstance.GetConfiguredOptions();

            const int MAX_ITEMS_TO_ANALYZE = 50000;
            
            var allItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", Constants.Episode, "Series" },
                IsVirtualItem = false,
                Recursive = true,
                Limit = MAX_ITEMS_TO_ANALYZE
            });

            int totalItems = allItems.Count();
            int processedCount = 0;

            var customIcons = _iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder);
            customIcons.TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutions);
            knownResolutions ??= new List<string>();

            var finalReportData = allItems
                .AsParallel()
                .WithDegreeOfParallelism(Environment.ProcessorCount)
                .Select(item =>
                {
                    var localReport = new LocalItemReport();
                    var streams = item.GetMediaStreams() ?? new List<MediaStream>();

                    var rating = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating);
                    if (rating != null) localReport.ParentalRatings.Add(rating);

                    if (item.Tags != null)
                    {
                        foreach (var tag in item.Tags) localReport.Tags.Add(tag);
                    }

                    if (!streams.Any()) return localReport;

                    var format = MediaStreamHelper.GetVideoFormatIconName(item, streams);
                    if (format != null)
                    {
                        localReport.VideoFormats.Add(format);
                        localReport.VideoFormat = format;
                    }

                    var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
                    if (primaryAudio != null)
                    {
                        var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                        if (ch != null)
                        {
                            localReport.Channels.Add(ch);
                            localReport.Channel = ch;
                        }
                    }

                    foreach (var stream in streams)
                    {
                        switch (stream.Type)
                        {
                            case MediaStreamType.Audio:
                                if (!string.IsNullOrEmpty(stream.DisplayLanguage))
                                {
                                    var lang = LanguageHelper.NormalizeLangCode(stream.DisplayLanguage);
                                    localReport.Languages.Add(lang);
                                    if (localReport.PrimaryLanguage == null) localReport.PrimaryLanguage = lang;
                                }
                                var audioCodec = MediaStreamHelper.GetAudioCodecIconName(stream);
                                if (audioCodec != null)
                                {
                                    localReport.AudioCodecs.Add(audioCodec);
                                    if (localReport.AudioCodec == null) localReport.AudioCodec = audioCodec;
                                }
                                break;
                            case MediaStreamType.Subtitle:
                                if (!string.IsNullOrEmpty(stream.DisplayLanguage))
                                {
                                    var subLang = LanguageHelper.NormalizeLangCode(stream.DisplayLanguage);
                                    localReport.Subtitles.Add(subLang);
                                    if (localReport.PrimarySubtitle == null) localReport.PrimarySubtitle = subLang;
                                }
                                break;
                            case MediaStreamType.Video:
                                var videoCodec = MediaStreamHelper.GetVideoCodecIconName(stream);
                                if (videoCodec != null)
                                {
                                    localReport.VideoCodecs.Add(videoCodec);
                                    localReport.VideoCodec = videoCodec;
                                }
                                var res = MediaStreamHelper.GetResolutionIconNameFromStream(stream, knownResolutions);
                                if (res != null)
                                {
                                    localReport.Resolutions.Add(res);
                                    localReport.Resolution = res;
                                }
                                var ar = MediaStreamHelper.GetAspectRatioIconName(stream, true);
                                if (ar != null)
                                {
                                    localReport.AspectRatios.Add(ar);
                                    localReport.AspectRatio = ar;
                                }
                                var fps = MediaStreamHelper.GetFrameRateIconName(stream);
                                if (fps != null)
                                {
                                    localReport.FrameRates.Add(fps);
                                    localReport.FrameRate = fps;
                                }
                                break;
                        }
                    }

                    var originalLang = GetOriginalLanguageFromItem(item);
                    if (!string.IsNullOrEmpty(originalLang))
                    {
                        var normalizedLang = LanguageHelper.NormalizeLangCode(originalLang);
                        localReport.OriginalLanguages.Add(normalizedLang);
                        localReport.OriginalLanguage = normalizedLang;
                    }

                    var newCount = System.Threading.Interlocked.Increment(ref processedCount);
                    if (newCount % 200 == 0)
                    {
                        ScanProgressService.UpdateProgress("IconManager", newCount, totalItems, $"Scanning item {newCount} of {totalItems}...");
                    }

                    return localReport;
                })
                .Aggregate(
                    seedFactory: () => new LocalItemReport(),
                    updateAccumulatorFunc: (threadReport, itemReport) =>
                    {
                        threadReport.Languages.UnionWith(itemReport.Languages);
                        threadReport.Subtitles.UnionWith(itemReport.Subtitles);
                        threadReport.Channels.UnionWith(itemReport.Channels);
                        threadReport.AudioCodecs.UnionWith(itemReport.AudioCodecs);
                        threadReport.VideoCodecs.UnionWith(itemReport.VideoCodecs);
                        threadReport.VideoFormats.UnionWith(itemReport.VideoFormats);
                        threadReport.Resolutions.UnionWith(itemReport.Resolutions);
                        threadReport.AspectRatios.UnionWith(itemReport.AspectRatios);
                        threadReport.Tags.UnionWith(itemReport.Tags);
                        threadReport.ParentalRatings.UnionWith(itemReport.ParentalRatings);
                        threadReport.FrameRates.UnionWith(itemReport.FrameRates);
                        threadReport.OriginalLanguages.UnionWith(itemReport.OriginalLanguages);
                        return threadReport;
                    },
                    combineAccumulatorsFunc: (mainReport, threadReport) =>
                    {
                        mainReport.Languages.UnionWith(threadReport.Languages);
                        mainReport.Subtitles.UnionWith(threadReport.Subtitles);
                        mainReport.Channels.UnionWith(threadReport.Channels);
                        mainReport.AudioCodecs.UnionWith(threadReport.AudioCodecs);
                        mainReport.VideoCodecs.UnionWith(threadReport.VideoCodecs);
                        mainReport.VideoFormats.UnionWith(threadReport.VideoFormats);
                        mainReport.Resolutions.UnionWith(threadReport.Resolutions);
                        mainReport.AspectRatios.UnionWith(threadReport.AspectRatios);
                        mainReport.Tags.UnionWith(threadReport.Tags);
                        mainReport.ParentalRatings.UnionWith(threadReport.ParentalRatings);
                        mainReport.FrameRates.UnionWith(threadReport.FrameRates);
                        mainReport.OriginalLanguages.UnionWith(threadReport.OriginalLanguages);
                        return mainReport;
                    },
                    resultSelector: finalReport => finalReport);

            var report = new IconManagerReport { ReportDate = DateTime.UtcNow };

            report.Groups[IconCacheManager.IconType.Language.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.Languages.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.Language, new List<string>()) };
            report.Groups[IconCacheManager.IconType.Subtitle.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.Subtitles.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.Subtitle, new List<string>()) };
            report.Groups[IconCacheManager.IconType.Channel.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.Channels.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.Channel, new List<string>()) };
            report.Groups[IconCacheManager.IconType.AudioCodec.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.AudioCodecs.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.AudioCodec, new List<string>()) };
            report.Groups[IconCacheManager.IconType.VideoCodec.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.VideoCodecs.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.VideoCodec, new List<string>()) };
            report.Groups[IconCacheManager.IconType.VideoFormat.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.VideoFormats.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.VideoFormat, new List<string>()) };
            report.Groups[IconCacheManager.IconType.Resolution.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.Resolutions.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>()) };
            report.Groups[IconCacheManager.IconType.AspectRatio.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.AspectRatios.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.AspectRatio, new List<string>()) };
            report.Groups[IconCacheManager.IconType.Tag.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.Tags.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.Tag, new List<string>()) };
            report.Groups[IconCacheManager.IconType.ParentalRating.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.ParentalRatings.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.ParentalRating, new List<string>()) };
            report.Groups[IconCacheManager.IconType.FrameRate.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.FrameRates.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.FrameRate, new List<string>()) };
            report.Groups[IconCacheManager.IconType.OriginalLanguage.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.OriginalLanguages.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.OriginalLanguage, new List<string>()) };

            report.Statistics = CalculateStatistics(allItems, knownResolutions);

            pluginInstance.Logger.Info("[EmbyIcons] Icon Manager report generation complete.");
            return report;
        }

        private LibraryStatistics CalculateStatistics(IEnumerable<BaseItem> items, List<string> knownResolutions)
        {
            var stats = new LibraryStatistics();
            var itemsList = items.Where(i => i is not Series).ToList();
            stats.TotalItems = itemsList.Count;

            var resolutionCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var languageCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var subtitleCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var audioCodecCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var videoCodecCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var videoFormatCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var aspectRatioCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var channelCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var frameRateCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var originalLanguageCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            System.Threading.Tasks.Parallel.ForEach(itemsList, item =>
            {
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                if (!streams.Any()) return;

                var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                if (videoStream != null)
                {
                    var res = MediaStreamHelper.GetResolutionIconNameFromStream(videoStream, knownResolutions);
                    if (res != null) resolutionCounts.AddOrUpdate(res, 1, (k, v) => v + 1);

                    var ar = MediaStreamHelper.GetAspectRatioIconName(videoStream, true);
                    if (ar != null) aspectRatioCounts.AddOrUpdate(ar, 1, (k, v) => v + 1);

                    var fps = MediaStreamHelper.GetFrameRateIconName(videoStream);
                    if (fps != null) frameRateCounts.AddOrUpdate(fps, 1, (k, v) => v + 1);

                    var vc = MediaStreamHelper.GetVideoCodecIconName(videoStream);
                    if (vc != null) videoCodecCounts.AddOrUpdate(vc, 1, (k, v) => v + 1);
                }

                var format = MediaStreamHelper.GetVideoFormatIconName(item, streams);
                if (format != null) videoFormatCounts.AddOrUpdate(format, 1, (k, v) => v + 1);

                var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
                if (primaryAudio != null)
                {
                    if (!string.IsNullOrEmpty(primaryAudio.DisplayLanguage))
                    {
                        var lang = LanguageHelper.NormalizeLangCode(primaryAudio.DisplayLanguage);
                        languageCounts.AddOrUpdate(lang, 1, (k, v) => v + 1);
                    }

                    var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                    if (ch != null) channelCounts.AddOrUpdate(ch, 1, (k, v) => v + 1);

                    var ac = MediaStreamHelper.GetAudioCodecIconName(primaryAudio);
                    if (ac != null) audioCodecCounts.AddOrUpdate(ac, 1, (k, v) => v + 1);
                }

                var firstSubtitle = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage));
                if (firstSubtitle != null)
                {
                    var subLang = LanguageHelper.NormalizeLangCode(firstSubtitle.DisplayLanguage);
                    subtitleCounts.AddOrUpdate(subLang, 1, (k, v) => v + 1);
                }

                var originalLang = GetOriginalLanguageFromItem(item);
                if (!string.IsNullOrEmpty(originalLang))
                {
                    var normalizedOriginalLang = LanguageHelper.NormalizeLangCode(originalLang);
                    originalLanguageCounts.AddOrUpdate(normalizedOriginalLang, 1, (k, v) => v + 1);
                }
            });

            stats.ResolutionCounts = resolutionCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.AudioLanguageCounts = languageCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.SubtitleLanguageCounts = subtitleCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.AudioCodecCounts = audioCodecCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.VideoCodecCounts = videoCodecCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.VideoFormatCounts = videoFormatCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.AspectRatioCounts = aspectRatioCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.ChannelCounts = channelCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.FrameRateCounts = frameRateCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            stats.OriginalLanguageCounts = originalLanguageCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

            return stats;
        }

        private static string? GetOriginalLanguageFromItem(BaseItem item)
        {
            if (item == null) return null;

            try
            {
                var itemType = item.GetType();
                var originalLangProp = itemType.GetProperty("OriginalLanguage");
                if (originalLangProp != null)
                {
                    var value = originalLangProp.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                var productionLocationsProp = itemType.GetProperty("ProductionLocations");
                if (productionLocationsProp != null)
                {
                    var locations = productionLocationsProp.GetValue(item) as string[];
                    if (locations != null && locations.Length > 0 && !string.IsNullOrWhiteSpace(locations[0]))
                    {
                        return locations[0];
                    }
                }
            }
            catch { }

            return null;
        }
    }
}