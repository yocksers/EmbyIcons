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
        private volatile MemoryCache _iconImageCache;
        private readonly long _cacheSizeLimitInBytes;
        private Timer? _cacheMaintenanceTimer;
        private readonly object _cacheInstanceLock = new object(); 

        private static Dictionary<IconType, List<string>>? _embeddedIconKeysCache;
        private static readonly object _embeddedCacheLock = new object();
        private static readonly Dictionary<string, IconType> _prefixLookup = Constants.PrefixMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        private volatile string? _iconsFolder;
        private readonly object _initLock = new object();

        private static readonly TimeSpan MissingCustomIconRetryInterval = TimeSpan.FromMinutes(2);
        private readonly ConcurrentDictionary<string, DateTime> _missingCustomIcons = new(StringComparer.Ordinal);

        private readonly object _bitmapGate = new object();

        private MemoryCacheEntryOptions CreateBitmapCacheEntryOptions(SKBitmap bitmap)
        {
            return new MemoryCacheEntryOptions()
                .SetSize(bitmap.ByteCount)
                .SetSlidingExpiration(TimeSpan.FromHours(2))
                .RegisterPostEvictionCallback((_, value, _, _) => DisposeCachedBitmap(value as SKBitmap));
        }

        private void DisposeCachedBitmap(SKBitmap? bitmap)
        {
            if (bitmap == null) return;

            lock (_bitmapGate)
            {
                try { bitmap.Dispose(); } catch { }
            }
        }

        private SKImage? TryGetCachedImage(MemoryCache cache, string key)
        {
            lock (_bitmapGate)
            {
                try
                {
                    if (cache.TryGetValue(key, out SKBitmap? cachedBitmap) && cachedBitmap != null && cachedBitmap.Handle != IntPtr.Zero)
                    {
                        return SKImage.FromBitmap(cachedBitmap);
                    }
                }
                catch (ObjectDisposedException) { }
            }

            return null;
        }

        private SKImage? CacheBitmapAndCreateImage(MemoryCache cache, string key, SKBitmap bitmap)
        {
            lock (_bitmapGate)
            {
                SKImage? image;
                try
                {
                    image = SKImage.FromBitmap(bitmap);
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }

                if (image == null)
                {
                    bitmap.Dispose();
                    return null;
                }

                try { cache.Set(key, bitmap, CreateBitmapCacheEntryOptions(bitmap)); }
                catch { bitmap.Dispose(); }

                return image;
            }
        }

        internal static readonly HashSet<string> SupportedCustomIconExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
        };

        public enum IconType { Language, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag, CommunityRating, AspectRatio, ParentalRating, Source, FrameRate, OriginalLanguage, SeriesStatus, SampleRate, AudioBitRate, BitDepth }

        private readonly object _customKeysLock = new();
        private string? _customKeysFolder;
        private Dictionary<IconType, List<string>>? _customIconKeys;

        public IconCacheManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _cacheSizeLimitInBytes = 100 * 1024 * 1024;

            _iconImageCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = _cacheSizeLimitInBytes
            });

            try
            {
                var maintenanceInterval = TimeSpan.FromHours(Math.Max(0.5, Plugin.Instance?.Configuration.CacheMaintenanceIntervalHours ?? 1));
                _cacheMaintenanceTimer = new Timer(_ => CompactCache(), null, maintenanceInterval, maintenanceInterval);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EmbyIcons] Failed to initialize cache maintenance timer: {ex.Message}");
            }
        }

        public Dictionary<IconType, List<string>> GetAllAvailableIconKeys(string iconsFolder)
        {
            if (string.IsNullOrEmpty(iconsFolder))
            {
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
            }

            if (!Directory.Exists(iconsFolder))
            {
                _logger.Warn($"[EmbyIcons] Custom icons folder does not exist: '{iconsFolder}'. No custom icons will be loaded.");
                var emptyKeys = CreateEmptyIconKeyMap();
                lock (_customKeysLock)
                {
                    _customKeysFolder = iconsFolder;
                    _customIconKeys = emptyKeys;
                }
                return emptyKeys;
            }

            var allKeys = CreateEmptyIconKeyMap();
            string[]? files = null;
            try
            {
                _logger.Debug($"[EmbyIcons] Scanning for icon keys in folder: '{iconsFolder}'");
                files = Directory.GetFiles(iconsFolder);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Failed to read files from '{iconsFolder}' during key scan. This may be due to a permissions issue or an inaccessible path.", ex);
            }

            if (files != null)
            {
                foreach (var file in files)
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

            lock (_customKeysLock)
            {
                if (_customIconKeys != null &&
                    _customKeysFolder != null &&
                    string.Equals(_customKeysFolder, iconsFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return _customIconKeys;
                }

                _customKeysFolder = iconsFolder;
                _customIconKeys = allKeys;
                return allKeys;
            }
        }

        public Dictionary<IconType, List<string>> GetAllAvailableEmbeddedIconKeys()
        {
            if (_embeddedIconKeysCache != null) return _embeddedIconKeysCache;

            lock (_embeddedCacheLock)
            {
                if (_embeddedIconKeysCache != null) return _embeddedIconKeysCache;

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

                _embeddedIconKeysCache = embeddedKeys;
                return embeddedKeys;
            }
        }

        public void Initialize(string iconsFolder)
        {
            var effectiveFolder = iconsFolder ?? string.Empty;

            lock (_initLock)
            {
                if (_iconsFolder != null &&
                    string.Equals(_iconsFolder, effectiveFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _iconsFolder = effectiveFolder;
            }

            RefreshCache(effectiveFolder);
        }

        public void RefreshCache(string iconsFolder)
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

            _missingCustomIcons.Clear();
        }

        public async Task<SKImage?> GetIconAsync(string iconNameKey, IconType iconType, PluginOptions options, CancellationToken cancellationToken)
        {
            var currentCache = _iconImageCache;

            var loadingMode = options.IconLoadingMode;
            var prefix = Constants.PrefixMap[iconType];
            var lowerIconNameKey = iconNameKey.ToLowerInvariant();

            var customIconFileName = $"{prefix}.{lowerIconNameKey}";
            var embeddedPrefix = prefix;
            var customIconsFolder = options.IconsFolder;

            if (iconType == IconType.CommunityRating && lowerIconNameKey == "heart")
            {
                customIconFileName = "f.heart";
                embeddedPrefix = "f";
            }
            else if (iconType == IconType.CommunityRating && lowerIconNameKey.StartsWith("t."))
            {
                customIconFileName = lowerIconNameKey;
            }

            switch (loadingMode)
            {
                case IconLoadingMode.CustomOnly:
                    return await LoadCustomIconAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache);
                case IconLoadingMode.BuiltInOnly:
                    return await TryLoadEmbeddedVariantsAsync(embeddedPrefix, lowerIconNameKey, cancellationToken, currentCache);
                default: 
                    var customIcon = await LoadCustomIconAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache);
                    return customIcon ?? await TryLoadEmbeddedVariantsAsync(embeddedPrefix, lowerIconNameKey, cancellationToken, currentCache);
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

            var cachedImage = TryGetCachedImage(cache, baseFileName);
            if (cachedImage != null)
            {
                return cachedImage;
            }

            var missingKey = iconsFolder + "|" + baseFileName;
            if (_missingCustomIcons.TryGetValue(missingKey, out var missingSince) &&
                DateTime.UtcNow - missingSince < MissingCustomIconRetryInterval)
            {
                return null;
            }

            foreach (var ext in SupportedCustomIconExtensions)
            {
                var fullPath = Path.Combine(iconsFolder, baseFileName + ext);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        byte[] bytes;
                        using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                        {
                            if (fs.Length == 0) return null;
                            using var ms = new MemoryStream((int)fs.Length);
                            await fs.CopyToAsync(ms, 81920, cancellationToken);
                            bytes = ms.ToArray();
                        }

                        var bitmap = SKBitmap.Decode(bytes);
                        if (bitmap == null)
                        {
                            _logger.Debug($"[EmbyIcons] Failed to decode icon file: {fullPath}");
                            return null;
                        }

                        return CacheBitmapAndCreateImage(cache, baseFileName, bitmap);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"[EmbyIcons] Error loading icon file '{fullPath}': {ex.Message}");
                    }
                }
            }

            _missingCustomIcons[missingKey] = DateTime.UtcNow;
            return null;
        }

        private async Task<SKImage?> LoadEmbeddedIconAsync(string cacheKey, CancellationToken cancellationToken, MemoryCache cache)
        {
            var cachedImage = TryGetCachedImage(cache, cacheKey);
            if (cachedImage != null)
            {
                return cachedImage;
            }

            var resourceName = $"EmbyIcons.EmbeddedIcons.{cacheKey.Substring("embedded_".Length)}.png";

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            try
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, 81920, cancellationToken);
                byte[] bytes = ms.ToArray();

                var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) return null;

                return CacheBitmapAndCreateImage(cache, cacheKey, bitmap);
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

        public long EstimatedCacheBytes => _iconImageCache?.CurrentSize ?? 0;

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