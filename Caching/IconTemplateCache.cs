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
                TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }
        private string GenerateTemplateKey(
            List<(IconCacheManager.IconType Type, List<string> Names)> iconGroups,
            int iconSize,
            int interIconPadding,
            bool isHorizontal)
        {
            using var md5 = MD5.Create();
            var sb = new StringBuilder();
            
            sb.Append($"sz{iconSize}_pad{interIconPadding}_{(isHorizontal ? "h" : "v")}_");
            
            foreach (var group in iconGroups.OrderBy(g => g.Type))
            {
                sb.Append($"{group.Type}:");
                foreach (var name in group.Names.OrderBy(n => n))
                {
                    sb.Append(name).Append(',');
                }
                sb.Append('|');
            }

            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hash).Replace('/', '_').Replace('+', '-');
        }
        // IMPORTANT: This method now returns a NEW SKImage each time from cached byte data
        // The caller MUST dispose the returned SKImage when done
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
                    // Cache stores byte[] instead of SKImage to avoid memory leaks
                    if (cache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes != null)
                    {
                        Interlocked.Increment(ref _cacheHits);
                        if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            _logger.Debug($"[EmbyIcons] Template cache HIT for key: {cacheKey}");
                        
                        // Recreate SKImage from cached bytes - caller must dispose
                        try
                        {
                            using var ms = new MemoryStream(cachedBytes);
                            return SKImage.FromEncodedData(ms);
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
            
            await lockObj.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cache = _templateCache;
                if (cache == null || _disposed)
                    return null;
                
                // Double-check cache after acquiring lock (another thread might have generated it)
                try
                {
                    if (cache.TryGetValue(cacheKey, out byte[]? reCheckedBytes) && reCheckedBytes != null)
                    {
                        try
                        {
                            using var ms = new MemoryStream(reCheckedBytes);
                            return SKImage.FromEncodedData(ms);
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException("[EmbyIcons] Error recreating template from cache after lock", ex);
                            // Continue to generate new template
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
                        // Encode template to PNG bytes for caching to prevent memory leaks
                        using var encodedData = template.Encode(SKEncodedImageFormat.Png, 100);
                        if (encodedData != null)
                        {
                            var bytes = encodedData.ToArray();
                            var cacheOptions = new MemoryCacheEntryOptions()
                                .SetSize(bytes.LongLength)
                                .SetSlidingExpiration(TimeSpan.FromHours(2));

                            try
                            {
                                if (cache != null && !_disposed)
                                {
                                    cache.Set(cacheKey, bytes, cacheOptions);
                                    Interlocked.Increment(ref _templatesGenerated);
                                    
                                    if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                                        _logger.Debug($"[EmbyIcons] Generated and cached template: {cacheKey} ({bytes.Length} bytes)");
                                }
                            }
                            catch (ObjectDisposedException)
                            {
                                // Cache was disposed, just return the template
                            }
                        }
                    }
                    finally
                    {
                        // Don't dispose template here - return it to caller who must dispose it
                    }
                }

                return template;
            }
            finally
            {
                // Release lock but keep it in the dictionary for reuse
                // Locks will be cleaned up in Clear() and Dispose() methods
                try
                {
                    lockObj.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Lock was already disposed, try to remove it
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

                int width, height;
                if (isHorizontal)
                {
                    width = allIcons.Count * iconSize + (allIcons.Count - 1) * interIconPadding;
                    height = iconSize;
                }
                else
                {
                    width = iconSize;
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
                foreach (var icon in allIcons)
                {
                    var destRect = new SKRect(x, y, x + iconSize, y + iconSize);
                    canvas.DrawImage(icon, destRect, paint);

                    if (isHorizontal)
                        x += iconSize + interIconPadding;
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
                catch (Exception ex) 
                { 
                    _logger?.Debug($"[EmbyIcons] Error disposing lock during clear: {ex.Message}"); 
                }
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
