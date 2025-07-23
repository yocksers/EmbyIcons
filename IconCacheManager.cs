using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly ConcurrentDictionary<string, CachedIcon> _iconImageCache = new();

        private string? _iconsFolder;
        private DateTime _lastCacheRefreshTime = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _iconFileLastWriteTimes = new();
        private string _currentIconVersion = string.Empty;
        public event EventHandler<string>? CacheRefreshedWithVersion;
        private static readonly Random _random = new();

        internal static readonly string[] SupportedIconExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

        public enum IconType { Language, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag, CommunityRating, AspectRatio }

        public IconCacheManager(TimeSpan cacheTtl, ILogger logger)
        {
            _cacheTtl = cacheTtl;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Dictionary<IconType, List<string>> GetAllAvailableIconKeys(string iconsFolder)
        {
            if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug($"[EmbyIcons] Scanning icons folder for available icon keys: '{iconsFolder}'");

            var allKeys = new Dictionary<IconType, List<string>>();
            foreach (IconType type in Enum.GetValues(typeof(IconType)))
            {
                allKeys[type] = new List<string>();
            }

            if (string.IsNullOrEmpty(iconsFolder) || !Directory.Exists(iconsFolder))
            {
                _logger.Warn($"[EmbyIcons] Icon keys scan cannot proceed, folder '{iconsFolder}' is not valid.");
                return allKeys;
            }

            string[] allFiles;
            try
            {
                allFiles = Directory.GetFiles(iconsFolder);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Failed to read files from '{iconsFolder}' during key scan.", ex);
                return allKeys;
            }

            var prefixLookup = Constants.PrefixMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var file in allFiles)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || !SupportedIconExtensions.Contains(ext)) continue;

                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split(new[] { '.' }, 2);

                if (parts.Length == 2 && prefixLookup.TryGetValue(parts[0], out var iconType))
                {
                    allKeys[iconType].Add(parts[1].ToLowerInvariant());
                }
            }

            if (allKeys.ContainsKey(IconType.Resolution))
            {
                allKeys[IconType.Resolution] = allKeys[IconType.Resolution].OrderByDescending(x => x.Length).ToList();
            }

            return allKeys;
        }

        public Task InitializeAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            if (_iconsFolder != iconsFolder)
            {
                _iconsFolder = iconsFolder;
                return RefreshCacheOnDemandAsync(iconsFolder, cancellationToken, force: true);
            }
            return Task.CompletedTask;
        }

        public Task RefreshCacheOnDemandAsync(string iconsFolder, CancellationToken cancellationToken, bool force = false)
        {
            _iconsFolder = iconsFolder;
            _logger.Info("[EmbyIcons] Clearing all cached icon image data and forcing poster refresh.");

            ClearImageCache(_iconImageCache);

            _lastCacheRefreshTime = DateTime.UtcNow;

            var newVersion = Guid.NewGuid().ToString("N");
            CacheRefreshedWithVersion?.Invoke(this, newVersion);
            _logger.Info($"[EmbyIcons] Icon cache version updated to: {newVersion}. Posters will be refreshed as they are viewed.");

            return Task.CompletedTask;
        }

        public async Task<SKImage?> GetCachedIconAsync(string iconNameKey, IconType iconType, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_iconsFolder) || string.IsNullOrEmpty(iconNameKey))
            {
                return null;
            }

            var prefix = Constants.PrefixMap[iconType];
            var baseFileName = $"{prefix}.{iconNameKey.ToLowerInvariant()}";

            if (_iconImageCache.TryGetValue(baseFileName, out var cached))
            {
                try
                {
                    var lastWriteTime = File.GetLastWriteTimeUtc(cached.FullPath);
                    if (lastWriteTime == cached.FileWriteTimeUtc)
                    {
                        return cached.Image;
                    }

                    _logger.Info($"[EmbyIcons] Icon file '{cached.FullPath}' has been modified. Invalidating and reloading.");
                    if (_iconImageCache.TryRemove(baseFileName, out var oldCached)) oldCached.Image?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"[EmbyIcons] Could not check file time for '{cached.FullPath}', reloading. Error: {ex.Message}");
                    if (_iconImageCache.TryRemove(baseFileName, out var oldCached)) oldCached.Image?.Dispose();
                }
            }

            foreach (var ext in SupportedIconExtensions)
            {
                var fullPath = Path.Combine(_iconsFolder, baseFileName + ext);
                if (File.Exists(fullPath))
                {
                    return await TryLoadAndCacheIconAsync(fullPath, baseFileName, cancellationToken);
                }
            }

            return null;
        }

        private async Task<SKImage?> TryLoadAndCacheIconAsync(string iconPath, string cacheKey, CancellationToken cancellationToken)
        {
            try
            {
                if (new FileInfo(iconPath).Length < 50)
                {
                    _logger.Warn($"[EmbyIcons] Skipping small/corrupt icon: '{iconPath}'.");
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
                _iconImageCache[cacheKey] = new CachedIcon(img, lastWrite, iconPath);
                return img;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] A critical error occurred while loading icon '{iconPath}'.", ex);
                return null;
            }
        }

        public void Dispose()
        {
            ClearImageCache(_iconImageCache);
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
            public string FullPath { get; }

            public CachedIcon(SKImage? image, DateTime fileWriteTimeUtc, string fullPath)
            {
                Image = image;
                FileWriteTimeUtc = fileWriteTimeUtc;
                FullPath = fullPath;
            }
        }
    }
}