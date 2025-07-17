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
        private readonly ConcurrentDictionary<string, string> _audioCodecIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _videoCodecIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _tagIconPathCache = new();
        private readonly ConcurrentDictionary<string, string> _communityRatingIconPathCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _audioIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _subtitleIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _channelIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _videoFormatIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _resolutionIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _audioCodecIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _videoCodecIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _tagIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _communityRatingIconImageCache = new();
        private string? _iconsFolder;
        private DateTime _lastCacheRefreshTime = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _iconFileLastWriteTimes = new();
        private string _currentIconVersion = string.Empty;
        public event EventHandler<string>? CacheRefreshedWithVersion;
        private static readonly Random _random = new();

        private static readonly string[] SupportedIconExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

        public enum IconType { Audio, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag, CommunityRating }

        public IconCacheManager(TimeSpan cacheTtl, ILogger logger, int maxParallelism = 4)
        {
            _cacheTtl = cacheTtl;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxParallelism = maxParallelism;
        }

        public string? GetRandomIconName(IconType iconType)
        {
            var (pathCache, _) = GetCachesForType(iconType);
            if (pathCache.IsEmpty) return null;

            var keys = pathCache.Keys.ToList();
            if (!keys.Any()) return null;

            var randomKey = keys[_random.Next(keys.Count)];

            if (iconType == IconType.Subtitle && randomKey.StartsWith("srt."))
            {
                return randomKey.Substring(4);
            }

            return randomKey;
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
            _communityRatingIconPathCache.Clear();
            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);
            ClearImageCache(_channelIconImageCache);
            ClearImageCache(_videoFormatIconImageCache);
            ClearImageCache(_resolutionIconImageCache);
            ClearImageCache(_audioCodecIconImageCache);
            ClearImageCache(_videoCodecIconImageCache);
            ClearImageCache(_tagIconImageCache);
            ClearImageCache(_communityRatingIconImageCache);

            var channelNames = new HashSet<string> { "mono", "stereo", "5.1", "7.1" };
            var formatNames = new HashSet<string> { "hdr", "dv", "hdr10plus" };
            var resolutionNames = new HashSet<string> { "480p", "576p", "720p", "1080p", "4k" };
            var audioCodecNames = new HashSet<string> { "aac", "pcm", "flac", "mp3", "ac3", "eac3", "dts", "truehd" };
            var videoCodecNames = new HashSet<string> { "av1", "avc", "h264", "h265", "mp4", "vc1", "vp9", "h266" };
            var communityRatingNames = new HashSet<string> { "imdb" };

            foreach (var file in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || !SupportedIconExtensions.Contains(ext)) continue;

                var name = Path.GetFileNameWithoutExtension(file);
                var lowerName = name.ToLowerInvariant();

                ConcurrentDictionary<string, string> dict;
                if (lowerName.StartsWith("srt.")) dict = _subtitleIconPathCache;
                else if (channelNames.Contains(lowerName)) dict = _channelIconPathCache;
                else if (formatNames.Contains(lowerName)) dict = _videoFormatIconPathCache;
                else if (resolutionNames.Contains(lowerName)) dict = _resolutionIconPathCache;
                else if (audioCodecNames.Contains(lowerName)) dict = _audioCodecIconPathCache;
                else if (videoCodecNames.Contains(lowerName)) dict = _videoCodecIconPathCache;
                else if (communityRatingNames.Contains(lowerName)) dict = _communityRatingIconPathCache;
                else
                {
                    _audioIconPathCache.TryAdd(lowerName, file);
                    _tagIconPathCache.TryAdd(lowerName, file);
                    continue;
                }
                dict.TryAdd(lowerName, file);
            }

            _lastCacheRefreshTime = DateTime.UtcNow;

            string newVersion;
            if (force)
            {
                newVersion = Guid.NewGuid().ToString("N");
                _logger.Info("[EmbyIcons] Cache refresh was forced by user. Generating new cache-busting version.");
            }
            else
            {
                newVersion = ComputeIconFilesVersion(currentWriteTimes);
            }

            if (newVersion != _currentIconVersion)
            {
                _currentIconVersion = newVersion;
                CacheRefreshedWithVersion?.Invoke(this, _currentIconVersion);
                _logger.Info($"[EmbyIcons] Icon cache version updated to: {_currentIconVersion}. Posters will be refreshed as they are viewed.");
            }
            return Task.CompletedTask;
        }

        private string? FindIconPathOnDisk(string iconNameKey)
        {
            if (string.IsNullOrEmpty(_iconsFolder)) return null;

            foreach (var ext in SupportedIconExtensions)
            {
                var fullPath = Path.Combine(_iconsFolder, iconNameKey + ext);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            return null;
        }

        public async Task<SKImage?> GetCachedIconAsync(string iconNameKey, IconType iconType, CancellationToken cancellationToken)
        {
            if (_iconsFolder == null) return null;
            var processedKey = iconNameKey.ToLowerInvariant();
            var (pathCache, imageCache) = GetCachesForType(iconType);

            pathCache.TryGetValue(processedKey, out var iconPath);

            if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath))
            {
                string? foundPath = FindIconPathOnDisk(processedKey);
                if (!string.IsNullOrEmpty(foundPath))
                {
                    _logger.Debug($"[EmbyIcons] Self-healing: Found new path '{foundPath}' for icon key '{processedKey}'. Updating path cache.");
                    iconPath = foundPath;
                    pathCache[processedKey] = iconPath; 
                }
                else
                {
                    pathCache.TryRemove(processedKey, out _); 
                    return null; 
                }
            }

            if (imageCache.TryGetValue(iconPath, out var cached))
            {
                try
                {
                    var lastWriteTime = File.GetLastWriteTimeUtc(iconPath);
                    if (lastWriteTime == cached.FileWriteTimeUtc)
                    {
                        return cached.Image; 
                    }

                    _logger.Info($"[EmbyIcons] Icon file '{iconPath}' has been modified. Invalidating and reloading.");
                    if (imageCache.TryRemove(iconPath, out var oldCached)) oldCached.Image?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[EmbyIcons] Could not check file time for '{iconPath}', reloading. Error: {ex.Message}");
                    if (imageCache.TryRemove(iconPath, out var oldCached)) oldCached.Image?.Dispose();
                }
            }

            return await TryLoadAndCacheIconAsync(iconPath, imageCache, cancellationToken);
        }

        private async Task<SKImage?> TryLoadAndCacheIconAsync(string iconPath, ConcurrentDictionary<string, CachedIcon> imageCache, CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(iconPath) || new FileInfo(iconPath).Length < 50)
                {
                    if (!File.Exists(iconPath)) _logger.Warn($"[EmbyIcons] Icon file not found on attempt to load: '{iconPath}'.");
                    else _logger.Warn($"[EmbyIcons] Skipping small/corrupt icon: '{iconPath}'.");
                    return null;
                }

                using var memoryStream = new MemoryStream();
                await using (var fileStream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                {
                    await fileStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                }
                memoryStream.Position = 0;

                using var data = SKData.Create(memoryStream);
                var img = SKImage.FromEncodedData(data);

                if (img == null)
                {
                    _logger.Warn($"[EmbyIcons] Failed to decode icon file: '{iconPath}'. It may be corrupt or an unsupported format.");
                    return null;
                }

                var lastWrite = File.GetLastWriteTimeUtc(iconPath);
                imageCache[iconPath] = new CachedIcon(img, lastWrite);
                return img;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] A critical error occurred while loading icon '{iconPath}'.", ex);
                return null;
            }
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
            IconType.CommunityRating => (_communityRatingIconPathCache, _communityRatingIconImageCache),
            _ => throw new ArgumentOutOfRangeException(nameof(iconType))
        };

        private static string ComputeIconFilesVersion(Dictionary<string, DateTime> fileWriteTimes)
        {
            using var md5 = MD5.Create();
            var combined = string.Join("|", fileWriteTimes.OrderBy(f => f.Key).Select(f => $"{f.Key.ToLowerInvariant()}:{f.Value.Ticks}"));
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
            ClearImageCache(_communityRatingIconImageCache);
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