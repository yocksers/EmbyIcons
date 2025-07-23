using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    [Route("/EmbyIcons/RefreshCache", "POST", Summary = "Forces the icon cache to be cleared and refreshed")]
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

        public async Task Post(RefreshCacheRequest request)
        {
            _logger.Info("[EmbyIcons] Received request to clear all icon and data caches from the settings page.");

            var iconsFolder = Plugin.Instance?.Configuration.IconsFolder;
            if (string.IsNullOrEmpty(iconsFolder))
            {
                _logger.Warn("[EmbyIcons] Cannot refresh cache as icons folder is not configured.");
                return;
            }

            await _enhancer.ForceCacheRefreshAsync(iconsFolder, CancellationToken.None);
        }
    }
}