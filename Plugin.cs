using EmbyIcons.Api;
using EmbyIcons.Helpers;
using EmbyIcons.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public class Plugin : BasePlugin<PluginOptions>, IHasWebPages, IHasThumbImage, IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private EmbyIconsEnhancer? _enhancer;
        private ProfileManagerService? _profileManager;
        private ConfigurationMonitor? _configMonitor;
        private OverlayDataService? _overlayDataService;

        public static Plugin? Instance { get; private set; }

        public ILogger Logger => _logger;

        public string ConfigurationVersion => Configuration.PersistedVersion;

        public EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager, _logManager, _fileSystem);
        private ProfileManagerService ProfileManager => _profileManager ??= new ProfileManagerService(_libraryManager, _logger, Configuration);
        private ConfigurationMonitor ConfigMonitor => _configMonitor ??= new ConfigurationMonitor(_logger, _libraryManager, _fileSystem);

        private OverlayDataService OverlayDataService => _overlayDataService ??= new OverlayDataService(Enhancer);

        public Plugin(
            IApplicationPaths appPaths,
            ILibraryManager libraryManager,
            ILogManager logManager,
            IFileSystem fileSystem,
            IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetLogger(nameof(Plugin));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Instance = this;

            _libraryManager.ItemUpdated += LibraryManagerOnItemChanged;
        }

        public IconProfile? GetProfileForItem(BaseItem item)
        {
            return ProfileManager.GetProfileForItem(item);
        }

        public bool IsLibraryAllowed(BaseItem item)
        {
            return GetProfileForItem(item) != null;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfiguration",
                    EmbeddedResourcePath = GetType().Namespace + ".EmbyIconsConfiguration.html",
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationjs",
                    EmbeddedResourcePath = GetType().Namespace + ".EmbyIconsConfiguration.js"
                }
            };
        }

        public void SaveCurrentConfiguration() => SaveConfiguration();

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var newOptions = (PluginOptions)configuration;

            var oldOptions = JsonSerializer.Deserialize<PluginOptions>(JsonSerializer.Serialize(Configuration));

            if (oldOptions != null)
            {
                ConfigMonitor.CheckForChangesAndTriggerRefreshes(oldOptions, newOptions);
            }

            _logger.Info("[EmbyIcons] Saving new configuration.");

            newOptions.PersistedVersion = Guid.NewGuid().ToString("N");

            base.UpdateConfiguration(newOptions);

            IconManagerService.InvalidateCache();
            ProfileManager.InvalidateLibraryCache();
            _profileManager = null;

            _logger.Info($"[EmbyIcons] Configuration saved. New cache-busting version is '{newOptions.PersistedVersion}'. Images will refresh as they are viewed.");
        }

        private void LibraryManagerOnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Folder && e.Parent == _libraryManager.RootFolder)
            {
                ProfileManager.InvalidateLibraryCache();
                return;
            }

            var enhancer = Enhancer;

            var seriesToUpdate = (e.Item as Episode)?.Series
                              ?? (e.Item as Season)?.Series
                              ?? e.Item as Series;

            if (seriesToUpdate != null && seriesToUpdate.Id != Guid.Empty)
            {
                enhancer.InvalidateAggregationCache(seriesToUpdate.Id);
                _logger.Debug($"[EmbyIcons] Change detected for '{e.Item.Name}'; queueing background re-aggregation for parent series '{seriesToUpdate.Name}' ({seriesToUpdate.Id}).");
                enhancer.QueueItemUpdate(seriesToUpdate);
            }

            if (e.Item is Episode || e.Item is Movie)
            {
                if (e.Item.Id != Guid.Empty)
                {
                    _logger.Debug($"[EmbyIcons] Change detected for '{e.Item.Name}'; invalidating its data caches.");
                    OverlayDataService.InvalidateCacheForItem(e.Item.Id);
                    enhancer.ClearEpisodeIconCache(e.Item.Id);
                }
            }
        }

        public override string Name => "EmbyIcons";
        public override string Description => "Overlays language, channel, video format, and resolution icons onto media posters.";
        public override Guid Id => new("b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f");

        public PluginOptions GetConfiguredOptions()
        {
            return Configuration;
        }

        public Stream GetThumbImage()
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = $"{GetType().Namespace}.Images.logo.png";
            return asm.GetManifestResourceStream(name) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public void Dispose()
        {
            _libraryManager.ItemUpdated -= LibraryManagerOnItemChanged;

            _enhancer?.Dispose();
            _enhancer = null;
            _overlayDataService = null;
            Instance = null;
            _logger.Debug("EmbyIcons plugin disposed.");
        }
    }
}