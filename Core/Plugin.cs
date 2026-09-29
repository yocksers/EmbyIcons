using EmbyIcons.Configuration;
using EmbyIcons.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
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
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Compat;

namespace EmbyIcons
{
    public class Plugin : BasePlugin<PluginOptions>, IHasWebPages, IHasThumbImage, IDisposable
    {
        private readonly IApplicationHost _appHost;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;
        private readonly Lazy<EmbyIconsEnhancer> _enhancerLazy;
        private Timer? _pruningTimer;
        private Timer? _libraryInvalidationDebounceTimer;
        private readonly object _libraryInvalidationDebounceLock = new object();
        private volatile Lazy<ProfileManagerService> _profileManagerLazy = null!;
        private CancellationTokenSource? _backgroundTasksCts;
        private MediaBrowser.Controller.Library.IUserDataManager? _userDataManager;

        private bool _migrationPerformed = false;
        private static bool _migrationAttempted = false;
        private static readonly object _migrationLock = new object();


        public static Plugin? Instance { get; private set; }

        public ILogger Logger => _logger;
        
        public IUserManager UserManager => _userManager;
        
        public IApplicationHost ApplicationHost => _appHost;

        public CancellationToken ShutdownToken
        {
            get
            {
                var cts = _backgroundTasksCts;
                return cts?.Token ?? CancellationToken.None;
            }
        }

        private readonly object _renderFingerprintLock = new object();
        private string? _globalRenderFingerprint;
        private string? _iconsFolderFingerprint;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _profileRenderFingerprints = new();
        private static readonly string PluginBuildVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

        public EmbyIconsEnhancer Enhancer => _enhancerLazy.Value;

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

        private ProfileManagerService ProfileManager => _profileManagerLazy.Value;


        public Plugin(
            IApplicationHost appHost,
            IApplicationPaths appPaths,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogManager logManager,
            IFileSystem fileSystem,
            IXmlSerializer xmlSerializer)
            : base(appPaths, xmlSerializer)
        {
            _appHost = appHost;
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetLogger(nameof(Plugin));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _backgroundTasksCts = new CancellationTokenSource();

            _profileManagerLazy = new Lazy<ProfileManagerService>(
                () => new ProfileManagerService(_libraryManager, _logger, Configuration),
                LazyThreadSafetyMode.ExecutionAndPublication);

            try
            {
                _userDataManager = _appHost.Resolve<MediaBrowser.Controller.Library.IUserDataManager>();
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EmbyIcons] Could not resolve IUserDataManager: {ex.Message}");
            }

            _enhancerLazy = new Lazy<EmbyIconsEnhancer>(() =>
            {
                var imageProcessor = _appHost.Resolve<MediaBrowser.Controller.Drawing.IImageProcessor>();
                var enhancer = new EmbyIconsEnhancer(_libraryManager, _logManager, _fileSystem, imageProcessor);
                EnsurePruningTimerInitialized();
                return enhancer;
            }, LazyThreadSafetyMode.ExecutionAndPublication);

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
                    _migrationAttempted = true;
                    return;
                }

                _migrationAttempted = true;
            }

            var migrationTask = Task.Run(() =>
            {
                var cancellationToken = _backgroundTasksCts?.Token ?? CancellationToken.None;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
                    cancellationToken.ThrowIfCancellationRequested();
                    SaveCurrentConfiguration();

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
            
            _ = migrationTask.ContinueWith(t => 
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    _logger.ErrorException("[EmbyIcons] Unhandled exception in migration background task.", t.Exception);
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public IconProfile? GetProfileForItem(BaseItem item)
        {
            return ProfileManager.GetProfileForItem(item);
        }

        public Task<IconProfile?> GetProfileForItemAsync(BaseItem item)
        {
            return ProfileManager.GetProfileForItemAsync(item);
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
                ,
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationUtils",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Utils.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationDom",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Dom.js"
                }
                ,
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationProfile",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Profile.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationScans",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Scans.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationApi",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Api.js"
                }
                ,
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationDomCache",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.DomCache.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationEvents",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Events.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationDataLoader",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.DataLoader.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationUIHandlers",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.UIHandlers.js"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationProfileUI",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.ProfileUI.js"
                }
                ,
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationSettings",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Settings.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationIconLayout",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.IconLayout.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationAdvanced",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Advanced.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationIconManager",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.IconManager.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationTroubleshooter",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Troubleshooter.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationReadme",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.Readme.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationAddProfileTemplate",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.AddProfileTemplate.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationRenameProfileTemplate",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.RenameProfileTemplate.html"
                },
                new PluginPageInfo
                {
                    Name = "EmbyIconsConfigurationSafeZones",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.EmbyIconsConfiguration.SafeZones.js"
                }
            };
        }

        private void SubscribeLibraryEvents()
        {
            _libraryManager.ItemUpdated += LibraryManagerOnItemChanged;
            _libraryManager.ItemAdded += LibraryManagerOnItemChanged;
            _libraryManager.ItemRemoved += LibraryManagerOnItemRemoved;
            
            if (_userDataManager != null)
            {
                _userDataManager.UserDataSaved += OnUserDataSaved;
            }
        }

        private void UnsubscribeLibraryEvents()
        {
            try { _libraryManager.ItemUpdated -= LibraryManagerOnItemChanged; } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error unsubscribing ItemUpdated: {ex.Message}"); }
            
            try { _libraryManager.ItemAdded -= LibraryManagerOnItemChanged; } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error unsubscribing ItemAdded: {ex.Message}"); }
            
            try { _libraryManager.ItemRemoved -= LibraryManagerOnItemRemoved; } 
            catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error unsubscribing ItemRemoved: {ex.Message}"); }
            
            if (_userDataManager != null)
            {
                try { _userDataManager.UserDataSaved -= OnUserDataSaved; }
                catch (Exception ex) { _logger?.Debug($"[EmbyIcons] Error unsubscribing UserDataSaved: {ex.Message}"); }
            }
        }

        private void OnUserDataSaved(object? sender, MediaBrowser.Controller.Library.UserDataSaveEventArgs e)
        {
            if (e?.Item == null) return;
            if (e.SaveReason != MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating) return;

            try
            {
                Enhancer.InvalidateFavoriteCount(e.Item.Id);

                if (Helpers.PluginHelper.IsDebugLoggingEnabled)
                    _logger?.Debug($"[EmbyIcons] Favorite state changed for '{e.Item.Name}'; cleared its cached favorite count.");
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[EmbyIcons] Error handling user data change: {ex.Message}");
            }
        }

        private void LibraryManagerOnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            LibraryManagerOnItemChanged(sender, e);

            if (e?.Item != null && e.Item.InternalId > 0)
            {
                EmbyIconsEnhancer.ForgetAggregatedParent(e.Item.InternalId);
            }
        }

        private void LibraryManagerOnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e?.Item == null) return;
            
            try
            {
                var enhancer = Enhancer;
                
                var rootFolder = _libraryManager.RootFolder;
                if (e.Item is Folder && rootFolder != null && e.Parent != null && e.Parent.Id == rootFolder.Id)
                {
                    lock (_libraryInvalidationDebounceLock)
                    {
                        _libraryInvalidationDebounceTimer?.Dispose();
                        _libraryInvalidationDebounceTimer = new Timer(_ => ProfileManager.InvalidateLibraryCache(), null, 2000, Timeout.Infinite);
                    }
                    return;
                }

                enhancer.ClearEpisodeIconCache(e.Item.Id);

                if (e.Item is Movie || e.Item is Episode || e.Item is Series)
                {
                    IconManagerService.InvalidateCache();
                }

                Guid seriesIdToClear = Guid.Empty;
                Guid seasonIdToClear = Guid.Empty;

                if (e.Item is Episode ep)
                {
                    if (EmbyIconsEnhancer.TryGetAggregatedParentId(ep.SeriesId, out var cachedSeriesId))
                    {
                        seriesIdToClear = cachedSeriesId;
                    }
                    seasonIdToClear = e.Parent?.Id ?? Guid.Empty;
                }
                else if (e.Item is Season seasonItem)
                {
                    seriesIdToClear = e.Parent?.Id ?? Guid.Empty;
                    seasonIdToClear = seasonItem.Id;
                }
                else if (e.Item is Series)
                {
                    seriesIdToClear = e.Item.Id;
                }
                else if (e.Item is Movie)
                {
                    enhancer.InvalidateMovieProviderPathCache(e.Item);
                }
                else if (e.Item is MediaBrowser.Controller.Entities.Audio.Audio ||
                         e.Item is MediaBrowser.Controller.Entities.Audio.MusicAlbum ||
                         e.Item is MediaBrowser.Controller.Entities.Audio.MusicArtist)
                {
                    ClearMusicAggregatesFor(enhancer, e.Item, e.Parent);
                }

                if (seasonIdToClear != Guid.Empty)
                {
                    if (Configuration?.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Change detected for '{e.Item.Name}'; clearing aggregation cache for season ID {seasonIdToClear}.");
                    enhancer.ClearSeriesAggregationCache(seasonIdToClear);
                }

                if (seriesIdToClear != Guid.Empty)
                {
                    if (Configuration?.EnableDebugLogging ?? false)
                        _logger.Debug($"[EmbyIcons] Change detected for '{e.Item.Name}'; clearing aggregation cache for series ID {seriesIdToClear}.");
                    enhancer.ClearSeriesAggregationCache(seriesIdToClear);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error in LibraryManagerOnItemChanged event handler.", ex);
            }
        }

        private void ClearMusicAggregatesFor(EmbyIconsEnhancer enhancer, BaseItem item, BaseItem? parent)
        {
            if (item is MediaBrowser.Controller.Entities.Audio.MusicAlbum || item is MediaBrowser.Controller.Entities.Audio.MusicArtist)
            {
                enhancer.ClearAlbumAggregationCache(item.Id);
            }

            var current = parent ?? item.Parent;
            for (int depth = 0; current != null && depth < 4; depth++)
            {
                if (current is MediaBrowser.Controller.Entities.Audio.MusicAlbum || current is MediaBrowser.Controller.Entities.Audio.MusicArtist)
                {
                    enhancer.ClearAlbumAggregationCache(current.Id);
                }

                current = current.Parent;
            }
        }

        public void SaveCurrentConfiguration()
        {
            SaveConfiguration();
            InvalidateRenderFingerprints();
        }

        internal string GetRenderFingerprint(IconProfile profile)
        {
            var global = GetGlobalRenderFingerprint();
            return _profileRenderFingerprints.GetOrAdd(profile.Id, _ => ShortHash(global + "|" + SimpleJson.Serialize(profile.Settings)));
        }

        internal void InvalidateRenderFingerprints()
        {
            lock (_renderFingerprintLock)
            {
                _globalRenderFingerprint = null;
                _profileRenderFingerprints.Clear();
            }
        }

        private string GetGlobalRenderFingerprint()
        {
            var cached = _globalRenderFingerprint;
            if (cached != null) return cached;

            string? iconsFolderToReload = null;
            string result;

            lock (_renderFingerprintLock)
            {
                if (_globalRenderFingerprint != null) return _globalRenderFingerprint;

                var config = Configuration;
                var folderFingerprint = ComputeIconsFolderFingerprint(config.IconsFolder);
                if (_iconsFolderFingerprint != null && !string.Equals(_iconsFolderFingerprint, folderFingerprint, StringComparison.Ordinal))
                {
                    iconsFolderToReload = config.IconsFolder ?? string.Empty;
                }
                _iconsFolderFingerprint = folderFingerprint;

                result = ShortHash(string.Join("|", new object?[]
                {
                    PluginBuildVersion,
                    config.ImageCacheVersion,
                    config.IconsFolder,
                    (int)config.IconLoadingMode,
                    (int)config.OutputFormat,
                    config.JpegQuality,
                    config.MaxRenderDimension,
                    config.EnableImageSmoothing,
                    config.EnableCollectionProfileLookup,
                    config.ForceDisableSkiaSharp,
                    !string.IsNullOrWhiteSpace(config.MDBListApiKey),
                    folderFingerprint
                }));
                _globalRenderFingerprint = result;
            }

            if (iconsFolderToReload != null)
            {
                _logger.Info("[EmbyIcons] Custom icons folder contents changed; reloading icons.");
                Enhancer.RefreshIconCaches(iconsFolderToReload);
            }

            return result;
        }

        private static string ComputeIconsFolderFingerprint(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "none";

            try
            {
                if (!Directory.Exists(folder)) return "missing";

                var sb = new System.Text.StringBuilder();
                foreach (var file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var info = new FileInfo(file);
                    sb.Append(info.Name).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
                }

                return ShortHash(sb.ToString());
            }
            catch
            {
                return "unreadable";
            }
        }

        private static string ShortHash(string value)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
            return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            var newOptions = (PluginOptions)configuration;

            _logger.Info("[EmbyIcons] Saving new configuration.");

            newOptions.ImageCacheVersion = Configuration.ImageCacheVersion;
            base.UpdateConfiguration(newOptions);
            InvalidateRenderFingerprints();

            Enhancer.ClearAllItemDataCaches();
            IconManagerService.InvalidateCache();
            var previousProfileManager = _profileManagerLazy;
            _profileManagerLazy = new Lazy<ProfileManagerService>(
                () => new ProfileManagerService(_libraryManager, _logger, newOptions),
                LazyThreadSafetyMode.ExecutionAndPublication);

            if (previousProfileManager != null && previousProfileManager.IsValueCreated)
            {
                var retired = previousProfileManager.Value;
                _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                {
                    try { retired.Dispose(); }
                    catch (Exception ex) { _logger.Debug($"[EmbyIcons] Error disposing previous profile manager: {ex.Message}"); }
                }, TaskScheduler.Default);
            }

            _logger.Info("[EmbyIcons] Configuration saved. Posters affected by the changes will be redrawn as they are viewed.");
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

        private static void CleanupStaticResources(ILogger? logger)
        {
            try
            {
                EmbyIconsEnhancer.CleanupStaticResources(logger);
            }
            catch (Exception ex)
            {
                logger?.Debug($"[EmbyIcons] Error cleaning up enhancer static resources: {ex.Message}");
            }
            
            try
            {
                Helpers.FontHelper.Dispose();
            }
            catch (Exception ex)
            {
                logger?.Debug($"[EmbyIcons] Error disposing FontHelper: {ex.Message}");
            }
            
            try
            {

                Services.MDBListService.Dispose();
            }
            catch (Exception ex)
            {
                logger?.Debug($"[EmbyIcons] Error disposing MDBList service: {ex.Message}");
            }
        }

        public void Dispose()
        {
            var cts = Interlocked.Exchange(ref _backgroundTasksCts, null);
            try
            {
                cts?.Cancel();
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[EmbyIcons] Error cancelling background tasks: {ex.Message}");
            }
            
            try
            {
                UnsubscribeLibraryEvents();
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[EmbyIcons] Error unsubscribing library events.", ex);
            }
            
            try { _pruningTimer?.Dispose(); } 
            catch (Exception ex) 
            { 
                _logger?.Debug($"[EmbyIcons] Error disposing pruning timer: {ex.Message}");
            }
            
            try { _libraryInvalidationDebounceTimer?.Dispose(); }
            catch (Exception ex)
            {
                _logger?.Debug($"[EmbyIcons] Error disposing debounce timer: {ex.Message}");
            }
            
            if (_enhancerLazy.IsValueCreated)
            {
                try { _enhancerLazy.Value?.Dispose(); } 
                catch (Exception ex) 
                { 
                    _logger?.ErrorException("[EmbyIcons] Error disposing enhancer.", ex);
                }
            }
            
            if (_profileManagerLazy.IsValueCreated)
            {
                try { _profileManagerLazy.Value?.Dispose(); } 
                catch (Exception ex) 
                { 
                    _logger?.Debug($"[EmbyIcons] Error disposing profile manager: {ex.Message}");
                }
            }
            
            try { cts?.Dispose(); }
            catch (Exception ex)
            {
                _logger?.Debug($"[EmbyIcons] Error disposing background tasks CTS: {ex.Message}");
            }
            
            CleanupStaticResources(_logger);
            
            Instance = null;
            
            _logger?.Debug("EmbyIcons plugin disposed.");
        }
    }
}