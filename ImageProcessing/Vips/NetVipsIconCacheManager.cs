using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Caching.Memory;
using NetVips;

namespace EmbyIcons.ImageProcessing.Vips
{
    internal class NetVipsIconCacheManager : IDisposable
    {
        private readonly ILogger _logger;
        private volatile MemoryCache _iconBytesCache;
        private readonly long _cacheSizeLimitInBytes;
        private Timer? _cacheMaintenanceTimer;
        private readonly object _cacheInstanceLock = new object();

        private volatile string? _iconsFolder;
        private readonly object _initLock = new object();

        private static readonly TimeSpan MissingCustomIconRetryInterval = TimeSpan.FromMinutes(2);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _missingCustomIcons = new(StringComparer.Ordinal);

        private static MemoryCacheEntryOptions CreateBytesCacheEntryOptions(int byteCount)
        {
            return new MemoryCacheEntryOptions()
                .SetSize(byteCount)
                .SetSlidingExpiration(TimeSpan.FromHours(2));
        }

        public NetVipsIconCacheManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _cacheSizeLimitInBytes = 20 * 1024 * 1024;

            _iconBytesCache = new MemoryCache(new MemoryCacheOptions
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
                _logger.Warn($"[EmbyIcons] Failed to initialize NetVips cache maintenance timer: {ex.Message}");
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

            MemoryCache? oldCache;
            lock (_cacheInstanceLock)
            {
                oldCache = _iconBytesCache;
                _iconBytesCache = new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = _cacheSizeLimitInBytes
                });
            }

            if (oldCache != null)
            {
                try { oldCache.Compact(1.0); } catch { }
                try { oldCache.Dispose(); } catch { }
            }

            _missingCustomIcons.Clear();
        }

        public async Task<Image?> GetIconAsync(string iconNameKey, IconCacheManager.IconType iconType, PluginOptions options, CancellationToken cancellationToken)
        {
            var currentCache = _iconBytesCache;

            var loadingMode = options.IconLoadingMode;
            var prefix = Constants.PrefixMap[iconType];
            var lowerIconNameKey = iconNameKey.ToLowerInvariant();

            var customIconFileName = $"{prefix}.{lowerIconNameKey}";
            var embeddedPrefix = prefix;
            var customIconsFolder = options.IconsFolder;

            if (iconType == IconCacheManager.IconType.CommunityRating && lowerIconNameKey == "heart")
            {
                customIconFileName = "f.heart";
                embeddedPrefix = "f";
            }
            else if (iconType == IconCacheManager.IconType.CommunityRating && lowerIconNameKey.StartsWith("t."))
            {
                customIconFileName = lowerIconNameKey;
            }

            byte[]? bytes;
            switch (loadingMode)
            {
                case IconLoadingMode.CustomOnly:
                    bytes = await LoadCustomIconBytesAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache).ConfigureAwait(false);
                    break;
                case IconLoadingMode.BuiltInOnly:
                    bytes = await TryLoadEmbeddedVariantBytesAsync(embeddedPrefix, lowerIconNameKey, cancellationToken, currentCache).ConfigureAwait(false);
                    break;
                default:
                    bytes = await LoadCustomIconBytesAsync(customIconFileName, customIconsFolder, cancellationToken, currentCache).ConfigureAwait(false)
                        ?? await TryLoadEmbeddedVariantBytesAsync(embeddedPrefix, lowerIconNameKey, cancellationToken, currentCache).ConfigureAwait(false);
                    break;
            }

            if (bytes == null) return null;

            try
            {
                return Image.NewFromBuffer(bytes);
            }
            catch (Exception ex)
            {
                _logger.Debug($"[EmbyIcons] NetVips failed to decode icon '{iconNameKey}': {ex.Message}");
                return null;
            }
        }

        private async Task<byte[]?> TryLoadEmbeddedVariantBytesAsync(string prefix, string lowerIconNameKey, CancellationToken cancellationToken, MemoryCache cache)
        {
            var primary = $"embedded_{prefix}_{lowerIconNameKey}";
            var bytes = await LoadEmbeddedIconBytesAsync(primary, cancellationToken, cache).ConfigureAwait(false);
            if (bytes != null) return bytes;

            if (lowerIconNameKey.Contains('.'))
            {
                var replaced = lowerIconNameKey.Replace('.', '_');
                bytes = await LoadEmbeddedIconBytesAsync($"embedded_{prefix}_{replaced}", cancellationToken, cache).ConfigureAwait(false);
                if (bytes != null) return bytes;

                var parts = lowerIconNameKey.Split(new[] { '.' }, 2);
                if (parts.Length == 2)
                {
                    bytes = await LoadEmbeddedIconBytesAsync($"embedded_{parts[0]}_{parts[1]}", cancellationToken, cache).ConfigureAwait(false);
                    if (bytes != null) return bytes;
                }
            }

            if (lowerIconNameKey.Contains('_'))
            {
                var parts = lowerIconNameKey.Split(new[] { '_' }, 2);
                if (parts.Length == 2)
                {
                    var second = parts[1];
                    bytes = await LoadEmbeddedIconBytesAsync($"embedded_{prefix}_{second}", cancellationToken, cache).ConfigureAwait(false);
                    if (bytes != null) return bytes;
                }
            }

            return await LoadEmbeddedIconBytesAsync($"embedded_{lowerIconNameKey}", cancellationToken, cache).ConfigureAwait(false);
        }

        private async Task<byte[]?> LoadCustomIconBytesAsync(string baseFileName, string iconsFolder, CancellationToken cancellationToken, MemoryCache cache)
        {
            if (string.IsNullOrEmpty(iconsFolder)) return null;

            if (cache.TryGetValue(baseFileName, out byte[]? cachedBytes) && cachedBytes != null)
            {
                return cachedBytes;
            }

            var missingKey = iconsFolder + "|" + baseFileName;
            if (_missingCustomIcons.TryGetValue(missingKey, out var missingSince) &&
                DateTime.UtcNow - missingSince < MissingCustomIconRetryInterval)
            {
                return null;
            }

            foreach (var ext in IconCacheManager.SupportedCustomIconExtensions)
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
                            await fs.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                            bytes = ms.ToArray();
                        }

                        try { cache.Set(baseFileName, bytes, CreateBytesCacheEntryOptions(bytes.Length)); }
                        catch { }

                        return bytes;
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

        private async Task<byte[]?> LoadEmbeddedIconBytesAsync(string cacheKey, CancellationToken cancellationToken, MemoryCache cache)
        {
            if (cache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes != null)
            {
                return cachedBytes;
            }

            var resourceName = $"EmbyIcons.EmbeddedIcons.{cacheKey.Substring("embedded_".Length)}.png";

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return null;

            try
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                var bytes = ms.ToArray();

                try { cache.Set(cacheKey, bytes, CreateBytesCacheEntryOptions(bytes.Length)); }
                catch { }

                return bytes;
            }
            catch
            {
                return null;
            }
        }

        public long EstimatedCacheBytes => _iconBytesCache?.CurrentSize ?? 0;

        private void CompactCache()
        {
            try
            {
                lock (_cacheInstanceLock)
                {
                    _iconBytesCache?.Compact(0.1);
                }

                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger.Debug("[EmbyIcons] Performed cache compaction for NetVips icon byte cache.");
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[EmbyIcons] Error during NetVips icon cache compaction.", ex);
            }
        }

        public void Dispose()
        {
            lock (_cacheInstanceLock)
            {
                try { _iconBytesCache?.Compact(1.0); } catch { }
                try { _iconBytesCache?.Dispose(); } catch { }
            }

            try { _cacheMaintenanceTimer?.Dispose(); } catch { }
            _cacheMaintenanceTimer = null;
        }
    }
}
