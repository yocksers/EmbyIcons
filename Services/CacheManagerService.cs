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

            var cacheRefreshTask = Task.Run(async () =>
            {
                try
                {
                    _logger.Info("[EmbyIcons] Starting background cache refresh.");
                    await _enhancer.ForceCacheRefreshAsync(iconsFolder, CancellationToken.None);
                    _logger.Info("[EmbyIcons] Background cache refresh completed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[EmbyIcons] Error during background cache refresh.", ex);
                }
            });
            
            _ = cacheRefreshTask.ContinueWith(t => 
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.ErrorException("[EmbyIcons] Unhandled exception in cache refresh background task.", t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);

            config.PersistedVersion = Guid.NewGuid().ToString("N");
            plugin.SaveCurrentConfiguration();
            _logger.Info($"[EmbyIcons] Cache clear requested. New cache-busting version is '{config.PersistedVersion}'. Cache refresh running in background.");
            return Task.CompletedTask;
        }
    }
}