﻿using System;
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
        private readonly ConcurrentDictionary<string, string> _audioCodecIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _videoCodecIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _tagIconPathCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _audioIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _subtitleIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _channelIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _videoFormatIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _resolutionIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _audioCodecIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _videoCodecIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _tagIconImageCache = new();
        private string? _iconsFolder;
        private DateTime _lastCacheRefreshTime = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _iconFileLastWriteTimes = new();
        private string _currentIconVersion = string.Empty;
        public event EventHandler<string>? CacheRefreshedWithVersion;

        public enum IconType { Audio, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag }

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
            var currentWriteTimes = allFiles.ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.Ordinal);
            bool anyChanged = force || currentWriteTimes.Count != _iconFileLastWriteTimes.Count || currentWriteTimes.Any(kvp => !_iconFileLastWriteTimes.TryGetValue(kvp.Key, out var knownWrite) || knownWrite != kvp.Value);

            if (!anyChanged && (DateTime.UtcNow - _lastCacheRefreshTime) <= _cacheTtl) return Task.CompletedTask;

            _iconFileLastWriteTimes.Clear();
            foreach (var (key, value) in currentWriteTimes) _iconFileLastWriteTimes[key] = value;

            _audioIconPathCache.Clear();
            _subtitleIconPathCache.Clear();
            _channelIconPathCache.Clear();
            _videoFormatIconPathCache.Clear();
            _resolutionIconPathCache.Clear();
            _audioCodecIconPathCache.Clear();
            _videoCodecIconPathCache.Clear();
            _tagIconPathCache.Clear();
            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);
            ClearImageCache(_channelIconImageCache);
            ClearImageCache(_videoFormatIconImageCache);
            ClearImageCache(_resolutionIconImageCache);
            ClearImageCache(_audioCodecIconImageCache);
            ClearImageCache(_videoCodecIconImageCache);
            ClearImageCache(_tagIconImageCache);

            var channelNames = new HashSet<string> { "mono", "stereo", "5.1", "7.1" };
            var formatNames = new HashSet<string> { "hdr", "dv", "hdr10plus" };
            var resolutionNames = new HashSet<string> { "480p", "576p", "720p", "1080p", "4k" };
            var audioCodecNames = new HashSet<string> { "aac", "pcm", "flac", "mp3", "ac3", "eac3", "dts", "truehd" };
            var videoCodecNames = new HashSet<string> { "av1", "avc", "h264", "h265", "mp4", "vc1", "vp9", "h266" };

            foreach (var file in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || ext == ".db" || ext == ".ini") continue;

                var name = Path.GetFileNameWithoutExtension(file); // Keep case for tags
                var lowerName = name.ToLowerInvariant();

                ConcurrentDictionary<string, string> dict;
                if (lowerName.StartsWith("srt.")) dict = _subtitleIconPathCache;
                else if (channelNames.Contains(lowerName)) dict = _channelIconPathCache;
                else if (formatNames.Contains(lowerName)) dict = _videoFormatIconPathCache;
                else if (resolutionNames.Contains(lowerName)) dict = _resolutionIconPathCache;
                else if (audioCodecNames.Contains(lowerName)) dict = _audioCodecIconPathCache;
                else if (videoCodecNames.Contains(lowerName)) dict = _videoCodecIconPathCache;
                else
                {
                    // For anything else, add it to both audio (for languages) and tags
                    _audioIconPathCache.TryAdd(lowerName, file);
                    _tagIconPathCache.TryAdd(name, file); // Use original case for tag key
                    continue;
                }
                dict.TryAdd(lowerName, file);
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
            var processedKey = iconType == IconType.Tag ? iconNameKey : iconNameKey.ToLowerInvariant();

            var (pathCache, imageCache) = GetCachesForType(iconType);

            // Optimization: If the image is already cached, return it immediately.
            // The RefreshCacheAsync method is responsible for invalidating the cache.
            if (pathCache.TryGetValue(processedKey, out var iconPath) && !string.IsNullOrEmpty(iconPath))
            {
                if (imageCache.TryGetValue(iconPath, out var cached))
                {
                    return cached.Image;
                }
            }

            // Fallback for paths and loading if not in image cache
            iconPath = ResolveIconPathWithFallback(processedKey, pathCache);
            if (string.IsNullOrEmpty(iconPath)) return null;

            // Re-check image cache in case it was populated by another thread
            if (imageCache.TryGetValue(iconPath, out var cachedAfterResolve))
            {
                return cachedAfterResolve.Image;
            }

            return TryLoadAndCacheIcon(iconPath, imageCache);
        }

        private SKImage? TryLoadAndCacheIcon(string iconPath, ConcurrentDictionary<string, CachedIcon> imageCache)
        {
            try
            {
                if (!File.Exists(iconPath) || new FileInfo(iconPath).Length < 50)
                {
                    if (!File.Exists(iconPath)) _logger.Warn($"[EmbyIcons] Icon file not found: '{iconPath}'.");
                    else _logger.Warn($"[EmbyIcons] Skipping small/corrupt icon: '{iconPath}'.");
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

            // Fallback for audio language codes (e.g., 'en' to 'eng')
            if (iconNameKey.Length == 3 && cache == _audioIconPathCache && cache.TryGetValue(iconNameKey.Substring(0, 2), out path))
                return cache.GetOrAdd(iconNameKey, path);

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
            IconType.AudioCodec => (_audioCodecIconPathCache, _audioCodecIconImageCache),
            IconType.VideoCodec => (_videoCodecIconPathCache, _videoCodecIconImageCache),
            IconType.Tag => (_tagIconPathCache, _tagIconImageCache),
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
            ClearImageCache(_audioCodecIconImageCache);
            ClearImageCache(_videoCodecIconImageCache);
            ClearImageCache(_tagIconImageCache);
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