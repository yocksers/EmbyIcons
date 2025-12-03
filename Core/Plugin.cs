using EmbyIcons.Api;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using EmbyIcons.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
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
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public class Plugin : BasePlugin<PluginOptions>, IHasWebPages, IHasThumbImage, IDisposable
    {
        private readonly IApplicationHost _appHost;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserViewManager _userViewManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;
        private EmbyIconsEnhancer? _enhancer;
        private Timer? _pruningTimer;
        private ProfileManagerService? _profileManager;
        private ConfigurationMonitor? _configMonitor;

        private bool _migrationPerformed = false;
        private static bool _migrationAttempted = false;
        private static readonly object _migrationLock = new object();


        public static Plugin? Instance { get; private set; }

        public ILogger Logger => _logger;

        public string ConfigurationVersion => Configuration.PersistedVersion;

        public EmbyIconsEnhancer Enhancer
        {
            get
            {
                if (_enhancer == null)
                {
                    _enhancer = new EmbyIconsEnhancer(_libraryManager, _logManager, _fileSystem);
                    EnsurePruningTimerInitialized();
                    _enhancer.EnsureTemplateCacheInitialized();
                }
                return _enhancer;
            }
        }

        private void EnsurePruningTimerInitialized()
        {
            if (_pruningTimer == null)
            {
                var pruningInterval = TimeSpan.FromHours(Math.Max(1, Configuration.CachePruningIntervalHours));
                _pruningTimer = new Timer(
                    _ => Enhancer.PruneSeriesAggregationCache(),
                    null,
                    TimeSpan.FromHours(1),
                    pruningInterval
                );
            }
        }

        private ProfileManagerService ProfileManager => _profileManager ??= new ProfileManagerService(_libraryManager, _logger, Configuration);
        private ConfigurationMonitor ConfigMonitor => _configMonitor ??= new ConfigurationMonitor(_logger, _libraryManager, _fileSystem);


        public Plugin(
            IApplicationHost appHost,
            IApplicationPaths appPaths,
            ILibraryManager libraryManager,
            IUserViewManager userViewManager,
            ILogManager logManager,
            IFileSystem fileSystem,
            IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            _appHost = appHost;
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userViewManager = userViewManager ?? throw new ArgumentNullException(nameof(userViewManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetLogger(nameof(Plugin));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Instance = this;

            _logger.Debug("EmbyIcons plugin initialized.");
            SubscribeLibraryEvents();
        }

        private void EnsureConfigurationMigrated()
        {
            lock (_migrationLock)
            {
                if (_migrationPerformed || _migrationAttempted) return;

                if (Configuration.Profiles != null && Configuration.Profiles.Any())
                {
                    _migrationPerformed = true;
                    return;
                }

                _migrationAttempted = true;
            }

            Task.Run(() =>
            {
                try
                {
                    _logger.Info("[EmbyIcons] No profiles found. Attempting to migrate old settings in the background.");

#pragma warning disable CS0612
                    var defaultProfileSettings = new ProfileSettings
                    {
                        ShowOverlaysForEpisodes = Configuration.ShowOverlaysForEpisodes,
                        ShowSeriesIconsIfAllEpisodesHaveLanguage = Configuration.ShowSeriesIconsIfAllEpisodesHaveLanguage,

                        AudioIconAlignment = Configuration.ShowAudioIcons ? Configuration.AudioIconAlignment : IconAlignment.Disabled,
                        SubtitleIconAlignment = Configuration.ShowSubtitleIcons ? Configuration.SubtitleIconAlignment : IconAlignment.Disabled,
                        ChannelIconAlignment = Configuration.ShowAudioChannelIcons ? Configuration.ChannelIconAlignment : IconAlignment.Disabled,
                        AudioCodecIconAlignment = Configuration.ShowAudioCodecIcons ? Configuration.AudioCodecIconAlignment : IconAlignment.Disabled,
                        VideoFormatIconAlignment = Configuration.ShowVideoFormatIcons ? Configuration.VideoFormatIconAlignment : IconAlignment.Disabled,
                        VideoCodecIconAlignment = Configuration.ShowVideoCodecIcons ? Configuration.VideoCodecIconAlignment : IconAlignment.Disabled,
                        TagIconAlignment = Configuration.ShowTagIcons ? Configuration.TagIconAlignment : IconAlignment.Disabled,
                        ResolutionIconAlignment = Configuration.ShowResolutionIcons ? Configuration.ResolutionIconAlignment : IconAlignment.Disabled,
                        CommunityScoreIconAlignment = Configuration.ShowCommunityScoreIcon ? Configuration.CommunityScoreIconAlignment : IconAlignment.Disabled,
                        AspectRatioIconAlignment = Configuration.ShowAspectRatioIcons ? Configuration.AspectRatioIconAlignment : IconAlignment.Disabled,

                        AudioOverlayHorizontal = Configuration.AudioOverlayHorizontal,
                        SubtitleOverlayHorizontal = Configuration.SubtitleOverlayHorizontal,
                        ChannelOverlayHorizontal = Configuration.ChannelOverlayHorizontal,
                        AudioCodecOverlayHorizontal = Configuration.AudioCodecOverlayHorizontal,
                        VideoFormatOverlayHorizontal = Configuration.VideoFormatOverlayHorizontal,
                        VideoCodecOverlayHorizontal = Configuration.VideoCodecOverlayHorizontal,
                        TagOverlayHorizontal = Configuration.TagOverlayHorizontal,
                        ResolutionOverlayHorizontal = Configuration.ResolutionOverlayHorizontal,
                        CommunityScoreOverlayHorizontal = Configuration.CommunityScoreOverlayHorizontal,
                        AspectRatioOverlayHorizontal = Configuration.AspectRatioOverlayHorizontal,
                        CommunityScoreBackgroundShape = Configuration.CommunityScoreBackgroundShape,
                        CommunityScoreBackgroundColor = Configuration.CommunityScoreBackgroundColor,
                        CommunityScoreBackgroundOpacity = Configuration.CommunityScoreBackgroundOpacity,
                        IconSize = Configuration.IconSize,
                        UseSeriesLiteMode = Configuration.UseSeriesLiteMode
                    };

                    var defaultProfile = new IconProfile
                    {
                        Name = "Default",
                        Id = Guid.NewGuid(),
                        Settings = defaultProfileSettings
                    };

                    if (Configuration.Profiles == null)
                    {
                        Configuration.Profiles = new List<IconProfile>();
                    }
                    Configuration.Profiles.Add(defaultProfile);

                    var oldSelectedLibsSet = new HashSet<string>((Configuration.SelectedLibraries ?? "").Split(','), StringComparer.OrdinalIgnoreCase);
                    oldSelectedLibsSet.RemoveWhere(string.IsNullOrWhiteSpace);

                    var virtualFolders = _libraryManager.GetVirtualFolders();
                    var libraryNameMap = virtualFolders
                        .GroupBy(lib => lib.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Id.ToString(), StringComparer.OrdinalIgnoreCase);

                    if (oldSelectedLibsSet.Any())
                    {
                        foreach (var libName in oldSelectedLibsSet)
                        {
                            if (libraryNameMap.TryGetValue(libName, out var libId))
                            {
                                if (Configuration.LibraryProfileMappings.All(m => m.LibraryId != libId))
                                {
                                    Configuration.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = libId, ProfileId = defaultProfile.Id });
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var lib in virtualFolders)
                        {
                            var libId = lib.Id.ToString();
                            if (Configuration.LibraryProfileMappings.All(m => m.LibraryId != libId))
                            {
                                Configuration.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = libId, ProfileId = defaultProfile.Id });
                            }
                        }
                    }
#pragma warning restore CS0612

                    _logger.Info($"[EmbyIcons] Background migration complete. Created 'Default' profile and assigned it to {Configuration.LibraryProfileMappings.Count} libraries.");
                    SaveConfiguration();

                    lock (_migrationLock)
                    {
                        _migrationPerformed = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[EmbyIcons] A critical error occurred during background configuration migration.", ex);
                }
                finally
                {
                    lock (_migrationLock)
                    {
                        _migrationAttempted = true;
                    }
                }
            });
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

        private void SubscribeLibraryEvents()
        {
            _libraryManager.ItemUpdated += LibraryManagerOnItemChanged;
            _libraryManager.ItemAdded += LibraryManagerOnItemChanged;
            _libraryManager.ItemRemoved += LibraryManagerOnItemChanged;
        }

        private void UnsubscribeLibraryEvents()
        {
            _libraryManager.ItemUpdated -= LibraryManagerOnItemChanged;
            _libraryManager.ItemAdded -= LibraryManagerOnItemChanged;
            _libraryManager.ItemRemoved -= LibraryManagerOnItemChanged;
        }

        private void LibraryManagerOnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e?.Item == null) return;
            
            try
            {
                var enhancer = Enhancer;
                
                if (e.Item is Folder && e.Parent == _libraryManager.RootFolder)
                {
                    ProfileManager.InvalidateLibraryCache();
                    return;
                }

                bool dateModifiedChanged = e.UpdateReason == ItemUpdateType.MetadataEdit ||
                                         e.UpdateReason == ItemUpdateType.MetadataImport ||
                                         e.UpdateReason == ItemUpdateType.None;

                enhancer.ClearEpisodeIconCache(e.Item.Id);

                var seriesToClear = (e.Item as Episode)?.Series
                                 ?? (e.Item as Season)?.Series
                                 ?? e.Item as Series;

                var seasonToClear = (e.Item as Episode)?.Season;

                if (dateModifiedChanged && e.Item is Episode episode && episode.Series != null)
                {
                    if (Configuration?.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] DateModified change detected for episode '{episode.Name}', clearing series and season caches.");
                }

                if (seasonToClear != null && seasonToClear.Id != Guid.Empty)
                {
                    if (Configuration?.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Change detected for '{e.Item.Name}'; clearing aggregation cache for season '{seasonToClear.Name}' ({seasonToClear.Id}).");
                    enhancer.ClearSeriesAggregationCache(seasonToClear.Id);
                }

                if (seriesToClear != null && seriesToClear.Id != Guid.Empty)
                {
                    if (Configuration?.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Change detected for '{e.Item.Name}'; clearing aggregation cache for parent series '{seriesToClear.Name}' ({seriesToClear.Id}).");
                    enhancer.ClearSeriesAggregationCache(seriesToClear.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error in LibraryManagerOnItemChanged event handler.", ex);
            }
        }

        public void SaveCurrentConfiguration() => SaveConfiguration();

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var newOptions = (PluginOptions)configuration;
            var oldOptions = JsonSerializer.Deserialize<PluginOptions>(JsonSerializer.Serialize(Configuration));

            _logger.Info("[EmbyIcons] Saving new configuration.");

            newOptions.PersistedVersion = Guid.NewGuid().ToString("N");
            base.UpdateConfiguration(newOptions);

            if (oldOptions != null)
            {
                ConfigMonitor.CheckForChangesAndTriggerRefreshes(oldOptions, newOptions);
                
                if (oldOptions.EnableIconTemplateCaching != newOptions.EnableIconTemplateCaching)
                {
                    _logger.Info($"[EmbyIcons] Template caching setting changed to: {newOptions.EnableIconTemplateCaching}");
                    Enhancer.EnsureTemplateCacheInitialized();
                }
            }

            Enhancer.ClearAllItemDataCaches();
            IconManagerService.InvalidateCache();
            ProfileManager.InvalidateLibraryCache();
            _profileManager = null;

            _logger.Info($"[EmbyIcons] Configuration saved. New cache-busting version is '{newOptions.PersistedVersion}'. Images will refresh as they are viewed.");
        }


        public override string Name => "EmbyIcons";
        public override string Description => "Overlays language, channel, video format, and resolution icons onto media posters.";
        public override Guid Id => new("b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f");

        public PluginOptions GetConfiguredOptions()
        {
            EnsureConfigurationMigrated();
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
            try
            {
                UnsubscribeLibraryEvents();
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[EmbyIcons] Error unsubscribing library events.", ex);
            }
            
            try { _pruningTimer?.Dispose(); } catch { }
            try { _enhancer?.Dispose(); } catch (Exception ex) 
            { 
                _logger?.ErrorException("[EmbyIcons] Error disposing enhancer.", ex);
            }
            _enhancer = null;
            
            try { _profileManager?.Dispose(); } catch { }
            _profileManager = null;
            _configMonitor = null;
            Instance = null;
            
            _logger?.Debug("EmbyIcons plugin disposed.");
        }
    }
}