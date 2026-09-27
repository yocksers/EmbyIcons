using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Logging;
using EmbyIcons.Api;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Authenticated]
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
            long workingSet;
            long privateBytes = 0;
            using (var proc = Process.GetCurrentProcess())
            {
                workingSet = proc.WorkingSet64;
                try
                {
                    privateBytes = proc.PrivateMemorySize64;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[EmbyIcons] Unable to read PrivateMemorySize64: {ex.Message}");
                }
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
                    seriesCacheCount = EmbyIconsEnhancer._seriesAggregationCache.Count;
                    episodeCacheCount = EmbyIconsEnhancer._episodeIconCache?.Count ?? 0;
                    itemLocksCount = EmbyIconsEnhancer.ItemLockCount;
                    iconCacheEstimate = plugin.Enhancer.IconCacheEstimatedBytes;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error while collecting plugin cache statistics.", ex);
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
