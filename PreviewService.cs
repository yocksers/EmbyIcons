using EmbyIcons.Api;
using EmbyIcons.Helpers;
using EmbyIcons.Models;
using EmbyIcons.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
    [Unauthenticated]
    [Route(ApiRoutes.Preview, "GET", Summary = "Generates a live preview image based on current settings")]
    public class GetIconPreview : IReturn<Stream>
    {
        public string? OptionsJson { get; set; }
    }

    public class PreviewService : IService
    {
        private readonly ImageOverlayService _imageOverlayService;

        public PreviewService()
        {
            var enhancer = Plugin.Instance?.Enhancer ?? throw new InvalidOperationException("Enhancer is not initialized.");
            _imageOverlayService = new ImageOverlayService(enhancer.Logger, enhancer._iconCacheManager);
        }

        public async Task<object> Get(GetIconPreview request)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not initialized.");
            if (string.IsNullOrEmpty(request.OptionsJson))
            {
                plugin.Logger.Warn("[EmbyIcons] Preview request received with empty options.");
                return new MemoryStream();
            }

            var profileSettings = JsonSerializer.Deserialize<ProfileSettings>(request.OptionsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? throw new ArgumentException("Could not deserialize profile settings from JSON.");

            var globalOptions = plugin.GetConfiguredOptions();

            using var originalBitmap = SKBitmap.Decode(Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbyIcons.Images.preview.png"))
                ?? throw new InvalidOperationException("Failed to decode the preview background image.");

            var cacheManager = plugin.Enhancer._iconCacheManager;

            var customIcons = cacheManager.GetAllAvailableIconKeys(globalOptions.IconsFolder);
            var embeddedIcons = cacheManager.GetAllAvailableEmbeddedIconKeys();
            var masterIconList = new Dictionary<IconCacheManager.IconType, List<string>>();

            foreach (IconCacheManager.IconType type in Enum.GetValues(typeof(IconCacheManager.IconType)))
            {
                var custom = customIcons.GetValueOrDefault(type, new List<string>());
                var embedded = embeddedIcons.GetValueOrDefault(type, new List<string>());

                masterIconList[type] = globalOptions.IconLoadingMode switch
                {
                    IconLoadingMode.CustomOnly => custom,
                    IconLoadingMode.BuiltInOnly => embedded,
                    _ => custom.Union(embedded, StringComparer.OrdinalIgnoreCase).ToList()
                };
            }

            var random = new Random();
            string GetRandom(IconCacheManager.IconType type, string fallback)
            {
                var list = masterIconList.GetValueOrDefault(type, new List<string>());
                return list.Any() ? list[random.Next(list.Count)] : fallback;
            }

            var previewData = new OverlayData
            {
                AudioLanguages = new HashSet<string> { GetRandom(IconCacheManager.IconType.Language, "english") },
                SubtitleLanguages = new HashSet<string> { GetRandom(IconCacheManager.IconType.Subtitle, "english") },
                AudioCodecs = new HashSet<string> { GetRandom(IconCacheManager.IconType.AudioCodec, "dts"), GetRandom(IconCacheManager.IconType.AudioCodec, "aac") },
                VideoCodecs = new HashSet<string> { GetRandom(IconCacheManager.IconType.VideoCodec, "h264") },
                Tags = new HashSet<string> { "placeholder_for_preview" },
                ChannelIconName = GetRandom(IconCacheManager.IconType.Channel, "5.1"),
                VideoFormatIconName = GetRandom(IconCacheManager.IconType.VideoFormat, "hdr"),
                ResolutionIconName = GetRandom(IconCacheManager.IconType.Resolution, "1080p"),
                CommunityRating = 6.9f,
                AspectRatioIconName = GetRandom(IconCacheManager.IconType.AspectRatio, "16x9"),
                ParentalRatingIconName = GetRandom(IconCacheManager.IconType.ParentalRating, "pg-13")
            };

            var injectedIcons = new Dictionary<IconCacheManager.IconType, List<SKImage>>();
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = $"{GetType().Namespace}.Images.tag.png";
            await using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream != null && stream.Length > 0)
                {
                    using var data = SKData.Create(stream);
                    var tagIcon = SKImage.FromEncodedData(data);
                    if (tagIcon != null)
                    {
                        injectedIcons[IconCacheManager.IconType.Tag] = new List<SKImage> { tagIcon };
                    }
                }
            }

            var resultStream = new MemoryStream();
            await _imageOverlayService.ApplyOverlaysToStreamAsync(originalBitmap, previewData, profileSettings, globalOptions, resultStream, CancellationToken.None, injectedIcons);
            resultStream.Position = 0;

            // Note: `ImageOverlayService` will dispose any SKImage instances that were
            // provided for this rendering pass (including injected icons), so we should
            // not dispose them here to avoid double-dispose.

            return resultStream;
        }
    }
}