using MediaBrowser.Model.Services;
using MediaBrowser.Model.Logging;
using EmbyIcons.Api;
using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.MemoryUsage, "GET", Summary = "Returns memory usage statistics for the plugin and process")]
    public class MemoryUsageRequest : IReturn<MemoryUsageResult>
    {
    }

    public class MemoryUsageResult
    {
        public long ProcessWorkingSetBytes { get; set; }
        public long ProcessPrivateBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
        public long IconCacheEstimatedBytes { get; set; }
        public int SeriesAggregationCacheCount { get; set; }
        public int EpisodeCacheCount { get; set; }
        public int ItemLocksCount { get; set; }
        public string TimestampUtc { get; set; } = DateTime.UtcNow.ToString("o");
    }

    public class MemoryUsageService : IService
    {
        private readonly ILogger _logger;

        public MemoryUsageService(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(MemoryUsageService));
        }

        public Task<object> Get(MemoryUsageRequest request)
        {
            var proc = Process.GetCurrentProcess();

            long workingSet = proc.WorkingSet64;
            long privateBytes = 0;
            try
            {
                privateBytes = proc.PrivateMemorySize64;
            }
            catch (Exception ex)
            {
                _logger.Debug($"[EmbyIcons] Unable to read PrivateMemorySize64: {ex.Message}");
            }

            long managed = GC.GetTotalMemory(forceFullCollection: false);

            long iconCacheEstimate = 0;
            int seriesCacheCount = 0;
            int episodeCacheCount = 0;
            int itemLocksCount = 0;
            
            try
            {
                var plugin = EmbyIcons.Plugin.Instance;
                if (plugin != null)
                {
                    var enhancer = plugin.Enhancer;
                    
                    // Get series aggregation cache count
                    var seriesCacheField = typeof(EmbyIconsEnhancer).GetField("_seriesAggregationCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (seriesCacheField?.GetValue(null) is System.Collections.Concurrent.ConcurrentDictionary<Guid, object> seriesCache)
                    {
                        seriesCacheCount = seriesCache.Count;
                    }
                    
                    // Get episode cache count
                    var episodeCacheField = typeof(EmbyIconsEnhancer).GetField("_episodeIconCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (episodeCacheField?.GetValue(null) is Microsoft.Extensions.Caching.Memory.MemoryCache episodeCache)
                    {
                        episodeCacheCount = episodeCache.Count;
                    }
                    
                    // Get item locks count
                    var locksField = typeof(EmbyIconsEnhancer).GetField("_locks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (locksField?.GetValue(null) is System.Collections.Concurrent.ConcurrentDictionary<string, object> locks)
                    {
                        itemLocksCount = locks.Count;
                    }
                    
                    var field = enhancer.GetType().GetField("_iconCacheManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    var icm = field?.GetValue(enhancer);
                    if (icm != null)
                    {
                        var cacheField = icm.GetType().GetField("_iconImageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var cache = cacheField?.GetValue(icm) as Microsoft.Extensions.Caching.Memory.MemoryCache;
                        if (cache != null)
                        {
                            iconCacheEstimate = 0;
                            
                            if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                            {
                                _logger.Debug("[EmbyIcons] Icon cache memory estimation not yet implemented.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error while estimating icon cache size.", ex);
            }

            var result = new MemoryUsageResult
            {
                ProcessWorkingSetBytes = workingSet,
                ProcessPrivateBytes = privateBytes,
                ManagedHeapBytes = managed,
                IconCacheEstimatedBytes = iconCacheEstimate,
                SeriesAggregationCacheCount = seriesCacheCount,
                EpisodeCacheCount = episodeCacheCount,
                ItemLocksCount = itemLocksCount,
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };

            return Task.FromResult<object>(result);
        }
    }
}
