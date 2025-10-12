using EmbyIcons.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
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
            var iconsFolder = config.IconsFolder;

            // Run the potentially long-running cache refresh in the background so the HTTP request
            // isn't blocked by cache compaction and native image disposal work which can be slow.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _enhancer.ForceCacheRefreshAsync(iconsFolder, CancellationToken.None);
                    _logger.Info("[EmbyIcons] Background cache refresh completed.");
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[EmbyIcons] Error during background cache refresh.", ex);
                }
            });

            // Immediately bump the persisted version so clients will see the new cache-busting key.
            config.PersistedVersion = Guid.NewGuid().ToString("N");
            plugin.SaveCurrentConfiguration();
            _logger.Info($"[EmbyIcons] Cache clear requested. New cache-busting version is '{config.PersistedVersion}'. Cache refresh running in background.");
            return Task.CompletedTask;
        }
    }
}