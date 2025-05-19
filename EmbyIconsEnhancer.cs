using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;  // Correct namespace for ImageType
using MediaBrowser.Model.Drawing;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        private readonly ILibraryManager _libraryManager;
        private readonly IconCacheManager _iconCacheManager;

        public EmbyIconsEnhancer(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _iconCacheManager = new IconCacheManager(TimeSpan.FromMinutes(30));
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null || imageType != ImageType.Primary) return false;
            if (item is Person) return false;

            return item is Movie || item is Episode || item is Series ||
                   item is Season || item is BoxSet || item is MusicVideo;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var options = Plugin.Instance!.GetConfiguredOptions();
            var libsKey = (options.SelectedLibraries ?? "").Replace(',', '-').Replace(" ", "");
            return $"embyicons_{item.Id}_{imageType}_scale{options.IconSize}_libs{libsKey}";
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
           => new() { RequiresTransparency = true };

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize)
           => originalSize;

        Task IImageEnhancer.EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                              ImageType imageType, int imageIndex)
           => EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                            ImageType imageType, int imageIndex,
                                            CancellationToken cancellationToken)
        {
            var sem = _locks.GetOrAdd(item.Id.ToString(), _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(cancellationToken);

            try
            {
                await EnhanceImageInternalAsync(item, inputFile, outputFile, imageType, imageIndex, cancellationToken);
            }
            finally
            {
                sem.Release();
            }
        }

        public void Dispose()
        {
            _iconCacheManager.Dispose();

            foreach (var sem in _locks.Values)
                sem.Dispose();

            _locks.Clear();
        }
    }
}