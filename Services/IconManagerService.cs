using EmbyIcons.Api;
using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace EmbyIcons.Services
{
    [Authenticated]
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
            bool hadReport;
            lock (_cacheLock)
            {
                hadReport = _cachedReport != null;
                _cachedReport = null;
            }

            if (hadReport)
                Plugin.Instance?.Logger.Info("[EmbyIcons] Icon Manager report cache invalidated.");
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
            public HashSet<string> SeriesStatuses { get; } = new(StringComparer.OrdinalIgnoreCase);

            public void MergeFrom(LocalItemReport other)
            {
                Languages.UnionWith(other.Languages);
                Subtitles.UnionWith(other.Subtitles);
                Channels.UnionWith(other.Channels);
                AudioCodecs.UnionWith(other.AudioCodecs);
                VideoCodecs.UnionWith(other.VideoCodecs);
                VideoFormats.UnionWith(other.VideoFormats);
                Resolutions.UnionWith(other.Resolutions);
                AspectRatios.UnionWith(other.AspectRatios);
                Tags.UnionWith(other.Tags);
                ParentalRatings.UnionWith(other.ParentalRatings);
                FrameRates.UnionWith(other.FrameRates);
                OriginalLanguages.UnionWith(other.OriginalLanguages);
                SeriesStatuses.UnionWith(other.SeriesStatuses);
            }
        }

        private sealed class StatisticsAccumulator
        {
            public int TotalItems;
            public Dictionary<string, int> Resolutions { get; } = NewCounter();
            public Dictionary<string, int> AudioLanguages { get; } = NewCounter();
            public Dictionary<string, int> SubtitleLanguages { get; } = NewCounter();
            public Dictionary<string, int> AudioCodecs { get; } = NewCounter();
            public Dictionary<string, int> VideoCodecs { get; } = NewCounter();
            public Dictionary<string, int> VideoFormats { get; } = NewCounter();
            public Dictionary<string, int> AspectRatios { get; } = NewCounter();
            public Dictionary<string, int> Channels { get; } = NewCounter();
            public Dictionary<string, int> FrameRates { get; } = NewCounter();
            public Dictionary<string, int> OriginalLanguages { get; } = NewCounter();

            private static Dictionary<string, int> NewCounter() => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public static void Increment(Dictionary<string, int> counter, string? key)
            {
                if (key == null) return;
                counter.TryGetValue(key, out var current);
                counter[key] = current + 1;
            }

            public void MergeFrom(StatisticsAccumulator other)
            {
                TotalItems += other.TotalItems;
                Merge(Resolutions, other.Resolutions);
                Merge(AudioLanguages, other.AudioLanguages);
                Merge(SubtitleLanguages, other.SubtitleLanguages);
                Merge(AudioCodecs, other.AudioCodecs);
                Merge(VideoCodecs, other.VideoCodecs);
                Merge(VideoFormats, other.VideoFormats);
                Merge(AspectRatios, other.AspectRatios);
                Merge(Channels, other.Channels);
                Merge(FrameRates, other.FrameRates);
                Merge(OriginalLanguages, other.OriginalLanguages);
            }

            private static void Merge(Dictionary<string, int> target, Dictionary<string, int> source)
            {
                foreach (var kv in source)
                {
                    target.TryGetValue(kv.Key, out var current);
                    target[kv.Key] = current + kv.Value;
                }
            }

            private static Dictionary<string, int> Sorted(Dictionary<string, int> counter)
                => counter.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

            public LibraryStatistics ToStatistics() => new LibraryStatistics
            {
                TotalItems = TotalItems,
                ResolutionCounts = Sorted(Resolutions),
                AudioLanguageCounts = Sorted(AudioLanguages),
                SubtitleLanguageCounts = Sorted(SubtitleLanguages),
                AudioCodecCounts = Sorted(AudioCodecs),
                VideoCodecCounts = Sorted(VideoCodecs),
                VideoFormatCounts = Sorted(VideoFormats),
                AspectRatioCounts = Sorted(AspectRatios),
                ChannelCounts = Sorted(Channels),
                FrameRateCounts = Sorted(FrameRates),
                OriginalLanguageCounts = Sorted(OriginalLanguages)
            };
        }

        private const int ReportBatchSize = 5000;

        private IconManagerReport GenerateReport()
        {
            var pluginInstance = Plugin.Instance;
            if (pluginInstance == null)
            {
                return new IconManagerReport { ReportDate = DateTime.UtcNow };
            }

            pluginInstance.Logger.Info("[EmbyIcons] Generating new Icon Manager report...");
            var options = pluginInstance.GetConfiguredOptions();

            var customIcons = _iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder);
            var customResolutionKeys = customIcons.GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
            var embeddedResolutionKeys = _iconCacheManager.GetAllAvailableEmbeddedIconKeys().GetValueOrDefault(IconCacheManager.IconType.Resolution, new List<string>());
            var knownResolutions = options.IconLoadingMode switch
            {
                IconLoadingMode.CustomOnly => customResolutionKeys,
                IconLoadingMode.BuiltInOnly => embeddedResolutionKeys,
                _ => customResolutionKeys.Union(embeddedResolutionKeys, StringComparer.OrdinalIgnoreCase).ToList()
            };

            InternalItemsQuery CreateQuery() => new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", Constants.Episode, "Series" },
                IsVirtualItem = false,
                Recursive = true
            };

            var finalReportData = new LocalItemReport();
            var statistics = new StatisticsAccumulator();
            int totalItems = 0;
            int processedCount = 0;
            var batch = new List<BaseItem>(ReportBatchSize);

            foreach (var item in LibraryItemPager.EnumerateAll(_libraryManager, CreateQuery, ReportBatchSize, total => totalItems = total))
            {
                batch.Add(item);
                if (batch.Count >= ReportBatchSize)
                {
                    AnalyzeBatch(batch, knownResolutions, finalReportData, statistics, processedCount, totalItems);
                    processedCount += batch.Count;
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                AnalyzeBatch(batch, knownResolutions, finalReportData, statistics, processedCount, totalItems);
                processedCount += batch.Count;
            }

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
            report.Groups[IconCacheManager.IconType.SeriesStatus.ToString()] = new IconGroupReport { FoundInLibrary = finalReportData.SeriesStatuses.OrderBy(p => p).ToList(), FoundInFolder = customIcons.GetValueOrDefault(IconCacheManager.IconType.SeriesStatus, new List<string>()) };

            report.Statistics = statistics.ToStatistics();

            pluginInstance.Logger.Info($"[EmbyIcons] Icon Manager report generation complete. Analyzed {processedCount} items.");
            return report;
        }

        private static void AnalyzeBatch(List<BaseItem> batch, List<string> knownResolutions, LocalItemReport finalReport, StatisticsAccumulator finalStatistics, int processedBefore, int totalItems)
        {
            var mergeLock = new object();
            int batchProcessed = 0;

            Parallel.ForEach(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
                () => (Report: new LocalItemReport(), Statistics: new StatisticsAccumulator()),
                (item, _, local) =>
                {
                    AnalyzeItem(item, knownResolutions, local.Report, local.Statistics);

                    var done = processedBefore + Interlocked.Increment(ref batchProcessed);
                    if (done % 200 == 0)
                    {
                        ScanProgressService.UpdateProgress("IconManager", done, totalItems, $"Scanning item {done} of {totalItems}...");
                    }

                    return local;
                },
                local =>
                {
                    lock (mergeLock)
                    {
                        finalReport.MergeFrom(local.Report);
                        finalStatistics.MergeFrom(local.Statistics);
                    }
                });
        }

        private static void AnalyzeItem(BaseItem item, List<string> knownResolutions, LocalItemReport report, StatisticsAccumulator statistics)
        {
            bool countInStatistics = item is not Series;
            if (countInStatistics) statistics.TotalItems++;

            var rating = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating);
            if (rating != null) report.ParentalRatings.Add(rating);

            if (item.Tags != null)
            {
                foreach (var tag in item.Tags) report.Tags.Add(tag);
            }

            var originalLang = GetOriginalLanguageFromItem(item);
            var normalizedOriginalLang = !string.IsNullOrEmpty(originalLang) ? LanguageHelper.NormalizeLangCode(originalLang!) : null;
            if (normalizedOriginalLang != null) report.OriginalLanguages.Add(normalizedOriginalLang);

            if (item is Series series)
            {
                var status = MediaStreamHelper.GetSeriesStatusIconName(series);
                if (status != null) report.SeriesStatuses.Add(status);
            }

            var streams = item.GetMediaStreams() ?? new List<MediaStream>();
            if (streams.Count == 0) return;

            var videoStream = MediaStreamHelper.GetPrimaryVideoStream(streams);
            bool isLikelyImage = false;
            if (videoStream != null)
            {
                var fpsValue = videoStream.RealFrameRate ?? videoStream.AverageFrameRate;
                isLikelyImage = fpsValue.HasValue && fpsValue.Value > 1000;
            }

            var format = MediaStreamHelper.GetVideoFormatIconName(item, streams);
            if (format != null) report.VideoFormats.Add(format);

            var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
            if (!isLikelyImage && primaryAudio != null)
            {
                var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                if (ch != null) report.Channels.Add(ch);
            }

            foreach (var stream in streams)
            {
                switch (stream.Type)
                {
                    case MediaStreamType.Audio:
                        if (!isLikelyImage)
                        {
                            var audioLangCode = !string.IsNullOrEmpty(stream.DisplayLanguage) ? stream.DisplayLanguage : stream.Language;
                            if (!string.IsNullOrEmpty(audioLangCode))
                            {
                                report.Languages.Add(LanguageHelper.NormalizeLangCode(audioLangCode));
                            }
                            var audioCodec = MediaStreamHelper.GetAudioCodecIconName(stream);
                            if (audioCodec != null) report.AudioCodecs.Add(audioCodec);
                        }
                        break;
                    case MediaStreamType.Subtitle:
                        if (!isLikelyImage)
                        {
                            var subLangCode = !string.IsNullOrEmpty(stream.DisplayLanguage) ? stream.DisplayLanguage : stream.Language;
                            if (!string.IsNullOrEmpty(subLangCode))
                            {
                                report.Subtitles.Add(LanguageHelper.NormalizeLangCode(subLangCode));
                            }
                        }
                        break;
                    case MediaStreamType.Video:
                        var videoCodec = MediaStreamHelper.GetVideoCodecIconName(stream);
                        if (videoCodec != null) report.VideoCodecs.Add(videoCodec);
                        var res = MediaStreamHelper.GetResolutionIconNameFromStream(stream, knownResolutions);
                        if (res != null) report.Resolutions.Add(res);
                        var ar = MediaStreamHelper.GetAspectRatioIconName(stream, true);
                        if (ar != null) report.AspectRatios.Add(ar);
                        if (!isLikelyImage)
                        {
                            var fps = MediaStreamHelper.GetFrameRateIconName(stream);
                            if (fps != null) report.FrameRates.Add(fps);
                        }
                        break;
                }
            }

            if (!countInStatistics) return;

            if (videoStream != null)
            {
                StatisticsAccumulator.Increment(statistics.Resolutions, MediaStreamHelper.GetResolutionIconNameFromStream(videoStream, knownResolutions));
                StatisticsAccumulator.Increment(statistics.AspectRatios, MediaStreamHelper.GetAspectRatioIconName(videoStream, true));
                if (!isLikelyImage)
                {
                    StatisticsAccumulator.Increment(statistics.FrameRates, MediaStreamHelper.GetFrameRateIconName(videoStream));
                }
                StatisticsAccumulator.Increment(statistics.VideoCodecs, MediaStreamHelper.GetVideoCodecIconName(videoStream));
            }

            StatisticsAccumulator.Increment(statistics.VideoFormats, format);

            if (!isLikelyImage)
            {
                if (primaryAudio != null)
                {
                    var primaryLangCode = !string.IsNullOrEmpty(primaryAudio.DisplayLanguage) ? primaryAudio.DisplayLanguage : primaryAudio.Language;
                    if (!string.IsNullOrEmpty(primaryLangCode))
                    {
                        StatisticsAccumulator.Increment(statistics.AudioLanguages, LanguageHelper.NormalizeLangCode(primaryLangCode));
                    }

                    StatisticsAccumulator.Increment(statistics.Channels, MediaStreamHelper.GetChannelIconName(primaryAudio));
                    StatisticsAccumulator.Increment(statistics.AudioCodecs, MediaStreamHelper.GetAudioCodecIconName(primaryAudio));
                }

                var firstSubtitle = streams.FirstOrDefault(st => st.Type == MediaStreamType.Subtitle && (!string.IsNullOrEmpty(st.DisplayLanguage) || !string.IsNullOrEmpty(st.Language)));
                if (firstSubtitle != null)
                {
                    var firstSubLangCode = !string.IsNullOrEmpty(firstSubtitle.DisplayLanguage) ? firstSubtitle.DisplayLanguage : firstSubtitle.Language;
                    StatisticsAccumulator.Increment(statistics.SubtitleLanguages, LanguageHelper.NormalizeLangCode(firstSubLangCode!));
                }
            }

            StatisticsAccumulator.Increment(statistics.OriginalLanguages, normalizedOriginalLang);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (System.Reflection.PropertyInfo? OriginalLanguage, System.Reflection.PropertyInfo? ProductionLocations)> _originalLangPropCache
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, (System.Reflection.PropertyInfo?, System.Reflection.PropertyInfo?)>();

        private static string? GetOriginalLanguageFromItem(BaseItem item)
        {
            if (item == null) return null;

            try
            {
                var itemType = item.GetType();
                var props = _originalLangPropCache.GetOrAdd(itemType, t => (
                    t.GetProperty("OriginalLanguage"),
                    t.GetProperty("ProductionLocations")
                ));

                if (props.OriginalLanguage != null)
                {
                    var value = props.OriginalLanguage.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }

                if (props.ProductionLocations != null)
                {
                    var locations = props.ProductionLocations.GetValue(item) as string[];
                    if (locations != null && locations.Length > 0 && !string.IsNullOrWhiteSpace(locations[0]))
                        return locations[0];
                }
            }
            catch { }

            return null;
        }
    }
}