﻿﻿using EmbyIcons.Helpers;
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

        private static List<(string Path, string Name, string Id)>? _libraryPathCache;
        private static readonly object _libraryPathCacheLock = new object();
        private bool _migrationPerformed = false;

        public static Plugin? Instance { get; private set; }

        public ILogger Logger => _logger;

        public string ConfigurationVersion => Configuration.PersistedVersion;

        public EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager, _userViewManager, _logManager);

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

            _pruningTimer = new Timer(
                _ => Enhancer.PruneSeriesAggregationCache(),
                null,
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(6)
            );
        }

        private void EnsureConfigurationMigrated()
        {
            if (_migrationPerformed) return;

#pragma warning disable CS0612 
            if (Configuration.Profiles == null || !Configuration.Profiles.Any())
            {
                _logger.Info("[EmbyIcons] No profiles found. Attempting to migrate old settings.");

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
                    JpegQuality = Configuration.JpegQuality,
                    EnableImageSmoothing = Configuration.EnableImageSmoothing,
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

                var oldSelectedLibs = new HashSet<string>((Configuration.SelectedLibraries ?? "").Split(','), StringComparer.OrdinalIgnoreCase);

                PopulateLibraryPathCache();
                if (_libraryPathCache == null)
                {
                    _logger.Error("[EmbyIcons] Library cache is null after population. Aborting migration.");
                    return;
                }

                if (oldSelectedLibs.Any(s => !string.IsNullOrWhiteSpace(s)))
                {
                    foreach (var lib in _libraryPathCache)
                    {
                        if (oldSelectedLibs.Contains(lib.Name.Trim().ToLowerInvariant()))
                        {
                            if (Configuration.LibraryProfileMappings.All(m => m.LibraryId != lib.Id))
                            {
                                Configuration.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = lib.Id, ProfileId = defaultProfile.Id });
                            }
                        }
                    }
                }
                else
                {
                    foreach (var lib in _libraryPathCache)
                    {
                        if (Configuration.LibraryProfileMappings.All(m => m.LibraryId != lib.Id))
                        {
                            Configuration.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = lib.Id, ProfileId = defaultProfile.Id });
                        }
                    }
                }

                _logger.Info($"[EmbyIcons] Migration complete. Created 'Default' profile and assigned it to {Configuration.LibraryProfileMappings.Count} libraries.");
                SaveConfiguration();
            }
#pragma warning restore CS0612
            _migrationPerformed = true;
        }

        private void PopulateLibraryPathCache()
        {
            _logger.Debug("[EmbyIcons] Populating library path cache.");
            var newCache = new List<(string Path, string Name, string Id)>();
            try
            {
                foreach (var lib in _libraryManager.GetVirtualFolders())
                {
                    if (lib?.Locations == null) continue;
                    foreach (var loc in lib.Locations)
                    {
                        if (!string.IsNullOrEmpty(loc))
                        {
                            newCache.Add((loc, lib.Name, lib.Id.ToString()));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] CRITICAL: Failed to get virtual folders from LibraryManager.", ex);
            }

            lock (_libraryPathCacheLock)
            {
                _libraryPathCache = newCache.OrderByDescending(i => i.Path.Length).ToList();
            }
        }

        private IconProfile? GetProfileForPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_libraryPathCacheLock)
            {
                if (_libraryPathCache == null)
                {
                    PopulateLibraryPathCache();
                }
            }

            var currentLibraryCache = _libraryPathCache;
            if (currentLibraryCache == null)
            {
                _logger.Warn("[EmbyIcons] Library path cache is null, cannot check library restrictions.");
                return null;
            }

            foreach (var libInfo in currentLibraryCache)
            {
                if (path.StartsWith(libInfo.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var mapping = Configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == libInfo.Id);
                    if (mapping != null)
                    {
                        return Configuration.Profiles?.FirstOrDefault(p => p.Id == mapping.ProfileId);
                    }
                    return null;
                }
            }

            return null;
        }

        public IconProfile? GetProfileForItem(BaseItem item)
        {
            if (item is BoxSet boxSet)
            {
                var firstChild = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    CollectionIds = new[] { boxSet.InternalId },
                    Limit = 1,
                    Recursive = true,
                    IncludeItemTypes = new[] { "Movie", "Episode" }
                }).FirstOrDefault();

                if (firstChild != null)
                {
                    return GetProfileForPath(firstChild.Path);
                }

                _logger.Warn($"[EmbyIcons] Collection '{boxSet.Name}' (ID: {boxSet.Id}) is empty. Cannot determine library profile for icon processing.");
                return null;
            }

            return GetProfileForPath(item.Path);
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
            var enhancer = Enhancer;

            try
            {
                if (e.Item is Folder && e.Parent == _libraryManager.RootFolder)
                {
                    _logger.Info("[EmbyIcons] A library folder has changed. Clearing the library path cache.");
                    lock (_libraryPathCacheLock)
                    {
                        _libraryPathCache = null;
                    }
                    return;
                }

                bool dateModifiedChanged = e.UpdateReason == ItemUpdateType.MetadataEdit ||
                                         e.UpdateReason == ItemUpdateType.MetadataImport ||
                                         e.UpdateReason == ItemUpdateType.None;

                enhancer.ClearEpisodeIconCache(e.Item.Id);

                var seriesToClear = (e.Item as Episode)?.Series
                                 ?? (e.Item as Season)?.Series
                                 ?? e.Item as Series;

                if (dateModifiedChanged && e.Item is Episode episode && episode.Series != null)
                {
                    _logger.Debug($"[EmbyIcons] DateModified change detected for episode '{episode.Name}', clearing series cache.");
                }

                if (seriesToClear != null && seriesToClear.Id != Guid.Empty)
                {
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
            var options = (PluginOptions)configuration;
            _logger.Info("[EmbyIcons] Saving new configuration.");

            options.PersistedVersion = Guid.NewGuid().ToString("N");

            base.UpdateConfiguration(options);

            IconManagerService.InvalidateCache();

            _logger.Info($"[EmbyIcons] Configuration saved. New cache-busting version is '{options.PersistedVersion}'. Images will refresh as they are viewed.");

            try
            {
                var expandedPath = Environment.ExpandEnvironmentVariables(options.IconsFolder);
                if (options.IconLoadingMode != IconLoadingMode.BuiltInOnly && !_fileSystem.DirectoryExists(expandedPath))
                {
                    _logger.Error($"[EmbyIcons] Configured icons folder '{expandedPath}' does not exist. Overlays may not work.");
                }
                else if (options.IconLoadingMode != IconLoadingMode.BuiltInOnly && !_fileSystem.GetFiles(expandedPath)
                    .Any(f =>
                    {
                        var ext = Path.GetExtension(f.FullName).ToLowerInvariant();
                        return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".webp" || ext == ".bmp" || ext == ".gif" || ext == ".ico" || ext == ".svg" || ext == ".avif";
                    }))
                {
                    _logger.Warn($"[EmbyIcons] No common icon image files found in '{expandedPath}'. Overlays may not work unless you are using fallback mode.");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error validating icons folder on save", ex);
            }
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
            UnsubscribeLibraryEvents();
            _pruningTimer?.Dispose();
            _enhancer?.Dispose();
            _enhancer = null;
            Instance = null;
            _logger.Debug("EmbyIcons plugin disposed.");
        }
    }
}