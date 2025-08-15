using EmbyIcons.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System;

namespace EmbyIcons.Services
{
    [Route(ApiRoutes.MemoryReport, "GET", Summary = "Gets an estimate of the memory used by the plugin's caches")]
    public class GetMemoryReport : IReturn<MemoryReportResponse> { }

    public class MemoryReportResponse
    {
        public long IconCacheSize { get; set; }
        public long AggregationCacheSize { get; set; }
        public long ItemDataCacheSize { get; set; }
        public long TotalEstimatedSize { get; set; }
    }

    public class MemoryUsageService : IService
    {
        private readonly EmbyIconsEnhancer _enhancer;

        public MemoryUsageService()
        {
            _enhancer = Plugin.Instance?.Enhancer ?? throw new InvalidOperationException("Enhancer is not available.");
        }

        public object Get(GetMemoryReport request)
        {
            var iconCacheSize = _enhancer._iconCacheManager.GetCacheMemoryUsage();
            var (aggCacheSize, itemDataCacheSize, episodeCacheSize) = _enhancer.GetCacheMemoryUsage();
            var combinedItemCache = itemDataCacheSize + episodeCacheSize;

            return new MemoryReportResponse
            {
                IconCacheSize = iconCacheSize,
                AggregationCacheSize = aggCacheSize,
                ItemDataCacheSize = combinedItemCache,
                TotalEstimatedSize = iconCacheSize + aggCacheSize + combinedItemCache
            };
        }
    }
}