using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;

namespace EmbyIcons
{
    // Implement IHasThumbImage to provide embedded logo to Emby UI
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
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

        protected override void OnOptionsSaved(PluginOptions options)
        {
            base.OnOptionsSaved(options);

            if (_enhancer == null)
            {
                _enhancer = new EmbyIconsEnhancer(_libraryManager);
            }

            try
            {
                var task = _enhancer.RefreshIconCacheAsync(CancellationToken.None);
                bool completed = task.Wait(TimeSpan.FromSeconds(10));

                if (!completed)
                {
                    Helpers.LoggingHelper.Log(true, "EmbyIcons: Warning - icon cache refresh timed out on options save.");
                }
                else
                {
                    Helpers.LoggingHelper.Log(true, "EmbyIcons: Icon cache refreshed successfully on options save.");
                }

                // Note: Clearing Emby image cache per item is not available in public API.
                // Consider instructing users to refresh metadata manually if needed.
            }
            catch (Exception ex)
            {
                Helpers.LoggingHelper.Log(true, "EmbyIcons: Error refreshing icon cache on options save: " + ex.Message);
            }

            _enhancer.Dispose();
            _enhancer = null;
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

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile,
                                                      ImageType imageType, int imageIndex) =>
            Enhancer.GetEnhancedImageInfo(item, inputFile, imageType, imageIndex);

        public ImageSize GetEnhancedImageSize(BaseItem item,
                                              ImageType imageType, int imageIndex,
                                              ImageSize originalSize) =>
            Enhancer.GetEnhancedImageSize(item, imageType, imageIndex, originalSize);

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                      ImageType imageType, int imageIndex) =>
            Enhancer.EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        // IHasThumbImage implementation to provide embedded logo as plugin icon
        public Stream GetThumbImage()
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Adjust namespace and path if logo.png location changes
            string resourceName = $"{GetType().Namespace}.Images.logo.png";

            return assembly.GetManifestResourceStream(resourceName) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}