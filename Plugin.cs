using EmbyIcons.Helpers;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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
        public string ProcessedIconsFolder { get; private set; } = string.Empty;

        public EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager, _userViewManager, _logManager);

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
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetLogger(nameof(Plugin));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Instance = this;

            _logger.Debug("EmbyIcons plugin initialized.");
            ApplySettings(GetOptions());
            SubscribeLibraryEvents();
        }

        private void SubscribeLibraryEvents()
        {
            _libraryManager.ItemAdded += LibraryManagerOnItemChanged;
            _libraryManager.ItemRemoved += LibraryManagerOnItemChanged;
            _libraryManager.ItemUpdated += LibraryManagerOnItemChanged;
        }

        private void UnsubscribeLibraryEvents()
        {
            _libraryManager.ItemAdded -= LibraryManagerOnItemChanged;
            _libraryManager.ItemRemoved -= LibraryManagerOnItemChanged;
            _libraryManager.ItemUpdated -= LibraryManagerOnItemChanged;
        }

        private void LibraryManagerOnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            BaseItem? seriesToClear = null;
            switch (e.Item)
            {
                case Episode episode:
                    seriesToClear = episode.Series;
                    break;
                case Season season:
                    seriesToClear = season.Series;
                    break;
                case Series series:
                    seriesToClear = series;
                    break;
            }

            // Invalidate overlay cache for the relevant series
            _enhancer?.ClearSeriesOverlayCache(seriesToClear);
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            base.OnOptionsSaved(options);
            ApplySettings(options);

            if (options.RefreshIconCacheNow)
            {
                _logger.Info("[EmbyIcons] User requested an icon cache refresh via plugin settings.");
                Enhancer.RefreshIconCacheAsync(CancellationToken.None, force: true).ConfigureAwait(false);

                options.RefreshIconCacheNow = false;
                SaveOptions(options);
            }

            try
            {
                // FIX #4: Use the processed path for validation
                if (!_fileSystem.DirectoryExists(ProcessedIconsFolder))
                {
                    _logger.Error($"[EmbyIcons] Configured icons folder '{ProcessedIconsFolder}' does not exist. Overlays may not work.");
                }
                else if (!_fileSystem.GetFiles(ProcessedIconsFolder)
                    .Any(f =>
                    {
                        var ext = Path.GetExtension(f.FullName).ToLowerInvariant();
                        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp" || ext == ".gif" || ext == ".ico" || ext == ".svg" || ext == ".avif";
                    }))
                {
                    _logger.Warn($"[EmbyIcons] No common icon image files found in '{ProcessedIconsFolder}'. Overlays may not work. Supported formats: PNG, JPG, JPEG, WebP, BMP, GIF, ICO, SVG, AVIF.");
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
            // FIX #4: Apply settings to internal fields instead of modifying the options object
            ProcessedIconsFolder = Environment.ExpandEnvironmentVariables(options.IconsFolder);
            _allowedLibraryIds = FileUtils.GetAllowedLibraryIds(_libraryManager, options.SelectedLibraries);
            _logger.Debug($"[EmbyIcons] Loaded settings: IconsFolder={ProcessedIconsFolder}, IconSize={options.IconSize}, AllowedLibraries={options.SelectedLibraries}");
        }

        public override string Name => "EmbyIcons";
        public override string Description => "Overlays language, channel, video format, and resolution icons onto media posters.";
        public override Guid Id => new("b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f");
        public PluginOptions GetConfiguredOptions() => GetOptions();

        public Stream GetThumbImage()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = $"{GetType().Namespace}.Images.logo.png";
            return asm.GetManifestResourceStream(name) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public void Dispose()
        {
            UnsubscribeLibraryEvents();
            _enhancer?.Dispose();
            _enhancer = null;
            Instance = null;
            _logger.Debug("EmbyIcons plugin disposed.");
        }
    }
}