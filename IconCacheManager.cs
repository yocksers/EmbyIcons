﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using MediaBrowser.Model.Logging;

namespace EmbyIcons.Helpers
{
    public class IconCacheManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, CachedIcon> _iconImageCache = new();
        private readonly ConcurrentDictionary<string, SKImage> _embeddedIconCache = new();

        private static Dictionary<IconType, List<string>>? _embeddedIconKeysCache;
        private static readonly object _embeddedCacheLock = new object();

        private string? _iconsFolder;
        internal static readonly string[] SupportedCustomIconExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
        public enum IconType { Language, Subtitle, Channel, VideoFormat, Resolution, AudioCodec, VideoCodec, Tag, CommunityRating, AspectRatio, ParentalRating }

        public IconCacheManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Dictionary<IconType, List<string>> GetAllAvailableIconKeys(string iconsFolder)
        {
            var allKeys = new Dictionary<IconType, List<string>>();
            foreach (IconType type in Enum.GetValues(typeof(IconType))) allKeys[type] = new List<string>();
            if (string.IsNullOrEmpty(iconsFolder) || !Directory.Exists(iconsFolder)) return allKeys;

            try
            {
                var prefixLookup = Constants.PrefixMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(iconsFolder))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext) || !SupportedCustomIconExtensions.Contains(ext)) continue;
                    var parts = Path.GetFileNameWithoutExtension(file).Split(new[] { '.' }, 2);
                    if (parts.Length == 2 && prefixLookup.TryGetValue(parts[0], out var iconType))
                    {
                        allKeys[iconType].Add(parts[1].ToLowerInvariant());
                    }
                }
                if (allKeys.ContainsKey(IconType.Resolution))
                {
                    allKeys[IconType.Resolution] = allKeys[IconType.Resolution].OrderByDescending(x => x.Length).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] Failed to read files from '{iconsFolder}' during key scan.", ex);
            }
            return allKeys;
        }

        public Dictionary<IconType, List<string>> GetAllAvailableEmbeddedIconKeys()
        {
            lock (_embeddedCacheLock)
            {
                if (_embeddedIconKeysCache != null) return _embeddedIconKeysCache;
            }

            var embeddedKeys = new Dictionary<IconType, List<string>>();
            foreach (IconType type in Enum.GetValues(typeof(IconType))) embeddedKeys[type] = new List<string>();

            var prefixLookup = Constants.PrefixMap.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
            var assembly = Assembly.GetExecutingAssembly();
            const string resourcePrefix = "EmbyIcons.EmbeddedIcons.";
            var resourceNames = assembly.GetManifestResourceNames().Where(name => name.StartsWith(resourcePrefix) && name.EndsWith(".png"));

            foreach (var name in resourceNames)
            {
                var fileNameWithExt = name.Substring(resourcePrefix.Length);
                var parts = Path.GetFileNameWithoutExtension(fileNameWithExt).Split(new[] { '_' }, 2);
                if (parts.Length == 2 && prefixLookup.TryGetValue(parts[0], out var iconType))
                {
                    embeddedKeys[iconType].Add(parts[1].ToLowerInvariant());
                }
            }

            lock (_embeddedCacheLock)
            {
                _embeddedIconKeysCache = embeddedKeys;
            }

            return embeddedKeys;
        }

        public Task InitializeAsync(string iconsFolder, CancellationToken cancellationToken)
        {
            if (_iconsFolder != iconsFolder)
            {
                _iconsFolder = iconsFolder;
                return RefreshCacheOnDemandAsync(iconsFolder, cancellationToken, force: true);
            }
            return Task.CompletedTask;
        }

        public Task RefreshCacheOnDemandAsync(string iconsFolder, CancellationToken cancellationToken, bool force = false)
        {
            _iconsFolder = iconsFolder;
            _logger.Info("[EmbyIcons] Clearing all cached icon image data.");
            ClearImageCache(_iconImageCache);
            foreach (var icon in _embeddedIconCache.Values) icon.Dispose();
            _embeddedIconCache.Clear();
            return Task.CompletedTask;
        }

        public async Task<SKImage?> GetCachedIconAsync(string iconNameKey, IconType iconType, PluginOptions options, CancellationToken cancellationToken)
        {
            var loadingMode = options.IconLoadingMode;
            var prefix = Constants.PrefixMap[iconType];
            var lowerIconNameKey = iconNameKey.ToLowerInvariant();

            var customIconFileName = $"{prefix}.{lowerIconNameKey}";
            var embeddedIconFileName = $"{prefix}_{lowerIconNameKey}";
            var customIconsFolder = options.IconsFolder;

            if (loadingMode == IconLoadingMode.CustomOnly)
            {
                return await LoadCustomIconAsync(customIconFileName, customIconsFolder, cancellationToken);
            }

            if (loadingMode == IconLoadingMode.BuiltInOnly)
            {
                return await LoadEmbeddedIconAsync(embeddedIconFileName, cancellationToken);
            }

            // Hybrid mode
            var customIcon = await LoadCustomIconAsync(customIconFileName, customIconsFolder, cancellationToken);
            return customIcon ?? await LoadEmbeddedIconAsync(embeddedIconFileName, cancellationToken);
        }

        private async Task<SKImage?> LoadCustomIconAsync(string baseFileName, string iconsFolder, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(iconsFolder)) return null;
            if (_iconImageCache.TryGetValue(baseFileName, out var cached))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(cached.FullPath) == cached.FileWriteTimeUtc) return cached.Image;
                    if (_iconImageCache.TryRemove(baseFileName, out var oldCached)) oldCached.Image?.Dispose();
                }
                catch (Exception) { if (_iconImageCache.TryRemove(baseFileName, out var oldCached)) oldCached.Image?.Dispose(); }
            }
            foreach (var ext in SupportedCustomIconExtensions)
            {
                var fullPath = Path.Combine(iconsFolder, baseFileName + ext);
                if (File.Exists(fullPath)) return await TryLoadAndCacheIconAsync(fullPath, baseFileName, cancellationToken);
            }
            return null;
        }

        private Task<SKImage?> LoadEmbeddedIconAsync(string baseFileName, CancellationToken cancellationToken)
        {
            if (_embeddedIconCache.TryGetValue(baseFileName, out var cachedImage)) return Task.FromResult<SKImage?>(cachedImage);
            var resourceName = $"EmbyIcons.EmbeddedIcons.{baseFileName}.png";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return Task.FromResult<SKImage?>(null);
            using var data = SKData.Create(stream);
            var img = SKImage.FromEncodedData(data);
            if (img != null) _embeddedIconCache.TryAdd(baseFileName, img);
            return Task.FromResult(img);
        }

        private async Task<SKImage?> TryLoadAndCacheIconAsync(string iconPath, string cacheKey, CancellationToken cancellationToken)
        {
            try
            {
                if (new FileInfo(iconPath).Length < 50) return null;
                using var memoryStream = new MemoryStream();
                await using (var fileStream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
                {
                    await fileStream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
                }
                memoryStream.Position = 0;
                using var data = SKData.Create(memoryStream);
                var img = SKImage.FromEncodedData(data);
                if (img == null) return null;
                _iconImageCache[cacheKey] = new CachedIcon(img, File.GetLastWriteTimeUtc(iconPath), iconPath);
                return img;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[EmbyIcons] A critical error occurred while loading icon '{iconPath}'.", ex);
                return null;
            }
        }

        public void Dispose()
        {
            ClearImageCache(_iconImageCache);
            foreach (var icon in _embeddedIconCache.Values) icon.Dispose();
            _embeddedIconCache.Clear();
        }

        private void ClearImageCache(ConcurrentDictionary<string, CachedIcon> cache)
        {
            foreach (var cached in cache.Values) cached.Image?.Dispose();
            cache.Clear();
        }

        private sealed class CachedIcon
        {
            public SKImage? Image { get; }
            public DateTime FileWriteTimeUtc { get; }
            public string FullPath { get; }
            public CachedIcon(SKImage? image, DateTime fileWriteTimeUtc, string fullPath) { Image = image; FileWriteTimeUtc = fileWriteTimeUtc; FullPath = fullPath; }
        }
    }
}