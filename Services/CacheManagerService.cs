using EmbyIcons.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Authenticated]
    [Route(ApiRoutes.RefreshCache, "POST", Summary = "Forces the icon cache to be cleared and refreshed")]
    public class RefreshCacheRequest : IReturnVoid
    {
    }

    public class CacheManagerService : IService
    {
        private readonly ILogger _logger;
        private readonly EmbyIconsEnhancer _enhancer;

        public CacheManagerService(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(CacheManagerService));
            _enhancer = Plugin.Instance?.Enhancer ?? throw new InvalidOperationException("Enhancer is not available.");
        }

        public Task Post(RefreshCacheRequest request)
        {
            _logger.Info("[EmbyIcons] Received request to clear all icon and data caches from the settings page.");
            IconManagerService.InvalidateCache();

            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                _logger.Warn("[EmbyIcons] Plugin instance not available.");
                return Task.CompletedTask;
            }

            var config = plugin.Configuration;
            config.ImageCacheVersion = Guid.NewGuid().ToString("N");
            plugin.SaveCurrentConfiguration();

            _enhancer.ForceCacheRefresh(config.IconsFolder);
            _logger.Info("[EmbyIcons] Caches cleared. All posters will be redrawn as they are viewed.");

            return Task.CompletedTask;
        }
    }
}