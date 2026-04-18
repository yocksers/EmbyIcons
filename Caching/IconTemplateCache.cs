using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Configuration;
using EmbyIcons.Models;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace EmbyIcons.Caching
{
    public class IconTemplateCache : IDisposable
    {
        private readonly ILogger _logger;
        private volatile MemoryCache? _templateCache;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _generationLocks = new();
        private Timer? _maintenanceTimer;
        private readonly object _cacheInstanceLock = new();
        private volatile bool _disposed = false;

        private long _cacheHits = 0;
        private long _cacheMisses = 0;
        private long _templatesGenerated = 0;

        public IconTemplateCache(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var cacheSizeLimit = 50 * 1024 * 1024;
            _templateCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = cacheSizeLimit
            });

            _maintenanceTimer = new Timer(_ => PerformMaintenance(), null, 
                TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }
        private readonly ThreadLocal<MD5> _md5Pool = new ThreadLocal<MD5>(MD5.Create);

        private string GenerateTemplateKey(
            List<(IconCacheManager.IconType Type, List<string> Names)> iconGroups,
            int iconSize,
            int interIconPadding,
            bool isHorizontal)
        {
            var md5 = _md5Pool.Value!;
            md5.Initialize();
            var encoding = Encoding.UTF8;
            
            void HashString(string str)
            {
                var bytes = encoding.GetBytes(str);
                md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }

            HashString($"sz{iconSize}_pad{interIconPadding}_{(isHorizontal ? "h" : "v")}_");
            
            foreach (var group in iconGroups.OrderBy(g => g.Type))
            {
                HashString($"{group.Type}:");
                foreach (var name in group.Names.OrderBy(n => n))
                {
                    HashString(name);
                    HashString(",");
                }
                HashString("|");
            }

            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToBase64String(md5.Hash!).Replace('/', '_').Replace('+', '-');
        }
        public async Task<SKImage?> GetOrCreateTemplateAsync(
            List<(IconCacheManager.IconType Type, List<string> Names)> iconGroups,
            Dictionary<IconCacheManager.IconType, List<SKImage>> loadedIcons,
            int iconSize,
            int interIconPadding,
            bool isHorizontal,
            CancellationToken cancellationToken)
        {
            if (iconGroups == null || iconGroups.Count == 0 || loadedIcons == null)
                return null;

            if (_disposed)
                return null;

            var cacheKey = GenerateTemplateKey(iconGroups, iconSize, interIconPadding, isHorizontal);

            var cache = _templateCache;
            if (cache != null)
            {
                try
                {
                    if (cache.TryGetValue(cacheKey, out SKBitmap? cachedBitmap) && cachedBitmap != null)
                    {
                        Interlocked.Increment(ref _cacheHits);
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _logger.Debug($"[EmbyIcons] Template cache HIT for key: {cacheKey}");
                        
                        try
                        {
                            return SKImage.FromBitmap(cachedBitmap);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException("[EmbyIcons] Error recreating template from cache", ex);
                            return null;
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            }

            Interlocked.Increment(ref _cacheMisses);

            var lockObj = _generationLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            
            bool lockAcquired = false;
            try
            {
                await lockObj.WaitAsync(cancellationToken).ConfigureAwait(false);
                lockAcquired = true;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            try
            {
                cache = _templateCache;
                if (cache == null || _disposed)
                    return null;
                
                try
                {
                    if (cache.TryGetValue(cacheKey, out SKBitmap? reCheckedBitmap) && reCheckedBitmap != null)
                    {
                        try
                        {
                            return SKImage.FromBitmap(reCheckedBitmap);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException("[EmbyIcons] Error recreating template from cache after lock", ex);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }

                var template = GenerateTemplate(iconGroups, loadedIcons, iconSize, interIconPadding, isHorizontal);
                
                if (template != null)
                {
                    try
                    {
                        var bitmap = SKBitmap.FromImage(template);
                        if (bitmap != null)
                        {
                            long pixelSize = (long)bitmap.Width * bitmap.Height * 4;
                            var cacheOptions = new MemoryCacheEntryOptions()
                                .SetSize(pixelSize)
                                .SetSlidingExpiration(TimeSpan.FromHours(2))
                                .RegisterPostEvictionCallback((_, v, _, _) =>
                                {
                                    if (v is SKBitmap evicted) try { evicted.Dispose(); } catch { }
                                });

                            try
                            {
                                if (cache != null && !_disposed)
                                {
                                    cache.Set(cacheKey, bitmap, cacheOptions);
                                    Interlocked.Increment(ref _templatesGenerated);

                                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                                        _logger.Debug($"[EmbyIcons] Generated and cached template: {cacheKey} ({pixelSize} bytes)");
                                }
                                else
                                {
                                    bitmap.Dispose();
                                }
                            }
                            catch
                            {
                                bitmap.Dispose();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[EmbyIcons] Error caching template bitmap: {ex.Message}");
                    }
                }

                return template;
            }
            finally
            {
                if (lockAcquired)
                {
                    try
                    {
                        lockObj.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        _generationLocks.TryRemove(cacheKey, out _);
                    }
                    catch (SemaphoreFullException ex)
                    {
                        _logger?.Debug($"[EmbyIcons] SemaphoreFullException releasing template lock: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[EmbyIcons] Error releasing template generation lock: {ex.Message}");
                    }
                }
            }
        }
        private SKImage? GenerateTemplate(
            List<(IconCacheManager.IconType Type, List<string> Names)> iconGroups,
            Dictionary<IconCacheManager.IconType, List<SKImage>> loadedIcons,
            int iconSize,
            int interIconPadding,
            bool isHorizontal)
        {
            try
            {
                var allIcons = new List<SKImage>();
                foreach (var group in iconGroups)
                {
                    if (loadedIcons.TryGetValue(group.Type, out var icons))
                    {
                        allIcons.AddRange(icons);
                    }
                }

                if (allIcons.Count == 0)
                    return null;

                var iconWidths = allIcons.Select(icon =>
                    icon.Height > 0 ? (int)Math.Round((double)icon.Width / icon.Height * iconSize) : iconSize
                ).ToList();

                int width, height;
                if (isHorizontal)
                {
                    width = iconWidths.Sum() + (allIcons.Count - 1) * interIconPadding;
                    height = iconSize;
                }
                else
                {
                    width = iconWidths.Max();
                    height = allIcons.Count * iconSize + (allIcons.Count - 1) * interIconPadding;
                }

                using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);

                var enableSmoothing = Plugin.Instance?.Configuration.EnableImageSmoothing ?? false;
                using var paint = new SKPaint
                {
                    IsAntialias = enableSmoothing,
                    FilterQuality = enableSmoothing ? SKFilterQuality.Medium : SKFilterQuality.None
                };

                int x = 0, y = 0;
                for (int i = 0; i < allIcons.Count; i++)
                {
                    var icon = allIcons[i];
                    var iw = iconWidths[i];
                    var destRect = new SKRect(x, y, x + iw, y + iconSize);
                    canvas.DrawImage(icon, destRect, paint);

                    if (isHorizontal)
                        x += iw + interIconPadding;
                    else
                        y += iconSize + interIconPadding;
                }

                return surface.Snapshot();
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error generating icon template", ex);
                return null;
            }
        }
        public void Clear()
        {
            if (_disposed) return;

            lock (_cacheInstanceLock)
            {
                var oldCache = _templateCache;
                _templateCache = new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = 50 * 1024 * 1024
                });
                
                try
                {
                    oldCache?.Compact(1.0);
                    oldCache?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[EmbyIcons] Error disposing old template cache", ex);
                }
            }
            
            var locks = _generationLocks.Values.ToArray();
            _generationLocks.Clear();
            foreach (var lockObj in locks)
            {
                try { lockObj.Dispose(); } 
                catch { }
            }
            
            _cacheHits = 0;
            _cacheMisses = 0;
            _templatesGenerated = 0;
            
            _logger?.Info("[EmbyIcons] Icon template cache cleared");
        }
        public TemplateCacheStats GetStats()
        {
            var hits = Interlocked.Read(ref _cacheHits);
            var misses = Interlocked.Read(ref _cacheMisses);
            var total = hits + misses;
            var hitRate = total > 0 ? (double)hits / total * 100 : 0;

            return new TemplateCacheStats
            {
                CacheHits = hits,
                CacheMisses = misses,
                TotalRequests = total,
                HitRate = hitRate,
                TemplatesGenerated = Interlocked.Read(ref _templatesGenerated)
            };
        }

        private void PerformMaintenance()
        {
            try
            {
                _templateCache?.Compact(0.2);

                var staleLocks = _generationLocks
                    .Where(kvp => kvp.Value.CurrentCount > 0)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in staleLocks)
                {
                    if (_generationLocks.TryRemove(key, out var sem))
                    {
                        try { sem.Dispose(); } catch { }
                    }
                }
                
                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                {
                    var stats = GetStats();
                    _logger.Debug($"[EmbyIcons] Template cache stats - Hits: {stats.CacheHits}, Misses: {stats.CacheMisses}, Hit Rate: {stats.HitRate:F2}%");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error during template cache maintenance", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _maintenanceTimer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[EmbyIcons] Error disposing template cache maintenance timer: {ex.Message}");
            }

            lock (_cacheInstanceLock)
            {
                try
                {
                    _templateCache?.Compact(1.0);
                    _templateCache?.Dispose();
                    _templateCache = null;
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[EmbyIcons] Error disposing template cache: {ex.Message}");
                }
            }

            var locks = _generationLocks.Values.ToArray();
            _generationLocks.Clear();
            foreach (var lockObj in locks)
            {
                try { lockObj.Dispose(); } 
                catch (Exception ex) 
                { 
                    _logger?.Debug($"[EmbyIcons] Error disposing generation lock: {ex.Message}"); 
                }
            }

            try { _md5Pool?.Dispose(); }
            catch (Exception ex)
            {
                _logger?.Debug($"[EmbyIcons] Error disposing MD5 pool: {ex.Message}");
            }
        }
    }

    public class TemplateCacheStats
    {
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public long TotalRequests { get; set; }
        public double HitRate { get; set; }
        public long TemplatesGenerated { get; set; }
    }
}
