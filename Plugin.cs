using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;

namespace EmbyIcons
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>
    {
        private readonly ILibraryManager _libraryManager;

        private EmbyIconsEnhancer? _enhancer;

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationHost appHost, ILibraryManager libraryManager)
            : base(appHost)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            Instance = this;
        }

        private EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager);

        public override string Name => "EmbyIcons";

        public override string Description => "Overlays language icons onto media posters.";

        public override Guid Id => new("b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f");

        public PluginOptions GetConfiguredOptions() => GetOptions();

        public MetadataProviderPriority Priority => Enhancer.Priority;

        public bool Supports(BaseItem item, ImageType imageType) =>
            Enhancer.Supports(item, imageType);

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType) =>
            Enhancer.GetConfigurationCacheKey(item, imageType);

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            Enhancer.GetEnhancedImageInfo(item, inputFile, imageType, imageIndex);

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            Enhancer.GetEnhancedImageSize(item, imageType, imageIndex, originalSize);

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            Enhancer.EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);
    }
}