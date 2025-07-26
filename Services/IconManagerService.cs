using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
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
    [Route("/EmbyIcons/IconManagerReport", "GET", Summary = "Generates a report of used, missing, and unused icons")]
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
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsVirtualItem = false,
                Recursive = true
            });

            var libraryProperties = new Dictionary<IconCacheManager.IconType, HashSet<string>>
            {
                { IconCacheManager.IconType.Language, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.Subtitle, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.Channel, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.AudioCodec, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.VideoCodec, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.VideoFormat, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.Resolution, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.AspectRatio, new HashSet<string>(StringComparer.OrdinalIgnoreCase) },
                { IconCacheManager.IconType.Tag, new HashSet<string>(StringComparer.OrdinalIgnoreCase) }
            };

            var customIcons = _iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder);
            customIcons.TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutions);
            knownResolutions ??= new List<string>();

            foreach (var item in allItems)
            {
                var streams = item.GetMediaStreams() ?? new List<MediaStream>();
                if (!streams.Any()) continue;

                foreach (var stream in streams)
                {
                    if (stream.Type == MediaStreamType.Audio)
                    {
                        if (!string.IsNullOrEmpty(stream.DisplayLanguage)) libraryProperties[IconCacheManager.IconType.Language].Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                        var codec = MediaStreamHelper.GetAudioCodecIconName(stream);
                        if (codec != null) libraryProperties[IconCacheManager.IconType.AudioCodec].Add(codec);
                    }
                    else if (stream.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(stream.DisplayLanguage))
                    {
                        libraryProperties[IconCacheManager.IconType.Subtitle].Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                    }
                    else if (stream.Type == MediaStreamType.Video)
                    {
                        var codec = MediaStreamHelper.GetVideoCodecIconName(stream);
                        if (codec != null) libraryProperties[IconCacheManager.IconType.VideoCodec].Add(codec);

                        var res = MediaStreamHelper.GetResolutionIconNameFromStream(stream, knownResolutions);
                        if (res != null) libraryProperties[IconCacheManager.IconType.Resolution].Add(res);

                        var ar = MediaStreamHelper.GetAspectRatioIconName(stream);
                        if (ar != null) libraryProperties[IconCacheManager.IconType.AspectRatio].Add(ar);
                    }
                }

                var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
                if (primaryAudio != null)
                {
                    var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                    if (ch != null) libraryProperties[IconCacheManager.IconType.Channel].Add(ch);
                }

                var format = MediaStreamHelper.GetVideoFormatIconName(item, streams);
                if (format != null) libraryProperties[IconCacheManager.IconType.VideoFormat].Add(format);

                if (item.Tags != null)
                {
                    foreach (var tag in item.Tags) libraryProperties[IconCacheManager.IconType.Tag].Add(tag);
                }
            }

            var finalReport = new IconManagerReport { ReportDate = DateTime.UtcNow };
            foreach (var type in libraryProperties.Keys)
            {
                finalReport.Groups[type.ToString()] = new IconGroupReport
                {
                    FoundInLibrary = libraryProperties[type].OrderBy(p => p).ToList(),
                    FoundInFolder = customIcons.TryGetValue(type, out var folderIcons) ? folderIcons.OrderBy(p => p).ToList() : new List<string>()
                };
            }
            pluginInstance.Logger.Info("[EmbyIcons] Icon Manager report generation complete.");
            return finalReport;
        }
    }
}