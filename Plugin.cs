using EmbyIcons.Helpers;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
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
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private EmbyIconsEnhancer? _enhancer;

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingRefreshes = new();

        public static Plugin? Instance { get; private set; }
        public ILogger Logger => _logger; // Make logger public for OverlayRefreshTask
        public EmbyIconsEnhancer Enhancer => _enhancer ??= new EmbyIconsEnhancer(_libraryManager); // Public for OverlayRefreshTask

        public Plugin(
            IApplicationHost appHost,
            ILibraryManager libraryManager,
            ILogManager logManager,
            IFileSystem fileSystem)
            : base(appHost)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(Plugin));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            Instance = this;

            _libraryManager.ItemUpdated += LibraryItemChanged;
            _libraryManager.ItemAdded += LibraryItemChanged;
            _logger.Info("EmbyIcons plugin initialized and event handlers registered.");

            ApplySettings(GetOptions());
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            base.OnOptionsSaved(options);
            ApplySettings(options);

            _enhancer ??= new EmbyIconsEnhancer(_libraryManager);

            try
            {
                var task = _enhancer.RefreshIconCacheAsync(CancellationToken.None);
                task.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error refreshing icon cache", ex);
            }
            finally
            {
                _enhancer?.Dispose();
                _enhancer = null;
            }
        }

        // PATCH: Make public so OverlayRefreshTask can save options and force overlay refresh
        public new void SaveOptions(PluginOptions options)
        {
            base.SaveOptions(options);
        }

        // PATCH: Make public so the scheduled task can call this if needed
        public void ApplySettings(PluginOptions options)
        {
            _logger.Info($"[EmbyIcons] Loaded settings: IconsFolder={options.IconsFolder}, " +
                         $"IconSize={options.IconSize}, AudioLanguages={options.AudioLanguages}, " +
                         $"SubtitleLanguages={options.SubtitleLanguages}");
        }

        private static readonly string[] OverlayRelevantProperties = new[] {
            "Path", "MediaStreams", "SubtitleFiles", "MediaSources", "VideoFiles", "AudioFiles"
        };

        private void LibraryItemChanged(object? sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null) return;

                if (!(item is Movie || item is Episode || item is Series || item is Season || item is MusicVideo))
                    return;

                var changedProps = GetChangedProperties(e);
                if (changedProps != null && changedProps.Length > 0)
                {
                    if (!AnyRelevantChange(changedProps))
                    {
                        _logger.Debug($"[EmbyIcons] Ignoring item update for {item.Name} ({item.Id}): only non-overlay properties changed.");
                        return;
                    }
                }

                QueueOverlayRefresh(item);

                if (item is Season season && season.Series != null)
                {
                    QueueOverlayRefresh(season.Series);
                }
                else if (item is Episode ep)
                {
                    if (ep.Season != null)
                        QueueOverlayRefresh(ep.Season);
                    if (ep.Series != null)
                        QueueOverlayRefresh(ep.Series);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Exception in LibraryItemChanged handler.", ex);
            }
        }

        private string[]? GetChangedProperties(ItemChangeEventArgs e)
        {
            var prop = e.GetType().GetProperty("ChangedProperties");
            return prop?.GetValue(e) as string[];
        }

        private bool AnyRelevantChange(string[] changed)
        {
            return changed.Any(prop => OverlayRelevantProperties.Contains(prop));
        }

        private void QueueOverlayRefresh(BaseItem item)
        {
            var key = item.Id.ToString();

            if (_pendingRefreshes.TryRemove(key, out var prevCts))
            {
                prevCts.Cancel();
                prevCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _pendingRefreshes[key] = cts;

            _ = DelayedOverlayRefreshAsync(item, cts.Token)
                .ContinueWith(_ =>
                {
                    if (_pendingRefreshes.TryRemove(key, out var finishedCts))
                        finishedCts.Dispose();
                });
        }

        private async Task DelayedOverlayRefreshAsync(BaseItem item, CancellationToken ct)
        {
            try
            {
                var delay = Plugin.Instance?.GetOptions()?.PosterUpdateDelaySeconds ?? 2;
                if (delay < 0) delay = 0;

                LogInfo($"[EmbyIcons] Scheduled overlay cache clear for '{item.Name}' in {delay}s");
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                _enhancer?.ClearOverlayCacheForItem(item);
                LogInfo($"[EmbyIcons] Overlay (plugin cache) cleared for '{item.Name}' after {delay}s delay.");
            }
            catch (TaskCanceledException)
            {
                _logger.Debug($"Canceled pending overlay refresh for '{item.Name}'.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Exception in DelayedOverlayRefreshAsync.", ex);
            }
        }

        private void LogInfo(string message)
        {
            if (GetOptions()?.EnableOverlayLogging == true)
                _logger.Info(message);
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
            _libraryManager.ItemAdded -= LibraryItemChanged;
            _libraryManager.ItemUpdated -= LibraryItemChanged;
            _logger.Info("EmbyIcons plugin disposed and event handlers unregistered.");
        }
    }
}
