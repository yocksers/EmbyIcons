using MediaBrowser.Controller.Plugins;

namespace EmbyIcons
{
    public sealed class ImageCacheEntryPoint : IServerEntryPoint
    {
        // Emby assigns configuration/data paths after constructing the plugin.
        public void Run() => Plugin.Instance?.InitializeImageCache();
        // Emby disposes entry points on shutdown; plugins are not guaranteed to be disposed.
        public void Dispose() => Plugin.Instance?.ImageState?.Flush();
    }
}
