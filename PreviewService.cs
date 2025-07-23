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
    [Route("/EmbyIcons/Preview", "GET", Summary = "Generates a live preview image based on current settings")]
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
            var enhancer = Plugin.Instance?.Enhancer ?? throw new InvalidOperationException("Enhancer is not initialized.");
            if (string.IsNullOrEmpty(request.OptionsJson))
            {
                enhancer.Logger.Warn("[EmbyIcons] Preview request received with empty options.");
                return new MemoryStream();
            }

            var options = JsonSerializer.Deserialize<PluginOptions>(request.OptionsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? throw new ArgumentException("Could not deserialize options from JSON.");

            using var originalBitmap = SKBitmap.Decode(Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbyIcons.Images.preview.png"))
                ?? throw new InvalidOperationException("Failed to decode the preview background image.");

            var cacheManager = enhancer._iconCacheManager;
            var allIcons = cacheManager.GetAllAvailableIconKeys(options.IconsFolder);
            var random = new Random();

            string GetRandom(IconCacheManager.IconType type, string fallback)
            {
                var list = allIcons[type];
                return list.Any() ? list[random.Next(list.Count)] : fallback;
            }

            var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            audioLangs.Add(GetRandom(IconCacheManager.IconType.Language, "eng"));

            var audioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            audioCodecs.Add(GetRandom(IconCacheManager.IconType.AudioCodec, "dts"));
            var secondCodec = GetRandom(IconCacheManager.IconType.AudioCodec, "ac3");
            if (audioCodecs.Count < 2)
            {
                audioCodecs.Add(secondCodec);
            }

            var previewData = new OverlayData
            {
                AudioLanguages = audioLangs,
                SubtitleLanguages = new HashSet<string> { GetRandom(IconCacheManager.IconType.Subtitle, "eng") },
                ResolutionIconName = GetRandom(IconCacheManager.IconType.Resolution, "4k"),
                VideoFormatIconName = GetRandom(IconCacheManager.IconType.VideoFormat, "hdr"),
                VideoCodecs = new HashSet<string> { GetRandom(IconCacheManager.IconType.VideoCodec, "hevc") },
                Tags = new HashSet<string> { "placeholder_for_preview" },
                ChannelIconName = GetRandom(IconCacheManager.IconType.Channel, "5.1"),
                AudioCodecs = audioCodecs,
                CommunityRating = 6.9f,
                AspectRatioIconName = GetRandom(IconCacheManager.IconType.AspectRatio, "16x9")
            };

            var injectedIcons = new Dictionary<IconCacheManager.IconType, List<SKImage>>();
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = $"{GetType().Namespace}.Images.tag.png";

            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream != null && stream.Length > 0)
                {
                    using var data = SKData.Create(stream);
                    var tagIcon = SKImage.FromEncodedData(data);
                    if (tagIcon != null)
                    {
                        injectedIcons[IconCacheManager.IconType.Tag] = new List<SKImage> { tagIcon };
                    }
                    else
                    {
                        enhancer.Logger.Warn($"[EmbyIcons] Failed to decode embedded tag icon from resource: {resourceName}");
                    }
                }
                else
                {
                    enhancer.Logger.Warn($"[EmbyIcons] Could not load embedded tag icon resource stream: {resourceName}. Stream was null or empty.");
                }
            }

            var resultStream = await _imageOverlayService.ApplyOverlaysAsync(originalBitmap, previewData, options, CancellationToken.None, injectedIcons);

            return resultStream;
        }
    }
}