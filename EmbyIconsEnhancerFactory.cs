using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace EmbyIcons
{
    public class EmbyIconsEnhancerFactory : IImageEnhancer
    {
        private EmbyIconsEnhancer CreateEnhancer()
        {
            var libraryManager = Plugin.Instance?.LibraryManager;
            var options = Plugin.Instance?.GetConfiguredOptions();
            if (libraryManager == null || options == null)
                throw new InvalidOperationException("Enhancer factory cannot resolve dependencies.");
            return new EmbyIconsEnhancer(libraryManager, options);
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem item, ImageType imageType) =>
            CreateEnhancer().Supports(item, imageType);

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType) =>
            CreateEnhancer().GetConfigurationCacheKey(item, imageType);

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            CreateEnhancer().GetEnhancedImageInfo(item, inputFile, imageType, imageIndex);

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) =>
            CreateEnhancer().GetEnhancedImageSize(item, imageType, imageIndex, originalSize);

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            CreateEnhancer().EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex);
    }
}