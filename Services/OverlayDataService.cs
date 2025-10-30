using EmbyIcons.Helpers;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;

namespace EmbyIcons.Services
{
    internal class OverlayDataService : IDisposable
    {
        private readonly EmbyIconsEnhancer _enhancer;
        private readonly ILibraryManager _libraryManager;
        private readonly MemoryCache _providerPathCache = new(new MemoryCacheOptions { SizeLimit = 5000 });
        private Timer? _cacheMaintenanceTimer;

        public OverlayDataService(EmbyIconsEnhancer enhancer, ILibraryManager libraryManager)
        {
            _enhancer = enhancer;
            _libraryManager = libraryManager;
            _cacheMaintenanceTimer = new Timer(_ => CompactProviderCache(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        private void CompactProviderCache()
        {
            try
            {
                _providerPathCache?.Compact(0.1);
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _enhancer.Logger.Debug("[EmbyIcons] Performed cache compaction for provider path cache.");
            }
            catch (Exception ex)
            {
                _enhancer.Logger.ErrorException("[EmbyIcons] Error during provider cache compaction.", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                _providerPathCache?.Dispose();
            }
            catch { }
            try { _cacheMaintenanceTimer?.Dispose(); } catch { }
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

            var sb = new StringBuilder(tag.Length);
            bool lastWasWhitespace = false;

            foreach (char c in tag.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasWhitespace)
                    {
                        sb.Append('-');
                        lastWasWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasWhitespace = false;
                }
            }

            return sb.ToString();
        }

        private static float? ExtractRottenTomatoesFromItem(BaseItem it)
        {
            if (it == null) return null;

            try
            {
                var providerIdsProp = it.GetType().GetProperty("ProviderIds");
                var providerIds = providerIdsProp?.GetValue(it) as System.Collections.IDictionary;
                if (providerIds != null)
                {
                    foreach (System.Collections.DictionaryEntry de in providerIds)
                    {
                        var key = de.Key?.ToString() ?? string.Empty;
                        var val = de.Value?.ToString() ?? string.Empty;
                        if (key.IndexOf("rotten", StringComparison.OrdinalIgnoreCase) >= 0 || key.Equals("rt", StringComparison.OrdinalIgnoreCase))
                        {
                            var parsed = TryParsePercent(val);
                            if (parsed.HasValue) return parsed.Value;
                        }
                    }
                }
            }
            catch { }

            try
            {
                var props = it.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                foreach (var p in props)
                {
                    if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string)) continue;
                    if (p.Name.IndexOf("Rating", StringComparison.OrdinalIgnoreCase) < 0 && p.Name.IndexOf("Ratings", StringComparison.OrdinalIgnoreCase) < 0 && p.Name.IndexOf("External", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    try
                    {
                        var col = p.GetValue(it) as System.Collections.IEnumerable;
                        if (col == null) continue;
                        foreach (var entry in col)
                        {
                            if (entry == null) continue;
                            var entryType = entry.GetType();
                            var sourceProp = entryType.GetProperty("Source") ?? entryType.GetProperty("Name") ?? entryType.GetProperty("Key");
                            var valueProp = entryType.GetProperty("Value") ?? entryType.GetProperty("Rating") ?? entryType.GetProperty("Score");
                            var source = sourceProp?.GetValue(entry)?.ToString() ?? string.Empty;
                            var value = valueProp?.GetValue(entry)?.ToString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(source) && source.IndexOf("rotten", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var parsed = TryParsePercent(value);
                                if (parsed.HasValue) return parsed.Value;
                            }
                            var parsed2 = TryParsePercent(source);
                            if (parsed2.HasValue) return parsed2.Value;
                            parsed2 = TryParsePercent(value);
                            if (parsed2.HasValue) return parsed2.Value;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;

            static float? TryParsePercent(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                s = s.Trim();
                try
                {
                    var idx = s.IndexOf('%');
                    if (idx >= 0)
                    {
                        var num = s.Substring(0, idx).Trim();
                        if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) return Math.Clamp(f, 0f, 100f);
                    }

                    if (s.Contains('/'))
                    {
                        var parts = s.Split('/');
                        if (parts.Length == 2 && float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) && float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) && b != 0)
                        {
                            var percent = (a / b) * 100f;
                            return Math.Clamp(percent, 0f, 100f);
                        }
                    }

                    if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        if (v <= 1f) return Math.Clamp(v * 100f, 0f, 100f);
                        if (v <= 10f) return Math.Clamp(v * 10f, 0f, 100f);
                        return Math.Clamp(v, 0f, 100f);
                    }
                }
                catch { }
                return null;
            }
        }

        private OverlayData CreateOverlayDataFromAggregate(EmbyIconsEnhancer.AggregatedSeriesResult aggResult, BaseItem item)
        {
            var tags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            if (item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    var nt = NormalizeTag(tag);
                    if (!string.IsNullOrEmpty(nt)) tags.Add(nt);
                }
            }

            return new OverlayData
            {
                AudioLanguages = aggResult.AudioLangs,
                SubtitleLanguages = aggResult.SubtitleLangs,
                AudioCodecs = aggResult.AudioCodecs,
                VideoCodecs = aggResult.VideoCodecs,
                ChannelIconName = aggResult.ChannelTypes.FirstOrDefault(),
                VideoFormatIconName = aggResult.VideoFormats.FirstOrDefault(),
                ResolutionIconName = aggResult.Resolutions.FirstOrDefault(),
                CommunityRating = item.CommunityRating,
                RottenTomatoesRating = item.CriticRating,
                Tags = tags,
                AspectRatioIconName = aggResult.AspectRatios.FirstOrDefault(),
                ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating)
            };
        }

        public OverlayData GetOverlayData(BaseItem item, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            if (item is Series seriesItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(seriesItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, seriesItem);
            }

            if (item is Season seasonItem)
            {
                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(seasonItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, seasonItem);
            }

            if (item is BoxSet collectionItem)
            {
                if (!profileOptions.UseCollectionLiteMode && !profileOptions.ShowCollectionIconsIfAllChildrenHaveLanguage)
                {
                    if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    {
                        Plugin.Instance.Logger.Debug($"[EmbyIcons] Overlays for collections are disabled in the current profile (Full Mode). Skipping '{item.Name}'.");
                    }
                    return new OverlayData();
                }

                var aggResult = _enhancer.GetOrBuildAggregatedDataForParent(collectionItem, profileOptions, globalOptions);
                return CreateOverlayDataFromAggregate(aggResult, collectionItem);
            }

            if (EmbyIconsEnhancer._episodeIconCache?.TryGetValue(item.Id, out EmbyIconsEnhancer.EpisodeIconInfo? cachedInfo) == true && cachedInfo != null && cachedInfo.DateModifiedTicks == item.DateModified.Ticks)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) Plugin.Instance?.Logger.Debug($"[EmbyIcons] Using cached icon info for '{item.Name}'.");
                return new OverlayData
                {
                    AudioLanguages = cachedInfo.AudioLangs,
                    SubtitleLanguages = cachedInfo.SubtitleLangs,
                    AudioCodecs = cachedInfo.AudioCodecs,
                    VideoCodecs = cachedInfo.VideoCodecs,
                    Tags = cachedInfo.Tags,
                    SourceIcons = cachedInfo.SourceIcons,
                    ChannelIconName = cachedInfo.ChannelIconName,
                    VideoFormatIconName = cachedInfo.VideoFormatIconName,
                    ResolutionIconName = cachedInfo.ResolutionIconName,
                    CommunityRating = item.CommunityRating,
                    RottenTomatoesRating = cachedInfo.RottenTomatoesRating,
                    AspectRatioIconName = cachedInfo.AspectRatioIconName,
                    ParentalRatingIconName = cachedInfo.ParentalRatingIconName
                };
            }

            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) Plugin.Instance?.Logger.Debug($"[EmbyIcons] No valid cache. Processing streams for '{item.Name}'.");
            var overlayData = ProcessMediaStreams(item, profileOptions, globalOptions);

            var newInfo = new EmbyIconsEnhancer.EpisodeIconInfo
            {
                AudioLangs = overlayData.AudioLanguages,
                SubtitleLangs = overlayData.SubtitleLanguages,
                AudioCodecs = overlayData.AudioCodecs,
                VideoCodecs = overlayData.VideoCodecs,
                Tags = overlayData.Tags,
                SourceIcons = overlayData.SourceIcons,
                ChannelIconName = overlayData.ChannelIconName,
                VideoFormatIconName = overlayData.VideoFormatIconName,
                ResolutionIconName = overlayData.ResolutionIconName,
                RottenTomatoesRating = overlayData.RottenTomatoesRating,
                DateModifiedTicks = item.DateModified.Ticks,
                AspectRatioIconName = overlayData.AspectRatioIconName,
                ParentalRatingIconName = overlayData.ParentalRatingIconName
            };

            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetSlidingExpiration(TimeSpan.FromHours(6));

            EmbyIconsEnhancer._episodeIconCache?.Set(item.Id, newInfo, cacheEntryOptions);

            return overlayData;
        }

        private OverlayData ProcessMediaStreams(BaseItem item, ProfileSettings profileOptions, PluginOptions options)
        {
            var data = new OverlayData();

            if (profileOptions.CommunityScoreIconAlignment != IconAlignment.Disabled)
            {
                data.CommunityRating = item.CommunityRating;
            }

            if (item.CriticRating.HasValue)
            {
                data.RottenTomatoesRating = item.CriticRating.Value;
            }
            else
            {
                try
                {
                    var rt = ExtractRottenTomatoesFromItem(item);
                    if (rt.HasValue) data.RottenTomatoesRating = rt.Value;
                }
                catch { }
            }

            if (profileOptions.ParentalRatingIconAlignment != IconAlignment.Disabled)
            {
                data.ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating);
            }

            if (profileOptions.SourceIconAlignment != IconAlignment.Disabled && item is Movie movieItem && profileOptions.FilenameBasedIcons.Any())
            {
                IReadOnlyCollection<string> allPaths = Array.Empty<string>();
                var providerIdKey = movieItem.ProviderIds.Keys.FirstOrDefault(k => k.Equals("Imdb", StringComparison.OrdinalIgnoreCase) || k.Equals("Tmdb", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(providerIdKey) && movieItem.ProviderIds.TryGetValue(providerIdKey, out var providerIdValue) && !string.IsNullOrEmpty(providerIdValue))
                {
                    var cacheKey = $"{providerIdKey}:{providerIdValue}";
                    if (!_providerPathCache.TryGetValue(cacheKey, out string[]? cachedPaths) || cachedPaths == null)
                    {
                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Movie" },
                            Recursive = true,
                            AnyProviderIdEquals = new[] { new KeyValuePair<string, string>(providerIdKey, providerIdValue) }
                        };
                        try
                        {
                            cachedPaths = _libraryManager.GetItemList(query)
                                .OfType<Movie>()
                                .Where(v => !string.IsNullOrEmpty(v.Path))
                                .Select(v => v.Path!.ToLowerInvariant())
                                .Distinct()
                                .ToArray();
                        }
                        catch (Exception ex)
                        {
                            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _enhancer.Logger.Debug($"[EmbyIcons] Failed to query movie versions for provider id {cacheKey}: {ex.Message}");
                            cachedPaths = Array.Empty<string>();
                        }

                        var cacheEntryOptions = new MemoryCacheEntryOptions()
                            .SetSize(1)
                            .SetSlidingExpiration(TimeSpan.FromHours(6))
                            .RegisterPostEvictionCallback((key, value, reason, state) =>
                            {
                                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                                {
                                    _enhancer.Logger.Debug($"[EmbyIcons] Provider path cache entry evicted: {key} Reason: {reason}");
                                }
                            });

                        _providerPathCache.Set(cacheKey, cachedPaths ?? Array.Empty<string>(), cacheEntryOptions);
                    }

                    allPaths = cachedPaths ?? Array.Empty<string>();
                }
                else
                {
                    allPaths = string.IsNullOrEmpty(movieItem.Path) ? Array.Empty<string>() : new[] { movieItem.Path!.ToLowerInvariant() };
                }

                foreach (var path in allPaths)
                {
                    foreach (var mapping in profileOptions.FilenameBasedIcons)
                    {
                        if (!string.IsNullOrWhiteSpace(mapping.Keyword) &&
                            !string.IsNullOrWhiteSpace(mapping.IconName) &&
                            path.Contains(mapping.Keyword.ToLowerInvariant()))
                        {
                            data.SourceIcons.Add(mapping.IconName.ToLowerInvariant());
                        }
                    }
                }
            }

            if (profileOptions.TagIconAlignment != IconAlignment.Disabled && item.Tags != null && item.Tags.Length > 0)
            {
                foreach (var tag in item.Tags)
                {
                    var nt = NormalizeTag(tag);
                    if (!string.IsNullOrEmpty(nt)) data.Tags.Add(nt);
                }
            }

            var mainItemStreams = item.GetMediaStreams() ?? new List<MediaStream>();

            if (!mainItemStreams.Any())
            {
                return data;
            }

            MediaStream? primaryVideoStream = mainItemStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            MediaStream? primaryAudioStream = mainItemStreams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();

            if (profileOptions.AudioIconAlignment != IconAlignment.Disabled || profileOptions.SubtitleIconAlignment != IconAlignment.Disabled ||
                profileOptions.AudioCodecIconAlignment != IconAlignment.Disabled || profileOptions.VideoCodecIconAlignment != IconAlignment.Disabled)
            {
                foreach (var stream in mainItemStreams)
                {
                    if (stream.Type == MediaStreamType.Audio)
                    {
                        if (profileOptions.AudioIconAlignment != IconAlignment.Disabled && !string.IsNullOrEmpty(stream.DisplayLanguage)) data.AudioLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                        if (profileOptions.AudioCodecIconAlignment != IconAlignment.Disabled)
                        {
                            var codecIcon = MediaStreamHelper.GetAudioCodecIconName(stream);
                            if (codecIcon != null) data.AudioCodecs.Add(codecIcon);
                        }
                    }
                    else if (stream.Type == MediaStreamType.Subtitle && profileOptions.SubtitleIconAlignment != IconAlignment.Disabled && !string.IsNullOrEmpty(stream.DisplayLanguage))
                    {
                        data.SubtitleLanguages.Add(LanguageHelper.NormalizeLangCode(stream.DisplayLanguage));
                    }
                    else if (stream.Type == MediaStreamType.Video && profileOptions.VideoCodecIconAlignment != IconAlignment.Disabled)
                    {
                        var codecIcon = MediaStreamHelper.GetVideoCodecIconName(stream);
                        if (codecIcon != null) data.VideoCodecs.Add(codecIcon);
                    }
                }
            }

            if (profileOptions.ChannelIconAlignment != IconAlignment.Disabled && primaryAudioStream != null)
            {
                data.ChannelIconName = MediaStreamHelper.GetChannelIconName(primaryAudioStream);
            }

            if (profileOptions.VideoFormatIconAlignment != IconAlignment.Disabled)
            {
                data.VideoFormatIconName = MediaStreamHelper.GetVideoFormatIconName(item, mainItemStreams);
            }

            if (profileOptions.ResolutionIconAlignment != IconAlignment.Disabled && primaryVideoStream != null)
            {
                _enhancer._iconCacheManager.GetAllAvailableIconKeys(options.IconsFolder)
                    .TryGetValue(IconCacheManager.IconType.Resolution, out var knownResolutionKeys);
                data.ResolutionIconName = MediaStreamHelper.GetResolutionIconNameFromStream(primaryVideoStream, knownResolutionKeys ?? new List<string>());
            }

            if (profileOptions.AspectRatioIconAlignment != IconAlignment.Disabled)
            {
                data.AspectRatioIconName = MediaStreamHelper.GetAspectRatioIconName(primaryVideoStream, profileOptions.SnapAspectRatioToCommon);
            }

            return data;
        }
    }

    class BaseItemComparer : IEqualityComparer<BaseItem>
    {
        public bool Equals(BaseItem? x, BaseItem? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.Id == y.Id;
        }

        public int GetHashCode([DisallowNull] BaseItem obj)
        {
            return obj.Id.GetHashCode();
        }
    }
}