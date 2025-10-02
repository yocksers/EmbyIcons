﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.Helpers
{
    public class IconCacheManager : IDisposable
    {
        private readonly ILogger _logger;
        private MemoryCache _iconImageCache;
        private readonly long _cacheSizeLimitInBytes;
        private readonly object _cacheInstanceLock = new object(); // Lock for cache instance safety

        private static Dictionary<IconType, List<string>>? _embeddedIconKeysCache;
        private static readonly object _embeddedCacheLock = new object();
        private static readonly Dictionary<string, IconType> _prefixLookup = Constants.PrefixMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

        private string? _iconsFolder;

        internal static readonly HashSet<string> SupportedCustomIconExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"
        };

        public enum IconType { Language, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag, CommunityRating, AspectRatio, ParentalRating, Source }

        private readonly object _customKeysLock = new();
        private string? _customKeysFolder;
        private Dictionary<IconType, List<string>>? _customIconKeys;

        public IconCacheManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _cacheSizeLimitInBytes = 250 * 1024 * 1024; // 250 MB

            _iconImageCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = _cacheSizeLimitInBytes
            });
        }

        public Dictionary<IconType, List<string>> GetAllAvailableIconKeys(string iconsFolder)
        {
            if (string.IsNullOrEmpty(iconsFolder))
            {
                return CreateEmptyIconKeyMap();
            }

            // More robust check for invalid/inaccessible paths
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

            lock (_cacheInstanceLock)
            {
                _iconImageCache.Dispose();
                _iconImageCache = new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = _cacheSizeLimitInBytes
                });
            }

            lock (_customKeysLock)
            {
                _customIconKeys = null;
                _customKeysFolder = null;
            }

            return Task.CompletedTask;
        }

        public async Task<byte[]?> GetIconBytesAsync(string iconNameKey, IconType iconType, PluginOptions options, CancellationToken cancellationToken)
        {
            // Get a stable reference to the current cache instance to prevent ObjectDisposedException
            // if a refresh happens while this async method is running.
            MemoryCache currentCache;
            lock (_cacheInstanceLock)
            {
                currentCache = _iconImageCache;
            }

            var loadingMode = options.IconLoadingMode;
            var prefix = Constants.PrefixMap[iconType];
            var lowerIconNameKey = iconNameKey.ToLowerInvariant();

            var customIconFileName = $"{prefix}.{lowerIconNameKey}";
            var embeddedIconFileName = $"{prefix}_{lowerIconNameKey}";
            var customIconsFolder = options.IconsFolder;

            if (loadingMode == IconLoadingMode.CustomOnly)
            {
                return await LoadCustomIconBytesAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache);
            }

            if (loadingMode == IconLoadingMode.BuiltInOnly)
            {
                return await LoadEmbeddedIconBytesAsync(embeddedIconFileName, cancellationToken, currentCache);
            }

            var customIconBytes = await LoadCustomIconBytesAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache);
            return customIconBytes ?? await LoadEmbeddedIconBytesAsync(embeddedIconFileName, cancellationToken, currentCache);
        }

        private async Task<byte[]?> LoadCustomIconBytesAsync(string baseFileName, string iconsFolder, CancellationToken cancellationToken, MemoryCache cache)
        {
            if (string.IsNullOrEmpty(iconsFolder)) return null;

            if (cache.TryGetValue(baseFileName, out byte[]? cachedData))
            {
                return cachedData;
            }

            foreach (var ext in SupportedCustomIconExtensions)
            {
                var fullPath = Path.Combine(iconsFolder, baseFileName + ext);
                if (File.Exists(fullPath)) return await TryLoadAndCacheIconBytesAsync(fullPath, baseFileName, cancellationToken, cache);
            }

            return null;
        }

        private async Task<byte[]?> LoadEmbeddedIconBytesAsync(string baseFileName, CancellationToken cancellationToken, MemoryCache cache)
        {
            var cacheKey = $"embedded_{baseFileName}";
            if (cache.TryGetValue(cacheKey, out byte[]? cachedData))
            {
                return cachedData;
            }

            var resourceName = $"EmbyIcons.EmbeddedIcons.{baseFileName}.png";
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            var bytes = memoryStream.ToArray();

            var cacheEntryOptions = new MemoryCacheEntryOptions()
                .SetSize(bytes.Length)
                .SetSlidingExpiration(TimeSpan.FromHours(12));

            cache.Set(cacheKey, bytes, cacheEntryOptions);

            return bytes;
        }

        private async Task<byte[]?> TryLoadAndCacheIconBytesAsync(string iconPath, string cacheKey, CancellationToken cancellationToken, MemoryCache cache)
        {
            try
            {
                if (new FileInfo(iconPath).Length < 50) return null;

                var bytes = await File.ReadAllBytesAsync(iconPath, cancellationToken);
                if (bytes.Length == 0) return null;

                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetSize(bytes.Length)
                    .SetSlidingExpiration(TimeSpan.FromHours(12));

                cache.Set(cacheKey, bytes, cacheEntryOptions);
                return bytes;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] A critical error occurred while loading icon '{iconPath}'.", ex);
                return null;
            }
        }

        public void Dispose()
        {
            lock (_cacheInstanceLock)
            {
                _iconImageCache.Dispose();
            }

            lock (_customKeysLock)
            {
                _customIconKeys = null;
                _customKeysFolder = null;
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