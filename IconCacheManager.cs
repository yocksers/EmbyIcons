using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.Helpers
{
    public class IconCacheManager : IDisposable
    {
        private readonly TimeSpan _cacheTtl;
        private readonly ILogger _logger;

        private readonly ConcurrentDictionary<string, string?> _audioIconPathCache = new();
        private readonly ConcurrentDictionary<string, string?> _subtitleIconPathCache = new();

        private readonly ConcurrentDictionary<string, CachedIcon> _audioIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _subtitleIconImageCache = new();

        private string? _iconsFolder;
        private DateTime _lastCacheRefreshTime = DateTime.MinValue;

        private readonly ConcurrentDictionary<string, DateTime> _iconFileLastWriteTimes = new();

        private string _currentIconVersion = string.Empty;

        public event EventHandler<string>? CacheRefreshedWithVersion;

        private readonly int _maxParallelism;

        public IconCacheManager(TimeSpan cacheTtl, ILogger logger, int maxParallelism = 4)
        {
            _cacheTtl = cacheTtl;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxParallelism = maxParallelism;
        }

        public Task InitializeAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            if (_iconsFolder != iconsFolder)
            {
                _iconsFolder = iconsFolder;
                return RefreshCacheAsync(cancellationToken, force: true);
            }
            else if ((DateTime.UtcNow - _lastCacheRefreshTime) > _cacheTtl)
            {
                return RefreshCacheAsync(cancellationToken);
            }
            return Task.CompletedTask;
        }

        public Task RefreshCacheOnDemandAsync(CancellationToken cancellationToken, bool force = false)
        {
            return RefreshCacheAsync(cancellationToken, force);
        }

        private Task RefreshCacheAsync(CancellationToken cancellationToken, bool force = false)
        {
            if (_iconsFolder == null || !Directory.Exists(_iconsFolder))
            {
                _logger.Warn($"Icons folder '{_iconsFolder}' does not exist or is not set. Icon cache cannot be refreshed.");
                return Task.CompletedTask;
            }

            var pngFiles = Directory.GetFiles(_iconsFolder, "*.png");

            bool anyChanged = false;
            var currentWriteTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in pngFiles)
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                currentWriteTimes[file] = lastWrite;

                if (!_iconFileLastWriteTimes.TryGetValue(file, out var knownWrite) || knownWrite != lastWrite)
                {
                    anyChanged = true;
                }
            }

            foreach (var cachedFile in _iconFileLastWriteTimes.Keys)
            {
                if (!currentWriteTimes.ContainsKey(cachedFile))
                {
                    anyChanged = true;
                    break;
                }
            }

            if (!force && !anyChanged && (DateTime.UtcNow - _lastCacheRefreshTime) <= _cacheTtl)
                return Task.CompletedTask;

            _iconFileLastWriteTimes.Clear();
            foreach (var kvp in currentWriteTimes)
                _iconFileLastWriteTimes[kvp.Key] = kvp.Value;

            _audioIconPathCache.Clear();
            _subtitleIconPathCache.Clear();

            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);

            foreach (var file in pngFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                if (name.StartsWith("srt."))
                    _subtitleIconPathCache[name] = file;
                else
                    _audioIconPathCache[name] = file;
            }

            _lastCacheRefreshTime = DateTime.UtcNow;

            var version = ComputeIconFilesVersion(pngFiles);

            bool versionChanged = version != _currentIconVersion;
            _currentIconVersion = version;

            if (anyChanged || versionChanged)
            {
                _logger.Info($"Icon cache refreshed. New version: {_currentIconVersion}. Files changed: {anyChanged}. Version changed: {versionChanged}.");
                CacheRefreshedWithVersion?.Invoke(this, _currentIconVersion);
            }

            return Task.CompletedTask;
        }

        private void ClearImageCache(ConcurrentDictionary<string, CachedIcon> cache)
        {
            foreach (var cached in cache.Values)
                cached.Image?.Dispose();

            cache.Clear();
        }

        private void TryLoadAndCacheIcon(string iconPath, bool isSubtitle)
        {
            try
            {
                using var stream = File.OpenRead(iconPath);
                var data = SKData.Create(stream);
                var img = data != null ? SKImage.FromEncodedData(data) : null;
                var lastWrite = File.GetLastWriteTimeUtc(iconPath);
                CacheIcon(iconPath, img, lastWrite, isSubtitle);
            }
            catch (Exception ex)
            {
                // FIX: Use the ErrorException extension method provided by Emby's logging for exceptions and messages.
                // This is the most robust way to log exceptions with Emby's ILogger.
                _logger.ErrorException($"Failed to load icon '{iconPath}'.", ex);
                CacheIcon(iconPath, null, DateTime.MinValue, isSubtitle);
            }
        }

        private void CacheIcon(string iconPath, SKImage? image, DateTime lastWrite, bool isSubtitle)
        {
            var cacheEntry = new CachedIcon(image, lastWrite);

            var cache = isSubtitle ? _subtitleIconImageCache : _audioIconImageCache;

            cache.AddOrUpdate(iconPath, cacheEntry,
                (key, old) =>
                {
                    old.Image?.Dispose();
                    return cacheEntry;
                });
        }

        public SKImage? GetCachedIcon(string langCodeKey, bool isSubtitle)
        {
            if (_iconsFolder == null)
                return null;

            langCodeKey = langCodeKey.ToLowerInvariant();

            var pathCache = isSubtitle ? _subtitleIconPathCache : _audioIconPathCache;
            var imageCache = isSubtitle ? _subtitleIconImageCache : _audioIconImageCache;

            string? iconPath = ResolveIconPathWithFallback(langCodeKey, _iconsFolder, pathCache);

            if (iconPath == null || !File.Exists(iconPath))
                return null;

            var currentFileWrite = File.GetLastWriteTimeUtc(iconPath);

            if (imageCache.TryGetValue(iconPath, out var cached))
            {
                if (cached.IsExpired(_cacheTtl) || cached.FileWriteTimeUtc != currentFileWrite)
                {
                    _logger.Debug($"Icon '{iconPath}' expired or file changed. Reloading.");
                    TryLoadAndCacheIcon(iconPath, isSubtitle);
                }
                return imageCache.TryGetValue(iconPath, out var latest) ? latest.Image : null;
            }

            _logger.Debug($"Icon '{iconPath}' not in cache. Loading.");
            TryLoadAndCacheIcon(iconPath, isSubtitle);
            return imageCache.TryGetValue(iconPath, out var loaded) ? loaded.Image : null;
        }

        private string? ResolveIconPathWithFallback(string langCodeKey, string folderPath,
                                                   ConcurrentDictionary<string, string?> cache)
        {
            langCodeKey = langCodeKey.ToLowerInvariant();

            if (cache.TryGetValue(langCodeKey, out var path) && !string.IsNullOrEmpty(path))
                return path;

            var candidate = Path.Combine(folderPath, $"{langCodeKey}.png");
            if (File.Exists(candidate))
            {
                cache[langCodeKey] = candidate;
                return candidate;
            }

            if (langCodeKey.Length == 3)
            {
                var shortCode = langCodeKey.Substring(0, 2);
                var shortCandidate = Path.Combine(folderPath, $"{shortCode}.png");
                if (File.Exists(shortCandidate))
                {
                    cache[langCodeKey] = shortCandidate;
                    return shortCandidate;
                }
            }

#pragma warning disable CS8604 // Possible null reference argument.
            cache[langCodeKey] = null!;
#pragma warning restore CS8604 // Possible null reference argument.

            return null;
        }

        private static string ComputeIconFilesVersion(string[] files)
        {
            using var md5 = MD5.Create();

            var combined =
              string.Join("|", files.OrderBy(f => f).Select(f =>
                  f.ToLowerInvariant() + ":" + File.GetLastWriteTimeUtc(f).Ticks));

            var bytes = Encoding.UTF8.GetBytes(combined);
            var hashBytes = md5.ComputeHash(bytes);

            return Convert.ToBase64String(hashBytes);
        }

        public void Dispose()
        {
            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);
        }

        private sealed class CachedIcon
        {
            public SKImage? Image { get; }
            public DateTime FileWriteTimeUtc { get; }
            private readonly DateTime _loadTimeUtc;

            public CachedIcon(SKImage? image, DateTime fileWriteTimeUtc)
            {
                Image = image;
                FileWriteTimeUtc = fileWriteTimeUtc;
                _loadTimeUtc = DateTime.UtcNow;
            }

            public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - _loadTimeUtc > ttl;
        }
    }
}