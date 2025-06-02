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

            // All files in icons folder, regardless of extension
            var allFiles = Directory.GetFiles(_iconsFolder);

            bool anyChanged = false;
            var currentWriteTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in allFiles)
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

            // Map icon base names to file paths, supporting all Skia formats
            foreach (var file in allFiles)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || ext == ".db" || ext == ".ini") continue;

                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var isSubtitle = name.StartsWith("srt.");
                var dict = isSubtitle ? _subtitleIconPathCache : _audioIconPathCache;

                if (!dict.ContainsKey(name))
                {
                    dict[name] = file;
                }
                else
                {
                    _logger.Warn($"Duplicate icon name detected ('{name}'), files: '{dict[name]}' and '{file}'. Using '{dict[name]}' only.");
                }
            }

            _lastCacheRefreshTime = DateTime.UtcNow;

            var version = ComputeIconFilesVersion(allFiles);

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
                var info = new FileInfo(iconPath);
                if (info.Length < 50) // Arbitrary, real icon will be bigger than this
                {
                    _logger.Warn($"[EmbyIcons] Skipping icon '{iconPath}' because it is too small or corrupt.");
                    CacheIcon(iconPath, null, DateTime.MinValue, isSubtitle);
                    return;
                }

                using var stream = File.OpenRead(iconPath);
                var data = SKData.Create(stream);
                var img = data != null ? SKImage.FromEncodedData(data) : null;
                var lastWrite = File.GetLastWriteTimeUtc(iconPath);
                CacheIcon(iconPath, img, lastWrite, isSubtitle);
            }
            catch (Exception ex)
            {
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

            string? iconPath = ResolveIconPathWithFallback(langCodeKey, pathCache);

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

        private string? ResolveIconPathWithFallback(string langCodeKey, ConcurrentDictionary<string, string?> cache)
        {
            langCodeKey = langCodeKey.ToLowerInvariant();

            if (cache.TryGetValue(langCodeKey, out var path) && !string.IsNullOrEmpty(path))
                return path;

            // If langCodeKey is 3 letters, try the first two as a short code
            if (langCodeKey.Length == 3)
            {
                var shortCode = langCodeKey.Substring(0, 2);
                if (cache.TryGetValue(shortCode, out var path2) && !string.IsNullOrEmpty(path2))
                    return path2;
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
