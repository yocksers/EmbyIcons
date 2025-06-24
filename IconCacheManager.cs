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
using System.Reflection;

namespace EmbyIcons.Helpers
{
    public class IconCacheManager : IDisposable
    {
        private readonly TimeSpan _cacheTtl;
        private readonly ILogger _logger;
        private readonly int _maxParallelism;
        private readonly ConcurrentDictionary<string, string> _audioIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _subtitleIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _channelIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _videoFormatIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _resolutionIconPathCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _audioIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _subtitleIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _channelIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _videoFormatIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _resolutionIconImageCache = new();
        private string? _iconsFolder;
        private DateTime _lastCacheRefreshTime = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _iconFileLastWriteTimes = new();
        private string _currentIconVersion = string.Empty;
        public event EventHandler<string>? CacheRefreshedWithVersion;

        public enum IconType { Audio, Subtitle, Channel, VideoFormat, Resolution }

        public IconCacheManager(TimeSpan cacheTtl, ILogger logger, int maxParallelism = 4)
        {
            _cacheTtl = cacheTtl;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxParallelism = maxParallelism;
        }

        public Task InitializeAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            if (_iconsFolder != iconsFolder || (DateTime.UtcNow - _lastCacheRefreshTime) > _cacheTtl)
            {
                _iconsFolder = iconsFolder;
                return RefreshCacheAsync(cancellationToken, force: _iconsFolder != iconsFolder);
            }
            return Task.CompletedTask;
        }

        public Task RefreshCacheOnDemandAsync(CancellationToken cancellationToken, bool force = false) => RefreshCacheAsync(cancellationToken, force);

        private Task RefreshCacheAsync(CancellationToken cancellationToken, bool force = false)
        {
            if (string.IsNullOrEmpty(_iconsFolder) || !Directory.Exists(_iconsFolder))
            {
                _logger.Warn($"[EmbyIcons] Icons folder '{_iconsFolder}' is not valid. Cache cannot be refreshed.");
                return Task.CompletedTask;
            }

            var allFiles = Directory.GetFiles(_iconsFolder);
            var currentWriteTimes = allFiles.ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
            bool anyChanged = force || currentWriteTimes.Count != _iconFileLastWriteTimes.Count || currentWriteTimes.Any(kvp => !_iconFileLastWriteTimes.TryGetValue(kvp.Key, out var knownWrite) || knownWrite != kvp.Value);

            if (!anyChanged && (DateTime.UtcNow - _lastCacheRefreshTime) <= _cacheTtl) return Task.CompletedTask;

            _iconFileLastWriteTimes.Clear();
            foreach (var (key, value) in currentWriteTimes) _iconFileLastWriteTimes[key] = value;

            _audioIconPathCache.Clear();
            _subtitleIconPathCache.Clear();
            _channelIconPathCache.Clear();
            _videoFormatIconPathCache.Clear();
            _resolutionIconPathCache.Clear();
            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);
            ClearImageCache(_channelIconImageCache);
            ClearImageCache(_videoFormatIconImageCache);
            ClearImageCache(_resolutionIconImageCache);

            var channelNames = new HashSet<string> { "mono", "stereo", "5.1", "7.1" };
            var formatNames = new HashSet<string> { "hdr", "dv", "hdr10plus" };
            var resolutionNames = new HashSet<string> { "480p", "576p", "720p", "1080p", "4k" };

            foreach (var file in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || ext == ".db" || ext == ".ini") continue;

                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var dict = name.StartsWith("srt.") ? _subtitleIconPathCache : channelNames.Contains(name) ? _channelIconPathCache : formatNames.Contains(name) ? _videoFormatIconPathCache : resolutionNames.Contains(name) ? _resolutionIconPathCache : _audioIconPathCache;
                dict.TryAdd(name, file);
            }

            _lastCacheRefreshTime = DateTime.UtcNow;
            var newVersion = ComputeIconFilesVersion(allFiles);
            if (newVersion != _currentIconVersion)
            {
                _currentIconVersion = newVersion;
                CacheRefreshedWithVersion?.Invoke(this, _currentIconVersion);
                _logger.Debug($"[EmbyIcons] Icon cache refreshed. New version: {_currentIconVersion}");
            }
            return Task.CompletedTask;
        }

        public SKImage? GetFirstAvailableIcon(IconType iconType)
        {
            var (pathCache, _) = GetCachesForType(iconType);

            var firstKey = pathCache.Keys.FirstOrDefault(key =>
                pathCache.TryGetValue(key, out var path) && !string.IsNullOrEmpty(path));

            if (firstKey != null)
            {
                return GetCachedIcon(firstKey, iconType);
            }

            return null;
        }

        public SKImage? GetCachedIcon(string iconNameKey, IconType iconType)
        {
            if (_iconsFolder == null) return null;
            iconNameKey = iconNameKey.ToLowerInvariant();

            var (pathCache, imageCache) = GetCachesForType(iconType);
            var iconPath = ResolveIconPathWithFallback(iconNameKey, pathCache);

            if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return null;

            if (imageCache.TryGetValue(iconPath, out var cached) && cached.FileWriteTimeUtc == File.GetLastWriteTimeUtc(iconPath)) return cached.Image;

            return TryLoadAndCacheIcon(iconPath, imageCache);
        }

        private SKImage? TryLoadAndCacheIcon(string iconPath, ConcurrentDictionary<string, CachedIcon> imageCache)
        {
            try
            {
                if (new FileInfo(iconPath).Length < 50)
                {
                    _logger.Warn($"[EmbyIcons] Skipping small/corrupt icon: '{iconPath}'.");
                    return null;
                }
                using var stream = File.OpenRead(iconPath);
                using var data = SKData.Create(stream);
                var img = SKImage.FromEncodedData(data);
                var lastWrite = File.GetLastWriteTimeUtc(iconPath);
                imageCache[iconPath] = new CachedIcon(img, lastWrite);
                return img;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Failed to load icon '{iconPath}'.", ex);
                return null;
            }
        }

        private string ResolveIconPathWithFallback(string iconNameKey, ConcurrentDictionary<string, string> cache)
        {
            if (cache.TryGetValue(iconNameKey, out var path)) return path;
            if (iconNameKey.Length == 3 && cache == _audioIconPathCache && cache.TryGetValue(iconNameKey.Substring(0, 2), out path)) return cache.GetOrAdd(iconNameKey, path);
            cache.TryAdd(iconNameKey, string.Empty);
            return string.Empty;
        }

        private (ConcurrentDictionary<string, string>, ConcurrentDictionary<string, CachedIcon>) GetCachesForType(IconType iconType) => iconType switch
        {
            IconType.Audio => (_audioIconPathCache, _audioIconImageCache),
            IconType.Subtitle => (_subtitleIconPathCache, _subtitleIconImageCache),
            IconType.Channel => (_channelIconPathCache, _channelIconImageCache),
            IconType.VideoFormat => (_videoFormatIconPathCache, _videoFormatIconImageCache),
            IconType.Resolution => (_resolutionIconPathCache, _resolutionIconImageCache),
            _ => throw new ArgumentOutOfRangeException(nameof(iconType))
        };

        private static string ComputeIconFilesVersion(string[] files)
        {
            using var md5 = MD5.Create();
            var combined = string.Join("|", files.OrderBy(f => f).Select(f => $"{f.ToLowerInvariant()}:{File.GetLastWriteTimeUtc(f).Ticks}"));
            var bytes = Encoding.UTF8.GetBytes(combined);
            return Convert.ToBase64String(md5.ComputeHash(bytes));
        }

        public void Dispose()
        {
            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);
            ClearImageCache(_channelIconImageCache);
            ClearImageCache(_videoFormatIconImageCache);
            ClearImageCache(_resolutionIconImageCache);
        }

        private void ClearImageCache(ConcurrentDictionary<string, CachedIcon> cache)
        {
            foreach (var cached in cache.Values) cached.Image?.Dispose();
            cache.Clear();
        }

        private sealed class CachedIcon
        {
            public SKImage? Image { get; }
            public DateTime FileWriteTimeUtc { get; }
            public CachedIcon(SKImage? image, DateTime fileWriteTimeUtc) { Image = image; FileWriteTimeUtc = fileWriteTimeUtc; }
        }
    }
}