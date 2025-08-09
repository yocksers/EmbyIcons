using EmbyIcons.Api;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
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
        public DateTime ReportDate { get; set; }
    }

    public class IconGroupReport
    {
        public List<string> FoundInLibrary { get; set; } = new List<string>();
        public List<string> FoundInFolder { get; set; } = new List<string>();
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

            var report = GenerateReport();
            lock (_cacheLock)
            {
                _cachedReport = report;
            }

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

            var allItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", Constants.Episode, "Series" },
                IsVirtualItem = false,
                Recursive = true
            });

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
                    if (format != null) localReport.VideoFormats.Add(format);

                    var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
                    if (primaryAudio != null)
                    {
                        var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                        if (ch != null) localReport.Channels.Add(ch);
                    }

                    foreach (var stream in streams)
                    {
                        switch (stream.Type)
                        {
                            case MediaStreamType.Audio:
                                if (!string.IsNullOrEmpty(stream.DisplayLanguage)) localReport.Languages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                                var audioCodec = MediaStreamHelper.GetAudioCodecIconName(stream);
                                if (audioCodec != null) localReport.AudioCodecs.Add(audioCodec);
                                break;
                            case MediaStreamType.Subtitle:
                                if (!string.IsNullOrEmpty(stream.DisplayLanguage)) localReport.Subtitles.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                                break;
                            case MediaStreamType.Video:
                                var videoCodec = MediaStreamHelper.GetVideoCodecIconName(stream);
                                if (videoCodec != null) localReport.VideoCodecs.Add(videoCodec);
                                var res = MediaStreamHelper.GetResolutionIconNameFromStream(stream, knownResolutions);
                                if (res != null) localReport.Resolutions.Add(res);
                                var ar = MediaStreamHelper.GetAspectRatioIconName(stream);
                                if (ar != null) localReport.AspectRatios.Add(ar);
                                break;
                        }
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
                        return threadReport;
                    },
                    // 3. Combine Accumulators: Merges the results from two parallel threads.
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

            pluginInstance.Logger.Info("[EmbyIcons] Icon Manager report generation complete.");
            return report;
        }
    }
}