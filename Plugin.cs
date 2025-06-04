using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserViewManager _userViewManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private EmbyIconsEnhancer? _enhancer;

        public static Plugin? Instance { get; private set; }

        public ILogger Logger => _logger;

        private HashSet<string> _allowedLibraryIds = new();
        public HashSet<string> AllowedLibraryIds => _allowedLibraryIds;

        // FIX: Pass the ILogger instance obtained from ILogManager to Enhancer
        public EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager, _userViewManager, _logManager);

        public Plugin(
            IApplicationHost appHost,
            ILibraryManager libraryManager,
            IUserViewManager userViewManager,
            ILogManager logManager, // Inject ILogManager
            IFileSystem fileSystem)
            : base(appHost)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new ArgumentNullException(nameof(userViewManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager)); // Store ILogManager
            _logger = logManager.GetLogger(nameof(Plugin)); // Get logger for Plugin class
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Instance = this;

            _logger.Info("EmbyIcons plugin initialized.");
            ApplySettings(GetOptions());
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            base.OnOptionsSaved(options);
            ApplySettings(options);

            try
            {
                var expandedPath = Environment.ExpandEnvironmentVariables(options.IconsFolder);
                if (!_fileSystem.DirectoryExists(expandedPath))
                {
                    _logger.Error($"[EmbyIcons] Configured icons folder '{expandedPath}' does not exist. Overlays may not work.");
                }
                // CHANGED: Accept all supported image formats, not just PNG
                else if (!_fileSystem.GetFiles(expandedPath)
                    .Any(f =>
                    {
                        var ext = Path.GetExtension(f.FullName).ToLowerInvariant();
                        // Update the list if you add/remove supported types
                        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp" || ext == ".gif" || ext == ".ico" || ext == ".svg" || ext == ".avif";
                    }))
                {
                    _logger.Warn($"[EmbyIcons] No icon image files (.png, .jpg, .jpeg, .webp, .bmp, .gif, .ico, .svg, .avif) found in '{expandedPath}'. Overlays may not work.");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error validating icons folder on save", ex);
            }
        }

        public new void SaveOptions(PluginOptions options)
        {
            base.SaveOptions(options);
        }

        public void ApplySettings(PluginOptions options)
        {
            options.IconsFolder = Environment.ExpandEnvironmentVariables(options.IconsFolder);

            _allowedLibraryIds = Helpers.FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);

            _logger.Info($"[EmbyIcons] Loaded settings: IconsFolder={options.IconsFolder}, " +
                         $"IconSize={options.IconSize}, AllowedLibraries={options.SelectedLibraries}");
        }

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

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType,
                                              int imageIndex, ImageSize originalSize) =>
            Enhancer.GetEnhancedImageSize(item, imageType, imageIndex, originalSize);

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile,
                                      ImageType imageType, int imageIndex) =>
            Enhancer.EnhanceImageAsync(item, inputFile, outputFile, imageType, CancellationToken.None); // Removed imageIndex

        public Stream GetThumbImage()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = $"{GetType().Namespace}.Images.logo.png";
            return asm.GetManifestResourceStream(name) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public void Dispose()
        {
            _logger.Debug("EmbyIcons plugin disposed.");
            // No need to dispose _enhancer here, it's handled by the application lifecycle or via IDisposable on Enhancer itself.
        }
    }
}