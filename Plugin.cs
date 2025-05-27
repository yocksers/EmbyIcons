using EmbyIcons.Helpers;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Collections; // Required for IUserViewManager
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic; // Needed for HashSet
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
        private EmbyIconsEnhancer? _enhancer;

        public static Plugin? Instance { get; private set; }

        public ILogger Logger => _logger;

        // === Library overlay restriction cache ===
        private HashSet<string> _allowedLibraryIds = new();
        public HashSet<string> AllowedLibraryIds => _allowedLibraryIds;
        // ==========================================

        public EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager, _userViewManager);

        public HashSet<string> AudioLangSet { get; private set; } = new();
        public HashSet<string> SubtitleLangSet { get; private set; } = new();

        public Plugin(
            IApplicationHost appHost,
            ILibraryManager libraryManager,
            IUserViewManager userViewManager,
            ILogManager logManager,
            IFileSystem fileSystem)
            : base(appHost)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new ArgumentNullException(nameof(userViewManager));
            _logger = logManager.GetLogger(nameof(Plugin));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Instance = this;

            _logger.Info("EmbyIcons plugin initialized.");
            ApplySettings(GetOptions());
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            base.OnOptionsSaved(options);
            ApplySettings(options);
        }

        public new void SaveOptions(PluginOptions options)
        {
            base.SaveOptions(options);
        }

        public void ApplySettings(PluginOptions options)
        {
            AudioLangSet = new((options.AudioLanguages ?? "").Split(',').Select(x => x.Trim().ToLowerInvariant()));
            SubtitleLangSet = new((options.SubtitleLanguages ?? "").Split(',').Select(x => x.Trim().ToLowerInvariant()));

            // === PATCHED: Cache allowed libraries set on settings change ===
            _allowedLibraryIds = Helpers.FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);
            // ===============================================================

            _logger.Info($"[EmbyIcons] Loaded settings: IconsFolder={options.IconsFolder}, " +
                         $"IconSize={options.IconSize}, AudioLanguages={options.AudioLanguages}, " +
                         $"SubtitleLanguages={options.SubtitleLanguages}, AllowedLibraries={options.SelectedLibraries}");
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
            Enhancer.EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public Stream GetThumbImage()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = $"{GetType().Namespace}.Images.logo.png";
            return asm.GetManifestResourceStream(name) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public void Dispose()
        {
            _logger.Info("EmbyIcons plugin disposed.");
        }
    }
}
