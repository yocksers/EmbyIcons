﻿using EmbyIcons.Helpers;
using EmbyIcons.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    public partial class EmbyIconsEnhancer : IImageEnhancer, IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private const int MaxConcurrencyLocks = 500;

        private readonly ILibraryManager _libraryManager;
        internal readonly ILogger _logger;

        private static readonly SemaphoreSlim _globalConcurrencyLock =
            new(Math.Max(1, Convert.ToInt32(Environment.ProcessorCount * 0.75)));

        internal readonly IconCacheManager _iconCacheManager;
        private readonly OverlayDataService _overlayDataService;
        private readonly ImageOverlayService _imageOverlayService;

        internal static readonly SKPaint ResamplingPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        internal static readonly SKPaint AliasedPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        internal static readonly SKPaint TextPaint = new() { IsAntialias = true, Color = SKColors.White };
        internal static readonly SKPaint AliasedTextPaint = new() { IsAntialias = false, FilterQuality = SKFilterQuality.None, Color = SKColors.White };
        internal static readonly SKPaint TextStrokePaint = new() { IsAntialias = true, Color = SKColors.Black, Style = SKPaintStyle.StrokeAndFill };

        public ILogger Logger => _logger;

        public EmbyIconsEnhancer(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logManager.GetLogger(nameof(EmbyIconsEnhancer));

            _logger.Info("[EmbyIcons] Session started.");

            _iconCacheManager = new IconCacheManager(_logger);
            _overlayDataService = new OverlayDataService(this);
            _imageOverlayService = new ImageOverlayService(_logger, _iconCacheManager);
        }

        public async Task ForceCacheRefreshAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            _logger.Info($"[EmbyIcons] Forcing full cache refresh for folder: '{iconsFolder}'");
            await _iconCacheManager.RefreshCacheOnDemandAsync(iconsFolder, cancellationToken, force: true);
        }

        private BaseItem GetFullItem(BaseItem item)
        {
            BaseItem? fullItem = null;

            if (item.Id != Guid.Empty)
            {
                fullItem = _libraryManager.GetItemById(item.Id);
            }

            if (fullItem == null && item.InternalId > 0)
            {
                fullItem = _libraryManager.GetItemById(item.InternalId);
            }

            return fullItem ?? item;
        }

        public MetadataProviderPriority Priority => MetadataProviderPriority.Last;

        public bool Supports(BaseItem? item, ImageType imageType)
        {
            if (item == null) return false;

            var fullItem = GetFullItem(item);

            var profile = Plugin.Instance?.GetProfileForItem(fullItem);
            if (profile == null) return false;

            var options = profile.Settings;

            bool isTypeSupported = imageType switch
            {
                ImageType.Primary => options.EnableForPosters,
                ImageType.Thumb => options.EnableForThumbs,
                ImageType.Banner => options.EnableForBanners,
                _ => false
            };

            if (!isTypeSupported) return false;

            bool isSupportedType = fullItem is Video || fullItem is Series || fullItem is Season || fullItem is Photo || fullItem is BoxSet;
            if (!isSupportedType) return false;

            if (fullItem is Episode && !options.ShowOverlaysForEpisodes) return false;
            if (fullItem is Series && !options.ShowSeriesIconsIfAllEpisodesHaveLanguage) return false;
            if (fullItem is BoxSet && !options.ShowCollectionIconsIfAllChildrenHaveLanguage) return false;

            return options.AudioIconAlignment != IconAlignment.Disabled ||
                   options.SubtitleIconAlignment != IconAlignment.Disabled ||
                   options.ChannelIconAlignment != IconAlignment.Disabled ||
                   options.VideoFormatIconAlignment != IconAlignment.Disabled ||
                   options.ResolutionIconAlignment != IconAlignment.Disabled ||
                   options.CommunityScoreIconAlignment != IconAlignment.Disabled ||
                   options.AudioCodecIconAlignment != IconAlignment.Disabled ||
                   options.VideoCodecIconAlignment != IconAlignment.Disabled ||
                   options.TagIconAlignment != IconAlignment.Disabled ||
                   options.AspectRatioIconAlignment != IconAlignment.Disabled ||
                   options.ParentalRatingIconAlignment != IconAlignment.Disabled;
        }

        private static string SanitizeTagForKey(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

            Span<char> buf = stackalloc char[tag.Length];
            int writeIndex = 0;
            bool lastWasWhitespace = true;

            foreach (char c in tag)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasWhitespace)
                    {
                        buf[writeIndex++] = '-';
                        lastWasWhitespace = true;
                    }
                }
                else
                {
                    buf[writeIndex++] = char.ToLowerInvariant(c);
                    lastWasWhitespace = false;
                }
            }

            if (writeIndex > 0 && buf[writeIndex - 1] == '-')
            {
                writeIndex--;
            }

            return (writeIndex == 0) ? string.Empty : new string(buf.Slice(0, writeIndex));
        }

        private static string? GetTagsCacheKey(IReadOnlyList<string> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return null;
            }

            return string.Join(",", tags.Select(SanitizeTagForKey).Where(t => !string.IsNullOrEmpty(t)).OrderBy(t => t, StringComparer.Ordinal));
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return "";

            var fullItem = GetFullItem(item);

            var profile = plugin.GetProfileForItem(fullItem);
            if (profile == null) return "";

            var globalOptions = plugin.GetConfiguredOptions();
            var options = profile.Settings;
            var sb = new StringBuilder(512);

            sb.Append("ei7_")
              .Append(fullItem.Id.ToString("N")).Append('_').Append((int)imageType)
              .Append("_v").Append(plugin.ConfigurationVersion)
              .Append("_p").Append(profile.Id.ToString("N"));

            sb.Append('_').Append((int)options.AudioIconAlignment).Append(options.AudioOverlayHorizontal ? 't' : 'f').Append(options.AudioIconPriority);
            sb.Append('_').Append((int)options.SubtitleIconAlignment).Append(options.SubtitleOverlayHorizontal ? 't' : 'f').Append(options.SubtitleIconPriority);
            sb.Append('_').Append((int)options.ChannelIconAlignment).Append(options.ChannelOverlayHorizontal ? 't' : 'f').Append(options.ChannelIconPriority);
            sb.Append('_').Append((int)options.AudioCodecIconAlignment).Append(options.AudioCodecOverlayHorizontal ? 't' : 'f').Append(options.AudioCodecIconPriority);
            sb.Append('_').Append((int)options.VideoFormatIconAlignment).Append(options.VideoFormatOverlayHorizontal ? 't' : 'f').Append(options.VideoFormatIconPriority);
            sb.Append('_').Append((int)options.VideoCodecIconAlignment).Append(options.VideoCodecOverlayHorizontal ? 't' : 'f').Append(options.VideoCodecIconPriority);
            sb.Append('_').Append((int)options.TagIconAlignment).Append(options.TagOverlayHorizontal ? 't' : 'f').Append(options.TagIconPriority);
            sb.Append('_').Append((int)options.ResolutionIconAlignment).Append(options.ResolutionOverlayHorizontal ? 't' : 'f').Append(options.ResolutionIconPriority);
            sb.Append('_').Append((int)options.CommunityScoreIconAlignment).Append(options.CommunityScoreOverlayHorizontal ? 't' : 'f').Append(options.CommunityScoreIconPriority);
            sb.Append('_').Append((int)options.AspectRatioIconAlignment).Append(options.AspectRatioOverlayHorizontal ? 't' : 'f').Append(options.AspectRatioIconPriority);
            sb.Append('_').Append((int)options.ParentalRatingIconAlignment).Append(options.ParentalRatingOverlayHorizontal ? 't' : 'f').Append(options.ParentalRatingIconPriority);

            sb.Append('_').Append(options.IconSize)
              .Append(globalOptions.JpegQuality)
              .Append(globalOptions.EnableImageSmoothing ? 't' : 'f')
              .Append((int)globalOptions.OutputFormat);

            sb.Append('_').Append((int)options.CommunityScoreBackgroundShape).Append(options.CommunityScoreBackgroundColor).Append(options.CommunityScoreBackgroundOpacity);
            sb.Append('_').Append(options.UseSeriesLiteMode ? 't' : 'f').Append(options.UseCollectionLiteMode ? 't' : 'f');
            sb.Append('_').Append(options.ShowSeriesIconsIfAllEpisodesHaveLanguage ? 't' : 'f').Append(options.ShowCollectionIconsIfAllChildrenHaveLanguage ? 't' : 'f');

            if (fullItem is Series series)
            {
                var aggResult = GetOrBuildAggregatedDataForParent(series, profile, options, globalOptions);
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else if (fullItem is BoxSet collection)
            {
                var aggResult = GetOrBuildAggregatedDataForParent(collection, profile, options, globalOptions);
                sb.Append("_ch").Append(aggResult.CombinedEpisodesHashShort);
            }
            else
            {
                sb.Append("_ih").Append(MediaStreamHelper.GetItemMediaStreamHashV2(fullItem, fullItem.GetMediaStreams() ?? new List<MediaStream>()));
            }

            var tagsKey = GetTagsCacheKey(fullItem.Tags);
            if (tagsKey != null)
            {
                sb.Append("_t").Append(tagsKey);
            }

            sb.Append("_r").Append(fullItem.CommunityRating?.ToString("F1") ?? "N");
            sb.Append("_d").Append(fullItem.DateModified.Ticks);
            return sb.ToString();
        }

        [Obsolete]
        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex) =>
            EnhanceImageAsync(item, inputFile, outputFile, imageType, imageIndex, CancellationToken.None);

        public async Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            var fullItem = GetFullItem(item);

            var profile = plugin.GetProfileForItem(fullItem);
            if (profile == null)
            {
                await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                return;
            }

            var globalOptions = plugin.GetConfiguredOptions();
            var profileOptions = profile.Settings;

            await _globalConcurrencyLock.WaitAsync(cancellationToken);
            try
            {
                var sem = _locks.GetOrAdd(fullItem.Id.ToString(), _ => new SemaphoreSlim(1, 1));
                await sem.WaitAsync(cancellationToken);
                try
                {
                    var overlayData = _overlayDataService.GetOverlayData(fullItem, profile, profileOptions, globalOptions);

                    using var sourceBitmap = SKBitmap.Decode(inputFile);
                    if (sourceBitmap == null)
                    {
                        await FileUtils.SafeCopyAsync(inputFile, outputFile, cancellationToken);
                        return;
                    }

                    bool sourceWasPng = string.Equals(Path.GetExtension(inputFile), ".png", StringComparison.OrdinalIgnoreCase);

                    using var outputStream = await _imageOverlayService.ApplyOverlaysAsync(
                        sourceBitmap, overlayData, profileOptions, globalOptions, cancellationToken, null, sourceWasPng);

                    string tempOutput = outputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await using (var fsOut = new FileStream(tempOutput, FileMode.Create, FileAccess.Write, FileShare.None, 262144, useAsync: true))
                    {
                        await outputStream.CopyToAsync(fsOut, cancellationToken);
                    }

                    try
                    {
                        File.Move(tempOutput, outputFile, true);
                    }
                    catch (IOException)
                    {
                        File.Copy(tempOutput, outputFile, true);
                        File.Delete(tempOutput);
                    }
                }
                finally
                {
                    sem.Release();

                    if (_locks.TryRemove(fullItem.Id.ToString(), out var removedSem))
                    {
                        removedSem.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false)
                    _logger.Debug($"[EmbyIcons] Image enhancement task cancelled for item: {item?.Name}.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Critical error during enhancement for {item?.Name}. Copying original.", ex);
                try { await FileUtils.SafeCopyAsync(inputFile, outputFile, CancellationToken.None); }
                catch { }
            }
            finally
            {
                _globalConcurrencyLock.Release();

                if (_locks.Count > MaxConcurrencyLocks)
                {
                    var keyToRemove = _locks.Keys.FirstOrDefault();
                    if (keyToRemove != null && _locks.TryRemove(keyToRemove, out var semToDispose))
                    {
                        semToDispose.Dispose();
                    }
                }
            }
        }

        public EnhancedImageInfo? GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex) =>
            (Plugin.Instance?.IsLibraryAllowed(item) ?? false) ? new() { RequiresTransparency = false } : null;

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalSize) => originalSize;

        public void Dispose()
        {
            foreach (var sem in _locks.Values)
                sem.Dispose();
            _locks.Clear();

            (_imageOverlayService as IDisposable)?.Dispose();
            _iconCacheManager.Dispose();
            _globalConcurrencyLock.Dispose();

            ResamplingPaint.Dispose();
            AliasedPaint.Dispose();
            TextPaint.Dispose();
            AliasedTextPaint.Dispose();
            TextStrokePaint.Dispose();
        }
    }
}