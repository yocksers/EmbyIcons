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
            if (string.IsNullOrEmpty(request.OptionsJson))
            {
                return new MemoryStream();
            }

            var options = JsonSerializer.Deserialize<PluginOptions>(request.OptionsJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            }) ?? throw new ArgumentException("Could not deserialize options from JSON.");

            using var originalBitmap = SKBitmap.Decode(Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbyIcons.Images.preview.png"))
                ?? throw new InvalidOperationException("Failed to decode the preview background image.");

            var enhancer = Plugin.Instance?.Enhancer ?? throw new InvalidOperationException("Enhancer is not initialized.");
            var cacheManager = enhancer._iconCacheManager;

            var audioLangs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var audioLang1 = cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.Audio);
            if (audioLang1 != null) audioLangs.Add(audioLang1);

            var audioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var audioCodec1 = cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.AudioCodec);
            if (audioCodec1 != null) audioCodecs.Add(audioCodec1);
            var audioCodec2 = cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.AudioCodec);
            if (audioCodec2 != null) audioCodecs.Add(audioCodec2);

            var previewData = new OverlayData
            {
                AudioLanguages = audioLangs.Any() ? audioLangs : new HashSet<string> { "eng" },
                SubtitleLanguages = new HashSet<string> { cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.Subtitle) ?? "eng" },
                ResolutionIconName = cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.Resolution) ?? "4k",
                VideoFormatIconName = cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.VideoFormat) ?? "hdr",
                VideoCodecs = new HashSet<string> { cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.VideoCodec) ?? "h265" },
                Tags = new HashSet<string> { "tag" },
                ChannelIconName = cacheManager.GetRandomIconName(Helpers.IconCacheManager.IconType.Channel) ?? "5.1",
                AudioCodecs = audioCodecs.Any() ? audioCodecs : new HashSet<string> { "dts" },
                CommunityRating = 6.9f
            };

            var resultStream = await _imageOverlayService.ApplyOverlaysAsync(originalBitmap, previewData, options, CancellationToken.None);

            return resultStream;
        }
    }
}