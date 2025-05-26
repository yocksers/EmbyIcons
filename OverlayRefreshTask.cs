using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public class OverlayRefreshTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;

        public OverlayRefreshTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public string Name => "EmbyIcons: Force Overlay Refresh";
        public string Description => "Forces overlays to be rebuilt for all items, identical to saving settings.";
        public string Category => "Library";
        public string Key => "EmbyIconsOverlayForceRefresh";
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() { yield break; }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                // PATCH: Increment hidden counter and save settings to force cache key change
                var options = plugin.GetConfiguredOptions();
                options.OverlayRefreshCounter++;
                plugin.SaveOptions(options); // Make sure this is public in your Plugin.cs

                plugin.Logger.Info($"[EmbyIcons] Forced overlay refresh by bumping OverlayRefreshCounter to {options.OverlayRefreshCounter}");

                // Now perform the icon cache refresh as usual (not strictly required, but keeps cache in sync)
                var enhancer = new EmbyIconsEnhancer(_libraryManager);
                try
                {
                    await enhancer.RefreshIconCacheAsync(cancellationToken, force: true);
                    plugin.Logger.Info("[EmbyIcons] Full overlay cache refresh triggered via scheduled task.");
                }
                catch (Exception ex)
                {
                    plugin.Logger.ErrorException("Error during overlay refresh from scheduled task", ex);
                }
                finally
                {
                    enhancer.Dispose();
                }
            }
        }
    }
}
