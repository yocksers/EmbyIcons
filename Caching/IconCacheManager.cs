using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Configuration;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.Caching
{
    public class IconCacheManager : IDisposable
    {
        private readonly ILogger _logger;
        private MemoryCache _iconImageCache;
        private readonly long _cacheSizeLimitInBytes;
        private Timer? _cacheMaintenanceTimer;
        private readonly object _cacheInstanceLock = new object(); 

        private static Dictionary<IconType, List<string>>? _embeddedIconKeysCache;
        private static readonly object _embeddedCacheLock = new object();
        private static readonly Dictionary<string, IconType> _prefixLookup = Constants.PrefixMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        private string? _iconsFolder;

        internal static readonly HashSet<string> SupportedCustomIconExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
        };

        public enum IconType { Language, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag, CommunityRating, AspectRatio, ParentalRating, Source, FrameRate, OriginalLanguage }

        private readonly object _customKeysLock = new();
        private string? _customKeysFolder;
        private Dictionary<IconType, List<string>>? _customIconKeys;

        public IconCacheManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _cacheSizeLimitInBytes = 100 * 1024 * 1024; // 100 MB

            _iconImageCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = _cacheSizeLimitInBytes
            });

            try
            {
                var maintenanceInterval = TimeSpan.FromHours(Math.Max(0.5, Plugin.Instance?.Configuration.CacheMaintenanceIntervalHours ?? 1));
                _cacheMaintenanceTimer = new Timer(_ => CompactCache(), null, maintenanceInterval, maintenanceInterval);
            }
            catch
            {
            }
        }

        public Dictionary<IconType, List<string>> GetAllAvailableIconKeys(string iconsFolder)
        {
            if (string.IsNullOrEmpty(iconsFolder))
            {
                return CreateEmptyIconKeyMap();
            }

            if (!Directory.Exists(iconsFolder))
            {
                _logger.Warn($"[EmbyIcons] Custom icons folder does not exist: '{iconsFolder}'. No custom icons will be loaded.");
                return CreateEmptyIconKeyMap();
            }

            lock (_customKeysLock)
            {
                if (_customIconKeys != null &&
                    _customKeysFolder != null &&
                    string.Equals(_customKeysFolder, iconsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return _customIconKeys;
                }

                var allKeys = CreateEmptyIconKeyMap();
                try
                {
                    _logger.Debug($"[EmbyIcons] Scanning for icon keys in folder: '{iconsFolder}'");
                    foreach (var file in Directory.GetFiles(iconsFolder))
                    {
                        var ext = Path.GetExtension(file);
                        if (string.IsNullOrEmpty(ext) || !SupportedCustomIconExtensions.Contains(ext)) continue;

                        var parts = Path.GetFileNameWithoutExtension(file).Split(new[] { '.' }, 2);
                        if (parts.Length == 2 && _prefixLookup.TryGetValue(parts[0], out var iconType))
                        {
                            allKeys[iconType].Add(parts[1].ToLowerInvariant());
                        }
                    }
                    _logger.Debug($"[EmbyIcons] Finished scanning. Found keys for {allKeys.Count(kv => kv.Value.Any())} icon types.");

                    if (allKeys.ContainsKey(IconType.Resolution))
                    {
                        allKeys[IconType.Resolution] = allKeys[IconType.Resolution].OrderByDescending(x => x.Length).ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[EmbyIcons] Failed to read files from '{iconsFolder}' during key scan. This may be due to a permissions issue or an inaccessible path.", ex);
                }

                _customKeysFolder = iconsFolder;
                _customIconKeys = allKeys;
                return allKeys;
            }
        }

        public Dictionary<IconType, List<string>> GetAllAvailableEmbeddedIconKeys()
        {
            lock (_embeddedCacheLock)
            {
                if (_embeddedIconKeysCache != null) return _embeddedIconKeysCache;
            }

            var embeddedKeys = CreateEmptyIconKeyMap();

            var assembly = Assembly.GetExecutingAssembly();
            const string resourcePrefix = "EmbyIcons.EmbeddedIcons.";
            var resourceNames = assembly.GetManifestResourceNames().Where(name => name.StartsWith(resourcePrefix) && name.EndsWith(".png"));

            foreach (var name in resourceNames)
            {
                var fileNameWithExt = name.Substring(resourcePrefix.Length);
                var parts = Path.GetFileNameWithoutExtension(fileNameWithExt).Split(new[] { '_' }, 2);
                if (parts.Length == 2 && _prefixLookup.TryGetValue(parts[0], out var iconType))
                {
                    embeddedKeys[iconType].Add(parts[1].ToLowerInvariant());
                }
            }

            lock (_embeddedCacheLock)
            {
                _embeddedIconKeysCache = embeddedKeys;
            }

            return embeddedKeys;
        }

        public Task InitializeAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(_iconsFolder) &&
                string.Equals(_iconsFolder, iconsFolder, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            _iconsFolder = iconsFolder;

            return RefreshCacheOnDemandAsync(iconsFolder, cancellationToken, force: true);
        }

        public Task RefreshCacheOnDemandAsync(string iconsFolder, CancellationToken cancellationToken, bool force = false)
        {
            _iconsFolder = iconsFolder;
            _logger.Info("[EmbyIcons] Clearing all cached icon image data.");

            MemoryCache? oldCache = null;
            lock (_cacheInstanceLock)
            {
                oldCache = _iconImageCache;
                _iconImageCache = new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = _cacheSizeLimitInBytes
                });
            }

            if (oldCache != null)
            {
                try { oldCache.Compact(1.0); } catch { }
                try { oldCache.Dispose(); } catch { }
            }

            lock (_customKeysLock)
            {
                _customIconKeys = null;
                _customKeysFolder = null;
            }

            return Task.CompletedTask;
        }

        public async Task<SKImage?> GetIconAsync(string iconNameKey, IconType iconType, PluginOptions options, CancellationToken cancellationToken)
        {
            MemoryCache currentCache;
            lock (_cacheInstanceLock)
            {
                currentCache = _iconImageCache;
            }

            var loadingMode = options.IconLoadingMode;
            var prefix = Constants.PrefixMap[iconType];
            var lowerIconNameKey = iconNameKey.ToLowerInvariant();

            var customIconFileName = $"{prefix}.{lowerIconNameKey}";
            var embeddedIconFileName = $"embedded_{prefix}_{lowerIconNameKey}";
            var customIconsFolder = options.IconsFolder;

            if (iconType == IconType.CommunityRating && lowerIconNameKey.StartsWith("t."))
            {
                customIconFileName = lowerIconNameKey;
            }

            switch (loadingMode)
            {
                case IconLoadingMode.CustomOnly:
                    return await LoadCustomIconAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache);
                case IconLoadingMode.BuiltInOnly:
                    return await TryLoadEmbeddedVariantsAsync(prefix, lowerIconNameKey, cancellationToken, currentCache);
                default: 
                    var customIcon = await LoadCustomIconAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache);
                    return customIcon ?? await TryLoadEmbeddedVariantsAsync(prefix, lowerIconNameKey, cancellationToken, currentCache);
            }
        }

        private async Task<SKImage?> TryLoadEmbeddedVariantsAsync(string prefix, string lowerIconNameKey, CancellationToken cancellationToken, MemoryCache cache)
        {
            var primary = $"embedded_{prefix}_{lowerIconNameKey}";
            var img = await LoadEmbeddedIconAsync(primary, cancellationToken, cache);
            if (img != null) return img;

            if (lowerIconNameKey.Contains('.'))
            {
                var replaced = lowerIconNameKey.Replace('.', '_');
                img = await LoadEmbeddedIconAsync($"embedded_{prefix}_{replaced}", cancellationToken, cache);
                if (img != null) return img;

                var parts = lowerIconNameKey.Split(new[] { '.' }, 2);
                if (parts.Length == 2)
                {
                    img = await LoadEmbeddedIconAsync($"embedded_{parts[0]}_{parts[1]}", cancellationToken, cache);
                    if (img != null) return img;
                }
            }

            if (lowerIconNameKey.Contains('_'))
            {
                var parts = lowerIconNameKey.Split(new[] { '_' }, 2);
                if (parts.Length == 2)
                {
                    var second = parts[1];
                    img = await LoadEmbeddedIconAsync($"embedded_{prefix}_{second}", cancellationToken, cache);
                    if (img != null) return img;
                }
            }

            img = await LoadEmbeddedIconAsync($"embedded_{lowerIconNameKey}", cancellationToken, cache);
            return img;
        }

        private async Task<SKImage?> LoadCustomIconAsync(string baseFileName, string iconsFolder, CancellationToken cancellationToken, MemoryCache cache)
        {
            if (string.IsNullOrEmpty(iconsFolder)) return null;

            if (cache.TryGetValue(baseFileName, out byte[]? cachedBytes) && cachedBytes != null)
            {
                try
                {
                    using var ms = new MemoryStream(cachedBytes);
                    var bmp = SKBitmap.Decode(ms);
                    if (bmp != null)
                    {
                        var img = SKImage.FromBitmap(bmp);
                        bmp.Dispose();
                        return img;
                    }
                }
                catch { }
            }

            foreach (var ext in SupportedCustomIconExtensions)
            {
                var fullPath = Path.Combine(iconsFolder, baseFileName + ext);
                if (File.Exists(fullPath))
                {
                    var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length == 0) return null;

                    var cacheEntryOptions = new MemoryCacheEntryOptions()
                        .SetSize(bytes.LongLength)
                        .SetSlidingExpiration(TimeSpan.FromHours(2))
                        .RegisterPostEvictionCallback((key, value, reason, state) => { /* no-op */ });

                    try { cache.Set(baseFileName, bytes, cacheEntryOptions); } catch { }

                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        var bmp = SKBitmap.Decode(ms);
                        if (bmp != null)
                        {
                            var image = SKImage.FromBitmap(bmp);
                            bmp.Dispose();
                            return image;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private async Task<SKImage?> LoadEmbeddedIconAsync(string cacheKey, CancellationToken cancellationToken, MemoryCache cache)
        {
            if (cache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes != null)
            {
                try
                {
                    using var ms = new MemoryStream(cachedBytes);
                    var bmp = SKBitmap.Decode(ms);
                    if (bmp != null)
                    {
                        var img = SKImage.FromBitmap(bmp);
                        bmp.Dispose();
                        return img;
                    }
                }
                catch { }
            }

            var resourceName = $"EmbyIcons.EmbeddedIcons.{cacheKey.Substring("embedded_".Length)}.png";

            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            var bytes = memoryStream.ToArray();

            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetSize(bytes.LongLength)
                .SetSlidingExpiration(TimeSpan.FromHours(2))
                .RegisterPostEvictionCallback((key, value, reason, state) => { /* no-op */ });

            try { cache.Set(cacheKey, bytes, cacheEntryOptions); } catch { }

            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = SKBitmap.Decode(ms);
                if (bmp != null)
                {
                    var img = SKImage.FromBitmap(bmp);
                    bmp.Dispose();
                    return img;
                }
            }
            catch { }

            return null;
        }

        public void Dispose()
        {
            lock (_cacheInstanceLock)
            {
                try { _iconImageCache?.Compact(1.0); } catch { }
                try { _iconImageCache?.Dispose(); } catch { }
            }

            lock (_customKeysLock)
            {
                _customIconKeys = null;
                _customKeysFolder = null;
            }
            
            try { _cacheMaintenanceTimer?.Dispose(); } catch { }
            _cacheMaintenanceTimer = null;
        }

        private void CompactCache()
        {
            try
            {
                lock (_cacheInstanceLock)
                {
                    _iconImageCache?.Compact(0.1);
                }

                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug("[EmbyIcons] Performed cache compaction for icon image cache.");
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[EmbyIcons] Error during icon cache compaction.", ex);
            }
        }

        private static Dictionary<IconType, List<string>> CreateEmptyIconKeyMap()
        {
            var dict = new Dictionary<IconType, List<string>>();
            foreach (IconType type in Enum.GetValues(typeof(IconType))) dict[type] = new List<string>();
            return dict;
        }
    }
}