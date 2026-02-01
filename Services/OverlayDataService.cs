using EmbyIcons.Caching;
using EmbyIcons.Configuration;
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
        private readonly MemoryCache _providerPathCache = new(new MemoryCacheOptions { SizeLimit = Constants.DefaultProviderPathCacheSize });
        private Timer? _cacheMaintenanceTimer;
        private readonly object _timerInitLock = new object();
        private static readonly System.Reflection.PropertyInfo? _providerIdsProperty = typeof(BaseItem).GetProperty("ProviderIds");

        public OverlayDataService(EmbyIconsEnhancer enhancer, ILibraryManager libraryManager)
        {
            _enhancer = enhancer;
            _libraryManager = libraryManager;
            EnsureMaintenanceTimerInitialized();
        }

        private void EnsureMaintenanceTimerInitialized()
        {
            if (_cacheMaintenanceTimer == null)
            {
                lock (_timerInitLock)
                {
                    if (_cacheMaintenanceTimer == null)
                    {
                        var maintenanceInterval = TimeSpan.FromHours(Math.Max(Constants.MinMaintenanceIntervalHours, Plugin.Instance?.Configuration.CacheMaintenanceIntervalHours ?? 1));
                        _cacheMaintenanceTimer = new Timer(_ => CompactProviderCache(), null, maintenanceInterval, maintenanceInterval);
                    }
                }
            }
        }

        private void CompactProviderCache()
        {
            try
            {
                _providerPathCache?.Compact(Constants.CacheCompactionPercentage);
                if (Helpers.PluginHelper.IsDebugLoggingEnabled) 
                    _enhancer.Logger.Debug("[EmbyIcons] Performed cache compaction for provider path cache.");
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
            catch (Exception ex)
            {
                _enhancer.Logger.Debug($"[EmbyIcons] Error disposing provider path cache: {ex.Message}");
            }
            
            try { _cacheMaintenanceTimer?.Dispose(); } catch (Exception ex) { _enhancer.Logger.Debug($"[EmbyIcons] Error disposing cache maintenance timer: {ex.Message}"); }
        }

        private static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

            tag = tag.Trim();
            var sb = new StringBuilder(tag.Length);
            bool lastWasWhitespace = false;

            for (int i = 0; i < tag.Length; i++)
            {
                char c = tag[i];
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

            var result = TryExtractFromProviderIds(it);
            if (result.HasValue) return result;

            return TryExtractFromRatingProperties(it);
        }

        private static float? TryExtractFromProviderIds(BaseItem item)
        {
            try
            {
                var providerIds = _providerIdsProperty?.GetValue(item) as System.Collections.IDictionary;
                if (providerIds == null) return null;

                foreach (System.Collections.DictionaryEntry de in providerIds)
                {
                    var key = de.Key?.ToString() ?? string.Empty;
                    var val = de.Value?.ToString() ?? string.Empty;
                    
                    if (key.IndexOf(StringConstants.RottenTomatoesProvider, StringComparison.OrdinalIgnoreCase) >= 0 || 
                        key.Equals(StringConstants.RTShort, StringComparison.OrdinalIgnoreCase))
                    {
                        var parsed = TryParsePercent(val);
                        if (parsed.HasValue) return parsed.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Debug($"[EmbyIcons] Error extracting rating from provider IDs: {ex.Message}");
                }
            }
            return null;
        }

        private static float? TryExtractFromRatingProperties(BaseItem item)
        {
            try
            {
                var props = item.GetType().GetProperties(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                
                foreach (var p in props)
                {
                    if (!IsRatingProperty(p)) continue;

                    var result = TryExtractFromProperty(p, item);
                    if (result.HasValue) return result;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Debug($"[EmbyIcons] Error extracting rating from properties: {ex.Message}");
                }
            }
            return null;
        }

        private static bool IsRatingProperty(System.Reflection.PropertyInfo prop)
        {
            if (!typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType) || 
                prop.PropertyType == typeof(string))
            {
                return false;
            }

            var name = prop.Name;
            return name.IndexOf(StringConstants.RatingPropertyName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf(StringConstants.RatingsPropertyName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf(StringConstants.ExternalPropertyName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static float? TryExtractFromProperty(System.Reflection.PropertyInfo prop, BaseItem item)
        {
            try
            {
                var col = prop.GetValue(item) as System.Collections.IEnumerable;
                if (col == null) return null;

                foreach (var entry in col)
                {
                    if (entry == null) continue;
                    
                    var result = TryExtractFromRatingEntry(entry);
                    if (result.HasValue) return result;
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Debug($"[EmbyIcons] Error extracting from property '{prop.Name}': {ex.Message}");
                }
            }
            return null;
        }

        private static float? TryExtractFromRatingEntry(object entry)
        {
            var entryType = entry.GetType();
            var sourceProp = entryType.GetProperty(StringConstants.SourcePropertyName) ?? 
                           entryType.GetProperty(StringConstants.NamePropertyName) ?? 
                           entryType.GetProperty(StringConstants.KeyPropertyName);
            var valueProp = entryType.GetProperty(StringConstants.ValuePropertyName) ?? 
                          entryType.GetProperty(StringConstants.RatingPropertyName) ?? 
                          entryType.GetProperty(StringConstants.ScorePropertyName);
            
            var source = sourceProp?.GetValue(entry)?.ToString() ?? string.Empty;
            var value = valueProp?.GetValue(entry)?.ToString() ?? string.Empty;
            
            if (!string.IsNullOrEmpty(source) && 
                source.IndexOf(StringConstants.RottenTomatoesProvider, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var parsed = TryParsePercent(value);
                if (parsed.HasValue) return parsed.Value;
            }
            
            var parsed2 = TryParsePercent(source) ?? TryParsePercent(value);
            return parsed2;
        }

        private static float? TryParsePercent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            
            try
            {
                var idx = s.IndexOf('%');
                if (idx >= 0)
                {
                    var num = s.Substring(0, idx).Trim();
                    if (float.TryParse(num, System.Globalization.NumberStyles.Float, 
                        System.Globalization.CultureInfo.InvariantCulture, out var f))
                    {
                        return Math.Clamp(f, 0f, 100f);
                    }
                }

                if (s.Contains('/'))
                {
                    var parts = s.Split('/');
                    if (parts.Length == 2 && 
                        float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, 
                            System.Globalization.CultureInfo.InvariantCulture, out var a) && 
                        float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, 
                            System.Globalization.CultureInfo.InvariantCulture, out var b) && 
                        b != 0)
                    {
                        return Math.Clamp((a / b) * 100f, 0f, 100f);
                    }
                }

                if (float.TryParse(s, System.Globalization.NumberStyles.Float, 
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    if (v <= 1f) return Math.Clamp(v * 100f, 0f, 100f);
                    if (v <= 10f) return Math.Clamp(v * 10f, 0f, 100f);
                    return Math.Clamp(v, 0f, 100f);
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Debug($"[EmbyIcons] Error parsing percentage from '{s}': {ex.Message}");
                }
            }
            
            return null;
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

            var data = new OverlayData
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

            var profileOptions = Plugin.Instance?.GetProfileForItem(item)?.Settings;
            if (profileOptions?.FavoriteCountIconAlignment != IconAlignment.Disabled)
            {
                data.FavoriteCount = _enhancer.GetFavoriteCount(item);
            }

            return data;
        }

        public OverlayData GetOverlayData(BaseItem item, ProfileSettings profileOptions, PluginOptions globalOptions)
        {
            EnsureMaintenanceTimerInitialized();

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

            EmbyIconsEnhancer.EnsureEpisodeCacheInitialized();
            if (EmbyIconsEnhancer._episodeIconCache?.TryGetValue(item.Id, out EmbyIconsEnhancer.EpisodeIconInfo? cachedInfo) == true && cachedInfo != null && cachedInfo.DateModifiedTicks == item.DateModified.Ticks)
            {
                if (Helpers.PluginHelper.IsDebugLoggingEnabled) 
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Using cached icon info for '{item.Name}'.");
                
                float? currentRottenTomatoesRating = null;
                if (item.CriticRating.HasValue)
                {
                    currentRottenTomatoesRating = item.CriticRating.Value;
                }
                else
                {
                    try
                    {
                        var rt = ExtractRottenTomatoesFromItem(item);
                        if (rt.HasValue) currentRottenTomatoesRating = rt.Value;
                    }
                    catch (Exception ex)
                    {
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _enhancer.Logger.Debug($"[EmbyIcons] Error extracting Rotten Tomatoes rating from cached item: {ex.Message}");
                    }
                }
                
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
                    RottenTomatoesRating = currentRottenTomatoesRating,
                    AspectRatioIconName = cachedInfo.AspectRatioIconName,
                    ParentalRatingIconName = cachedInfo.ParentalRatingIconName,
                    FrameRateIconName = cachedInfo.FrameRateIconName,
                    OriginalLanguageIconName = cachedInfo.OriginalLanguageIconName
                };
            }

            if (Helpers.PluginHelper.IsDebugLoggingEnabled) 
                Plugin.Instance?.Logger.Debug($"[EmbyIcons] No valid cache. Processing streams for '{item.Name}'.");
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
                ParentalRatingIconName = overlayData.ParentalRatingIconName,
                FrameRateIconName = overlayData.FrameRateIconName,
                OriginalLanguageIconName = overlayData.OriginalLanguageIconName
            };

            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetSize(1)
                .SetSlidingExpiration(TimeSpan.FromHours(EmbyIconsEnhancer.EpisodeCacheSlidingExpirationHours));

            EmbyIconsEnhancer.EnsureEpisodeCacheInitialized();
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
                catch (Exception ex)
                {
                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                        _enhancer.Logger.Debug($"[EmbyIcons] Error extracting Rotten Tomatoes rating: {ex.Message}");
                }
            }

            if (profileOptions.FavoriteCountIconAlignment != IconAlignment.Disabled)
            {
                data.FavoriteCount = _enhancer.GetFavoriteCount(item);
            }

            if (profileOptions.ParentalRatingIconAlignment != IconAlignment.Disabled)
            {
                data.ParentalRatingIconName = MediaStreamHelper.GetParentalRatingIconName(item.OfficialRating);
            }

            if (profileOptions.SourceIconAlignment != IconAlignment.Disabled && item is Movie movieItem && profileOptions.FilenameBasedIcons.Any())
            {
                IReadOnlyCollection<string> allPaths = Array.Empty<string>();
                string? providerIdKey = null;
                foreach (var key in movieItem.ProviderIds.Keys)
                {
                    if (key.Equals(StringConstants.ImdbProvider, StringComparison.OrdinalIgnoreCase) || 
                        key.Equals(StringConstants.TmdbProvider, StringComparison.OrdinalIgnoreCase))
                    {
                        providerIdKey = key;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(providerIdKey) && movieItem.ProviderIds.TryGetValue(providerIdKey, out var providerIdValue) && !string.IsNullOrEmpty(providerIdValue))
                {
                    var cacheKey = $"{providerIdKey}:{providerIdValue}";
                    if (!_providerPathCache.TryGetValue(cacheKey, out string[]? cachedPaths) || cachedPaths == null)
                    {
                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Movie" },
                            Recursive = true,
                            AnyProviderIdEquals = new[] { new KeyValuePair<string, string>(providerIdKey, providerIdValue) },
                            Limit = 50
                        };
                        try
                        {
                            cachedPaths = _libraryManager.GetItemList(query)
                                .OfType<Movie>()
                                .Where(v => !string.IsNullOrEmpty(v.Path))
                                .Select(v => v.Path!.ToLowerInvariant())
                                .Distinct()
                                .Take(50)
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
                                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
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

            var audioStreams = mainItemStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
            var subtitleStreams = mainItemStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

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

            if (profileOptions.FrameRateIconAlignment != IconAlignment.Disabled && primaryVideoStream != null)
            {
                data.FrameRateIconName = MediaStreamHelper.GetFrameRateIconName(primaryVideoStream, profileOptions.SnapFrameRateToCommon);
            }

            if (profileOptions.OriginalLanguageIconAlignment != IconAlignment.Disabled)
            {
                var originalLang = GetOriginalLanguage(item);
                if (!string.IsNullOrEmpty(originalLang))
                {
                    data.OriginalLanguageIconName = LanguageHelper.NormalizeLangCode(originalLang);
                }
            }

            return data;
        }

        private static string? GetOriginalLanguage(BaseItem item)
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
            catch (Exception ex)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                {
                    Plugin.Instance.Logger.Debug($"[EmbyIcons] Error extracting original language: {ex.Message}");
                }
            }

            return null;
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