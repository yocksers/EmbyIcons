using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Common;
using MediaBrowser.Controller.Library;

namespace EmbyIcons
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IServerEntryPoint, IImageEnhancer, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly EmbyIconsEnhancer _enhancer;

        public static Plugin Instance { get; private set; } = null!;

        public Plugin(IApplicationHost appHost, ILibraryManager libraryManager)
            : base(appHost)
        {
            Instance = this;
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _enhancer = new EmbyIconsEnhancer(_libraryManager);
        }

        public override string Name => "EmbyIcons";

        public override string Description => "Overlays language icons onto media posters.";

        public override Guid Id => new Guid("b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f");

        public void Run() { /* No background tasks needed */ }

        public void Dispose() { /* Cleanup if needed */ }

        public PluginOptions GetConfiguredOptions() => GetOptions();

        public MetadataProviderPriority Priority => _enhancer.Priority;

        public bool Supports(BaseItem item, ImageType imageType) =>
            _enhancer.Supports(item, imageType);

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType) =>
            _enhancer.GetConfigurationCacheKey(item, imageType);

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            _enhancer.GetEnhancedImageInfo(item, inputFile, imageType, imageIndex);

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            _enhancer.GetEnhancedImageSize(item, imageType, imageIndex, originalSize);

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            _enhancer.EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex);
    }
}