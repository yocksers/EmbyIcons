using EmbyIcons.Api;
using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
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
    [Route(ApiRoutes.MusicTroubleshooter, "GET", Summary = "Reports missing icons for music tracks in the library")]
    public class GetMusicTroubleshooterReport : IReturn<MusicTroubleshooterResponse>
    {
        [ApiMember(Name = "AlbumId", Description = "The ID of a specific album to check. If omitted, all music tracks will be checked.", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? AlbumId { get; set; }

        [ApiMember(Name = "ChecksToRun", Description = "A comma-separated list of checks to perform (e.g., AudioLanguage,AudioCodec,SampleRate). If omitted, all checks are run.", IsRequired = false, DataType = "string", ParameterType = "query")]
        public string? ChecksToRun { get; set; }
    }

    #region Report Models
    public class MusicTroubleshooterResponse
    {
        public bool IsSingleAlbumScan { get; set; }
        public int TotalTracksScanned { get; set; }
        public string AlbumName { get; set; } = string.Empty;
        public string AlbumId { get; set; } = string.Empty;
        public List<SingleTrackCheck> Checks { get; set; } = new List<SingleTrackCheck>();
        public List<MusicLibraryCheckGroup> LibraryGroups { get; set; } = new List<MusicLibraryCheckGroup>();
    }

    public class SingleTrackCheck
    {
        public string CheckName { get; set; } = string.Empty;
        public string? Value { get; set; }
        public bool HasIcon { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public class MusicLibraryCheckGroup
    {
        public string CheckName { get; set; } = string.Empty;
        public List<MissingMusicIconEntry> Missing { get; set; } = new List<MissingMusicIconEntry>();
        public List<string> Covered { get; set; } = new List<string>();
    }

    public class MissingMusicIconEntry
    {
        public string Value { get; set; } = string.Empty;
        public int TrackCount { get; set; }
        public List<AffectedTrackInfo> SampleTracks { get; set; } = new List<AffectedTrackInfo>();
    }

    public class AffectedTrackInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? AlbumName { get; set; }
    }
    #endregion

    public class MusicTroubleshooterService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IconCacheManager _iconCacheManager;

        private static class CheckNames
        {
            public const string AudioLanguage = "AudioLanguage";
            public const string AudioCodec = "AudioCodec";
            public const string AudioChannels = "AudioChannels";
            public const string SampleRate = "SampleRate";
            public const string AudioBitRate = "AudioBitRate";
            public const string BitDepth = "BitDepth";
        }

        private static readonly List<string> AllCheckNames = new List<string>
        {
            CheckNames.AudioLanguage, CheckNames.AudioCodec, CheckNames.AudioChannels,
            CheckNames.SampleRate, CheckNames.AudioBitRate, CheckNames.BitDepth
        };

        private static readonly Dictionary<string, (string DisplayName, string IconTypeKey)> CheckMeta =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                [CheckNames.AudioLanguage]  = ("Audio Language",  "Language"),
                [CheckNames.AudioCodec]     = ("Audio Codec",     "AudioCodec"),
                [CheckNames.AudioChannels]  = ("Audio Channels",  "Channel"),
                [CheckNames.SampleRate]     = ("Sample Rate",     "SampleRate"),
                [CheckNames.AudioBitRate]   = ("Audio Bitrate",   "AudioBitRate"),
                [CheckNames.BitDepth]       = ("Bit Depth",       "BitDepth"),
            };

        public MusicTroubleshooterService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
            _iconCacheManager = Plugin.Instance?.Enhancer._iconCacheManager ?? throw new InvalidOperationException("IconCacheManager not available");
        }

        public object Get(GetMusicTroubleshooterReport request)
        {
            var requestedChecks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(request.ChecksToRun))
                requestedChecks.UnionWith(request.ChecksToRun.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            bool runAllChecks = !requestedChecks.Any();

            var config = Plugin.Instance?.Configuration ?? new PluginOptions();
            var availableIcons = BuildAvailableIconSet(config);

            if (!string.IsNullOrEmpty(request.AlbumId)
                && _libraryManager.GetItemById(request.AlbumId) is MusicAlbum albumItem)
            {
                return GenerateSingleAlbumReport(albumItem, requestedChecks, runAllChecks, availableIcons);
            }

            return GenerateLibraryReport(requestedChecks, runAllChecks, availableIcons);
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

        private MusicTroubleshooterResponse GenerateSingleAlbumReport(
            MusicAlbum album,
            HashSet<string> requestedChecks,
            bool runAllChecks,
            Dictionary<string, HashSet<string>> availableIcons)
        {
            var tracks = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Parent = album,
                IncludeItemTypes = new[] { "Audio" },
                Recursive = true
            }).OfType<Audio>().ToList();

            var response = new MusicTroubleshooterResponse
            {
                IsSingleAlbumScan = true,
                TotalTracksScanned = tracks.Count,
                AlbumName = album.Name,
                AlbumId = album.Id.ToString()
            };

            if (!tracks.Any()) return response;

            var activeChecks = runAllChecks ? AllCheckNames : AllCheckNames.Where(c => requestedChecks.Contains(c)).ToList();

            foreach (var checkKey in activeChecks)
            {
                if (!CheckMeta.TryGetValue(checkKey, out var meta)) continue;
                var (displayName, iconTypeKey) = meta;
                var iconSet = availableIcons.TryGetValue(iconTypeKey, out var s) ? s : new HashSet<string>();

                var allValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var track in tracks)
                {
                    var trackValues = GetTrackValues(checkKey, track);
                    allValues.UnionWith(trackValues);
                }

                foreach (var value in allValues.OrderBy(v => v))
                {
                    response.Checks.Add(new SingleTrackCheck
                    {
                        CheckName = displayName,
                        Value     = value,
                        HasIcon   = iconSet.Contains(value),
                        Status    = iconSet.Contains(value) ? "OK" : "MissingIcon"
                    });
                }

                if (!allValues.Any())
                {
                    response.Checks.Add(new SingleTrackCheck
                    {
                        CheckName = displayName,
                        Value     = null,
                        HasIcon   = false,
                        Status    = "NoValue"
                    });
                }
            }

            return response;
        }

        private MusicTroubleshooterResponse GenerateLibraryReport(
            HashSet<string> requestedChecks,
            bool runAllChecks,
            Dictionary<string, HashSet<string>> availableIcons)
        {
            var allTracks = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Audio" },
                IsVirtualItem = false,
                Recursive = true,
                Limit = 50000
            }).OfType<Audio>().ToList();

            var response = new MusicTroubleshooterResponse
            {
                IsSingleAlbumScan = false,
                TotalTracksScanned = allTracks.Count
            };

            var activeChecks = runAllChecks ? AllCheckNames : AllCheckNames.Where(c => requestedChecks.Contains(c)).ToList();

            foreach (var checkKey in activeChecks)
            {
                if (!CheckMeta.TryGetValue(checkKey, out var meta)) continue;
                var (displayName, iconTypeKey) = meta;
                var iconSet = availableIcons.TryGetValue(iconTypeKey, out var s) ? s : new HashSet<string>();

                var valueCounts = new Dictionary<string, (int Count, List<AffectedTrackInfo> Samples)>(StringComparer.OrdinalIgnoreCase);

                foreach (var track in allTracks)
                {
                    var values = GetTrackValues(checkKey, track);
                    foreach (var value in values)
                    {
                        if (iconSet.Contains(value)) continue;

                        if (!valueCounts.TryGetValue(value, out var entry))
                        {
                            entry = (0, new List<AffectedTrackInfo>());
                            valueCounts[value] = entry;
                        }

                        var newCount = entry.Count + 1;
                        var samples = entry.Samples;
                        if (samples.Count < 3)
                        {
                            samples.Add(new AffectedTrackInfo
                            {
                                Name      = track.Name,
                                Id        = track.Id.ToString(),
                                AlbumName = track.Album
                            });
                        }
                        valueCounts[value] = (newCount, samples);
                    }
                }

                var covered = allTracks
                    .SelectMany(t => GetTrackValues(checkKey, t))
                    .Where(v => iconSet.Contains(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v)
                    .ToList();

                if (valueCounts.Any() || covered.Any())
                {
                    response.LibraryGroups.Add(new MusicLibraryCheckGroup
                    {
                        CheckName = displayName,
                        Missing = valueCounts
                            .OrderByDescending(kvp => kvp.Value.Count)
                            .Select(kvp => new MissingMusicIconEntry
                            {
                                Value       = kvp.Key,
                                TrackCount  = kvp.Value.Count,
                                SampleTracks = kvp.Value.Samples
                            }).ToList(),
                        Covered = covered
                    });
                }
            }

            return response;
        }

        private static List<string> GetTrackValues(string checkKey, Audio track)
        {
            var streams = track.GetMediaStreams() ?? new List<MediaStream>();
            var primaryAudio = streams.Where(s => s.Type == MediaStreamType.Audio)
                                      .OrderByDescending(s => s.Channels)
                                      .FirstOrDefault();

            switch (checkKey)
            {
                case CheckNames.AudioLanguage:
                    return streams
                        .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage))
                        .Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage))
                        .Where(l => !string.IsNullOrEmpty(l))
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

                case CheckNames.AudioChannels:
                    if (primaryAudio == null) return new List<string>();
                    var ch = MediaStreamHelper.GetChannelIconName(primaryAudio);
                    return ch != null ? new List<string> { ch } : new List<string>();

                case CheckNames.SampleRate:
                    if (primaryAudio == null) return new List<string>();
                    var sr = MediaStreamHelper.GetSampleRateIconName(primaryAudio);
                    return sr != null ? new List<string> { sr } : new List<string>();

                case CheckNames.AudioBitRate:
                    if (primaryAudio == null) return new List<string>();
                    var br = MediaStreamHelper.GetAudioBitRateIconName(primaryAudio);
                    return br != null ? new List<string> { br } : new List<string>();

                case CheckNames.BitDepth:
                    if (primaryAudio == null) return new List<string>();
                    var bd = MediaStreamHelper.GetBitDepthIconName(primaryAudio);
                    return bd != null ? new List<string> { bd } : new List<string>();

                default:
                    return new List<string>();
            }
        }
    }
}
