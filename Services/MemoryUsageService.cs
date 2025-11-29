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
            catch { }

            long managed = GC.GetTotalMemory(forceFullCollection: false);

            long iconCacheEstimate = 0;
            try
            {
                var icmType = typeof(EmbyIcons.EmbyIconsEnhancer).Assembly.GetType("EmbyIcons.Helpers.IconCacheManager");
                var plugin = EmbyIcons.Plugin.Instance;
                if (plugin != null)
                {
                    var enhancer = plugin.Enhancer;
                    var field = enhancer.GetType().GetField("_iconCacheManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    var icm = field?.GetValue(enhancer);
                    if (icm != null)
                    {
                        var cacheField = icm.GetType().GetField("_iconImageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var cache = cacheField?.GetValue(icm) as Microsoft.Extensions.Caching.Memory.MemoryCache;
                        if (cache != null)
                        {
                            try
                            {
                                iconCacheEstimate = 0;
                            }
                            catch { }
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
                TimestampUtc = DateTime.UtcNow.ToString("o")
            };

            return Task.FromResult<object>(result);
        }
    }
}
