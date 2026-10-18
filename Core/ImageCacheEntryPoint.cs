using MediaBrowser.Controller.Plugins;

namespace EmbyIcons
{
    public sealed class ImageCacheEntryPoint : IServerEntryPoint
    {
        // Emby assigns configuration/data paths after constructing the plugin.
        public void Run() => Plugin.Instance?.InitializeImageCache();
        public void Dispose() { }
    }
}
