using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace EmbyIcons.Helpers
{
    public class IconCacheManager : IDisposable
    {
        private FileSystemWatcher? _iconFolderWatcher;

        private readonly TimeSpan _cacheTtl;

        private readonly ConcurrentDictionary<string, string?> _audioIconPathCache = new();
        private readonly ConcurrentDictionary<string, string?> _subtitleIconPathCache = new();

        private readonly ConcurrentDictionary<string, CachedIcon> _audioIconImageCache = new();
        private readonly ConcurrentDictionary<string, CachedIcon> _subtitleIconImageCache = new();

        private string? _iconsFolder;
        private DateTime _lastCacheRefreshTime = DateTime.MinValue;

        private readonly Dictionary<string, string> _languageFallbackMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"eng", "en"},
            {"fre", "fr"},
            {"ger", "de"},
            {"jpn", "jp"}
            // Add more as needed
        };

        public IconCacheManager(TimeSpan cacheTtl)
        {
            _cacheTtl = cacheTtl;
        }

        public async Task InitializeAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            if (_iconsFolder != iconsFolder)
            {
                _iconsFolder = iconsFolder;
                await RefreshCacheAsync(cancellationToken);
                SetupWatcher();
            }
            else if ((DateTime.UtcNow - _lastCacheRefreshTime) > _cacheTtl)
            {
                await RefreshCacheAsync(cancellationToken);
            }
        }

        private async Task RefreshCacheAsync(CancellationToken cancellationToken)
        {
            if (_iconsFolder == null || !Directory.Exists(_iconsFolder))
                return;

            var pngFiles = Directory.GetFiles(_iconsFolder, "*.png");

            _audioIconPathCache.Clear();
            _subtitleIconPathCache.Clear();

            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);

            await Task.Run(() =>
            {
                foreach (var file in pngFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                    if (name.StartsWith("srt."))
                        _subtitleIconPathCache[name] = file;
                    else
                        _audioIconPathCache[name] = file;
                }

                Parallel.ForEach(_audioIconPathCache.Values.Where(f => f != null)!, new ParallelOptions { CancellationToken = cancellationToken }, path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        TryLoadAndCacheIcon(path!, false);
                });

                Parallel.ForEach(_subtitleIconPathCache.Values.Where(f => f != null)!, new ParallelOptions { CancellationToken = cancellationToken }, path =>
                {
                    if (!string.IsNullOrEmpty(path))
                        TryLoadAndCacheIcon(path!, true);
                });
            }, cancellationToken);

            _lastCacheRefreshTime = DateTime.UtcNow;
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
                CacheIcon(iconPath, img, isSubtitle);
            }
            catch
            {
                CacheIcon(iconPath, null, isSubtitle);
            }
        }

        private void CacheIcon(string iconPath, SKImage? image, bool isSubtitle)
        {
            var cacheEntry = new CachedIcon(image);

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

            if (imageCache.TryGetValue(iconPath, out var cached))
            {
                if (cached.IsExpired(_cacheTtl))
                    TryLoadAndCacheIcon(iconPath, isSubtitle);

                return cached.Image;
            }

            TryLoadAndCacheIcon(iconPath, isSubtitle);
            return imageCache.TryGetValue(iconPath, out var loaded) ? loaded.Image : null;
        }

        private string? ResolveIconPathWithFallback(string langCodeKey, string folderPath,
                                                   ConcurrentDictionary<string, string?> cache)
        {
            langCodeKey = langCodeKey.ToLowerInvariant();

            if (cache.TryGetValue(langCodeKey, out var path) && !string.IsNullOrEmpty(path))
                return path;

            if (_languageFallbackMap.TryGetValue(langCodeKey, out var fallback))
            {
                if (cache.TryGetValue(fallback, out var fbPath) && !string.IsNullOrEmpty(fbPath))
                    return fbPath;

                var fbCandidate = Path.Combine(folderPath, $"{fallback}.png");
                if (File.Exists(fbCandidate))
                {
                    cache[fallback] = fbCandidate;
                    return fbCandidate;
                }
            }

            var candidate = Path.Combine(folderPath, $"{langCodeKey}.png");
            if (File.Exists(candidate))
            {
                cache[langCodeKey] = candidate;
                return candidate;
            }

            cache[langCodeKey] = null!;
            return null;
        }

        private void SetupWatcher()
        {
            try
            {
                if (_iconFolderWatcher != null)
                {
                    _iconFolderWatcher.EnableRaisingEvents = false;
                    _iconFolderWatcher.Dispose();
                    _iconFolderWatcher = null;
                }

                if (_iconsFolder == null || !Directory.Exists(_iconsFolder))
                    return;

                _iconFolderWatcher = new FileSystemWatcher(_iconsFolder, "*.png")
                {
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false,
                    NotifyFilter =
                        NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
                };

                _iconFolderWatcher.Changed += OnFolderChanged;
                _iconFolderWatcher.Created += OnFolderChanged;
                _iconFolderWatcher.Deleted += OnFolderChanged;
                _iconFolderWatcher.Renamed += OnFolderChanged;
            }
            catch
            {
                // Optionally handle watcher setup failure
            }
        }

        private DateTime _lastEventTime = DateTime.MinValue;
        private static readonly TimeSpan _eventDebounceTime = TimeSpan.FromSeconds(1);

        private void OnFolderChanged(object sender, FileSystemEventArgs e)
        {
            var now = DateTime.UtcNow;
            if (now - _lastEventTime < _eventDebounceTime)
                return;

            _lastEventTime = now;

#pragma warning disable CS4014 // fire-and-forget
            RefreshCacheAsync(CancellationToken.None);
#pragma warning restore CS4014
        }

        public void Dispose()
        {
            ClearImageCache(_audioIconImageCache);
            ClearImageCache(_subtitleIconImageCache);

            if (_iconFolderWatcher != null)
            {
                _iconFolderWatcher.EnableRaisingEvents = false;
                _iconFolderWatcher.Dispose();
                _iconFolderWatcher = null;
            }
        }

        private sealed class CachedIcon
        {
            public SKImage? Image { get; }
            private readonly DateTime _loadTimeUtc;

            public CachedIcon(SKImage? image)
            {
                Image = image;
                _loadTimeUtc = DateTime.UtcNow;
            }

            public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - _loadTimeUtc > ttl;
        }
    }
}