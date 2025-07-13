using EmbyIcons.Models;
using EmbyIcons.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons
{
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

            var previewData = new OverlayData
            {
                AudioLanguages = { "eng" },
                SubtitleLanguages = { "eng" },
                ResolutionIconName = "4k",
                VideoFormatIconName = "hdr",
                VideoCodecs = { "h265" },
                Tags = { "tag" },
                ChannelIconName = "5.1",
                AudioCodecs = { "dts" },
                CommunityRating = 6.9f
            };

            var resultStream = await _imageOverlayService.ApplyOverlaysAsync(originalBitmap, previewData, options, CancellationToken.None);

            return resultStream;
        }
    }
}
