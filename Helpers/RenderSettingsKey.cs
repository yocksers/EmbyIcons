using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbyIcons.Configuration;

namespace EmbyIcons.Helpers
{
    [Flags]
    internal enum DrawableContent : long
    {
        None = 0,
        AudioLanguage = 1L << 0,
        Subtitle = 1L << 1,
        Channel = 1L << 2,
        AudioCodec = 1L << 3,
        VideoFormat = 1L << 4,
        VideoCodec = 1L << 5,
        Resolution = 1L << 6,
        AspectRatio = 1L << 7,
        FrameRate = 1L << 8,
        SampleRate = 1L << 9,
        AudioBitRate = 1L << 10,
        BitDepth = 1L << 11,
        Tags = 1L << 12,
        ParentalRating = 1L << 13,
        SeriesStatus = 1L << 14,
        MdbListRatings = 1L << 15,
        TvShow = 1L << 16,
        Collection = 1L << 17,
        Music = 1L << 18,
        MusicPoster = 1L << 19,
        PosterImage = 1L << 20,
        ThumbImage = 1L << 21,
        BannerImage = 1L << 22,
        AudioStreams = AudioLanguage | Channel | AudioCodec | SampleRate | AudioBitRate | BitDepth,
        VideoStreams = VideoFormat | VideoCodec | Resolution | AspectRatio | FrameRate
    }

    internal static class RenderSettingsKey
    {
        private sealed class SettingGroup
        {
            public SettingGroup(DrawableContent requires, Func<ProfileSettings, IconAlignment>? alignment, string? alignmentName, params string[] settings)
            {
                Requires = requires;
                Alignment = alignment;
                AlignmentName = alignmentName;
                Settings = settings;
            }

            public DrawableContent Requires { get; }
            public Func<ProfileSettings, IconAlignment>? Alignment { get; }
            public string? AlignmentName { get; }
            public string[] Settings { get; }
        }

        private static readonly string[] SupportOnlySettings =
        {
            nameof(ProfileSettings.EnableForPosters),
            nameof(ProfileSettings.EnableForThumbs),
            nameof(ProfileSettings.EnableForBanners),
            nameof(ProfileSettings.ShowOverlaysForEpisodes),
            nameof(ProfileSettings.ShowOverlaysForSeasons)
        };

        private static readonly SettingGroup[] Groups =
        {
            new(DrawableContent.AudioLanguage, s => s.AudioIconAlignment, nameof(ProfileSettings.AudioIconAlignment),
                nameof(ProfileSettings.AudioOverlayHorizontal), nameof(ProfileSettings.AudioIconPriority)),
            new(DrawableContent.Subtitle, s => s.SubtitleIconAlignment, nameof(ProfileSettings.SubtitleIconAlignment),
                nameof(ProfileSettings.SubtitleOverlayHorizontal), nameof(ProfileSettings.SubtitleIconPriority)),
            new(DrawableContent.Channel, s => s.ChannelIconAlignment, nameof(ProfileSettings.ChannelIconAlignment),
                nameof(ProfileSettings.ChannelOverlayHorizontal), nameof(ProfileSettings.ChannelIconPriority)),
            new(DrawableContent.AudioCodec, s => s.AudioCodecIconAlignment, nameof(ProfileSettings.AudioCodecIconAlignment),
                nameof(ProfileSettings.AudioCodecOverlayHorizontal), nameof(ProfileSettings.AudioCodecIconPriority)),
            new(DrawableContent.VideoFormat, s => s.VideoFormatIconAlignment, nameof(ProfileSettings.VideoFormatIconAlignment),
                nameof(ProfileSettings.VideoFormatOverlayHorizontal), nameof(ProfileSettings.VideoFormatIconPriority)),
            new(DrawableContent.VideoCodec, s => s.VideoCodecIconAlignment, nameof(ProfileSettings.VideoCodecIconAlignment),
                nameof(ProfileSettings.VideoCodecOverlayHorizontal), nameof(ProfileSettings.VideoCodecIconPriority)),
            new(DrawableContent.Resolution, s => s.ResolutionIconAlignment, nameof(ProfileSettings.ResolutionIconAlignment),
                nameof(ProfileSettings.ResolutionOverlayHorizontal), nameof(ProfileSettings.ResolutionIconPriority)),
            new(DrawableContent.AspectRatio, s => s.AspectRatioIconAlignment, nameof(ProfileSettings.AspectRatioIconAlignment),
                nameof(ProfileSettings.AspectRatioOverlayHorizontal), nameof(ProfileSettings.AspectRatioIconPriority), nameof(ProfileSettings.SnapAspectRatioToCommon)),
            new(DrawableContent.FrameRate, s => s.FrameRateIconAlignment, nameof(ProfileSettings.FrameRateIconAlignment),
                nameof(ProfileSettings.FrameRateOverlayHorizontal), nameof(ProfileSettings.FrameRateIconPriority), nameof(ProfileSettings.SnapFrameRateToCommon)),
            new(DrawableContent.SampleRate, s => s.SampleRateIconAlignment, nameof(ProfileSettings.SampleRateIconAlignment),
                nameof(ProfileSettings.SampleRateOverlayHorizontal), nameof(ProfileSettings.SampleRateIconPriority)),
            new(DrawableContent.AudioBitRate, s => s.AudioBitRateIconAlignment, nameof(ProfileSettings.AudioBitRateIconAlignment),
                nameof(ProfileSettings.AudioBitRateOverlayHorizontal), nameof(ProfileSettings.AudioBitRateIconPriority)),
            new(DrawableContent.BitDepth, s => s.BitDepthIconAlignment, nameof(ProfileSettings.BitDepthIconAlignment),
                nameof(ProfileSettings.BitDepthOverlayHorizontal), nameof(ProfileSettings.BitDepthIconPriority)),
            new(DrawableContent.Tags, s => s.TagIconAlignment, nameof(ProfileSettings.TagIconAlignment),
                nameof(ProfileSettings.TagOverlayHorizontal), nameof(ProfileSettings.TagIconPriority)),
            new(DrawableContent.Tags, null, null, nameof(ProfileSettings.TagBasedIcons)),
            new(DrawableContent.ParentalRating, s => s.ParentalRatingIconAlignment, nameof(ProfileSettings.ParentalRatingIconAlignment),
                nameof(ProfileSettings.ParentalRatingOverlayHorizontal), nameof(ProfileSettings.ParentalRatingIconPriority)),
            new(DrawableContent.SeriesStatus, s => s.SeriesStatusIconAlignment, nameof(ProfileSettings.SeriesStatusIconAlignment),
                nameof(ProfileSettings.SeriesStatusOverlayHorizontal), nameof(ProfileSettings.SeriesStatusIconPriority)),
            new(DrawableContent.MdbListRatings, s => s.PopcornScoreIconAlignment, nameof(ProfileSettings.PopcornScoreIconAlignment),
                nameof(ProfileSettings.PopcornScoreOverlayHorizontal), nameof(ProfileSettings.PopcornScoreIconPriority),
                nameof(ProfileSettings.PopcornScoreBackgroundShape), nameof(ProfileSettings.PopcornScoreBackgroundColor), nameof(ProfileSettings.PopcornScoreBackgroundOpacity)),
            new(DrawableContent.MdbListRatings, s => s.MyAnimeListScoreIconAlignment, nameof(ProfileSettings.MyAnimeListScoreIconAlignment),
                nameof(ProfileSettings.MyAnimeListScoreOverlayHorizontal), nameof(ProfileSettings.MyAnimeListScoreIconPriority),
                nameof(ProfileSettings.MyAnimeListScoreBackgroundShape), nameof(ProfileSettings.MyAnimeListScoreBackgroundColor), nameof(ProfileSettings.MyAnimeListScoreBackgroundOpacity)),
            new(DrawableContent.TvShow, null, null,
                nameof(ProfileSettings.ShowSeriesIconsIfAllEpisodesHaveLanguage), nameof(ProfileSettings.ExcludeSpecialsFromSeriesAggregation), nameof(ProfileSettings.UseSeriesLiteMode)),
            new(DrawableContent.Collection, null, null,
                nameof(ProfileSettings.ShowCollectionIconsIfAllChildrenHaveLanguage), nameof(ProfileSettings.UseCollectionLiteMode)),
            new(DrawableContent.Music, null, null,
                nameof(ProfileSettings.EnableMusicAlbumAggregation), nameof(ProfileSettings.UseMusicAlbumLiteMode)),
            new(DrawableContent.MusicPoster, null, null, nameof(ProfileSettings.NormalizeMusicPosterAspectRatio)),
            new(DrawableContent.PosterImage, null, null, nameof(ProfileSettings.NormalizePosterAspectRatio)),
            new(DrawableContent.ThumbImage, null, null, nameof(ProfileSettings.NormalizeThumbAspectRatio)),
            new(DrawableContent.BannerImage, null, null, nameof(ProfileSettings.NormalizeBannerAspectRatio))
        };

        private static readonly ConcurrentDictionary<(Guid ProfileId, long Content), string> _cache = new();

        public static void Invalidate() => _cache.Clear();

        public static string Get(IconProfile profile, PluginOptions globalOptions, DrawableContent content)
        {
            return _cache.GetOrAdd((profile.Id, (long)content), _ => Compute(profile.Settings, globalOptions, content));
        }

        private static JsonObject BuildNode(ProfileSettings settings, DrawableContent content)
        {
            var node = JsonSerializer.SerializeToNode(settings) as JsonObject ?? new JsonObject();

            foreach (var name in SupportOnlySettings)
            {
                node.Remove(name);
            }

            foreach (var group in Groups)
            {
                bool present = (content & group.Requires) == group.Requires;
                bool disabled = group.Alignment != null && group.Alignment(settings) == IconAlignment.Disabled;

                if (!present && group.AlignmentName != null)
                {
                    node.Remove(group.AlignmentName);
                }

                if (!present || disabled)
                {
                    foreach (var name in group.Settings)
                    {
                        node.Remove(name);
                    }
                }
            }

            return node;
        }

        internal static string Compute(ProfileSettings settings, PluginOptions globalOptions, DrawableContent content)
        {
            var node = BuildNode(settings, content);
            var text = new StringBuilder(node.ToJsonString())
                .Append('|').Append(globalOptions.IconsFolder ?? string.Empty)
                .Append('|').Append((int)globalOptions.IconLoadingMode)
                .Append('|').Append((int)globalOptions.OutputFormat)
                .Append('|').Append(globalOptions.JpegQuality)
                .Append('|').Append(globalOptions.MaxRenderDimension)
                .Append('|').Append(globalOptions.EnableImageSmoothing)
                .ToString();

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
        }

        // Deliberate compatibility contract. New options enter image invalidation only
        // after their rendering effect is reviewed; JSON property order is irrelevant.
        private const string RenderCompatibilityVersion = "1";

        private static readonly HashSet<string> VisualProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ProfileSettings.EnableForPosters), nameof(ProfileSettings.EnableForThumbs), nameof(ProfileSettings.EnableForBanners),
            nameof(ProfileSettings.NormalizePosterAspectRatio), nameof(ProfileSettings.NormalizeThumbAspectRatio), nameof(ProfileSettings.NormalizeBannerAspectRatio),
            nameof(ProfileSettings.NormalizeMusicPosterAspectRatio), nameof(ProfileSettings.ShowOverlaysForEpisodes), nameof(ProfileSettings.ShowOverlaysForSeasons),
            nameof(ProfileSettings.ShowSeriesIconsIfAllEpisodesHaveLanguage), nameof(ProfileSettings.ExcludeSpecialsFromSeriesAggregation), nameof(ProfileSettings.ShowCollectionIconsIfAllChildrenHaveLanguage),
            nameof(ProfileSettings.UseCollectionLiteMode), nameof(ProfileSettings.EnableMusicAlbumAggregation), nameof(ProfileSettings.UseMusicAlbumLiteMode),
            nameof(ProfileSettings.AudioIconAlignment), nameof(ProfileSettings.AudioOverlayHorizontal), nameof(ProfileSettings.AudioIconPriority),
            nameof(ProfileSettings.SubtitleIconAlignment), nameof(ProfileSettings.SubtitleOverlayHorizontal), nameof(ProfileSettings.SubtitleIconPriority),
            nameof(ProfileSettings.ChannelIconAlignment), nameof(ProfileSettings.ChannelOverlayHorizontal), nameof(ProfileSettings.ChannelIconPriority),
            nameof(ProfileSettings.AudioCodecIconAlignment), nameof(ProfileSettings.AudioCodecOverlayHorizontal), nameof(ProfileSettings.AudioCodecIconPriority),
            nameof(ProfileSettings.VideoFormatIconAlignment), nameof(ProfileSettings.VideoFormatOverlayHorizontal), nameof(ProfileSettings.VideoFormatIconPriority),
            nameof(ProfileSettings.VideoCodecIconAlignment), nameof(ProfileSettings.VideoCodecOverlayHorizontal), nameof(ProfileSettings.VideoCodecIconPriority),
            nameof(ProfileSettings.TagIconAlignment), nameof(ProfileSettings.TagOverlayHorizontal), nameof(ProfileSettings.TagIconPriority),
            nameof(ProfileSettings.ResolutionIconAlignment), nameof(ProfileSettings.ResolutionOverlayHorizontal), nameof(ProfileSettings.ResolutionIconPriority),
            nameof(ProfileSettings.CommunityScoreIconAlignment), nameof(ProfileSettings.CommunityScoreOverlayHorizontal), nameof(ProfileSettings.CommunityScoreIconPriority),
            nameof(ProfileSettings.RottenTomatoesScoreIconAlignment), nameof(ProfileSettings.RottenTomatoesScoreOverlayHorizontal), nameof(ProfileSettings.RottenTomatoesScoreIconPriority),
            nameof(ProfileSettings.RottenTomatoesScoreBackgroundShape), nameof(ProfileSettings.RottenTomatoesScoreBackgroundColor), nameof(ProfileSettings.RottenTomatoesScoreBackgroundOpacity),
            nameof(ProfileSettings.PopcornScoreIconAlignment), nameof(ProfileSettings.PopcornScoreOverlayHorizontal), nameof(ProfileSettings.PopcornScoreIconPriority),
            nameof(ProfileSettings.PopcornScoreBackgroundShape), nameof(ProfileSettings.PopcornScoreBackgroundColor), nameof(ProfileSettings.PopcornScoreBackgroundOpacity),
            nameof(ProfileSettings.MyAnimeListScoreIconAlignment), nameof(ProfileSettings.MyAnimeListScoreOverlayHorizontal), nameof(ProfileSettings.MyAnimeListScoreIconPriority),
            nameof(ProfileSettings.MyAnimeListScoreBackgroundShape), nameof(ProfileSettings.MyAnimeListScoreBackgroundColor), nameof(ProfileSettings.MyAnimeListScoreBackgroundOpacity),
            nameof(ProfileSettings.AspectRatioIconAlignment), nameof(ProfileSettings.AspectRatioOverlayHorizontal), nameof(ProfileSettings.AspectRatioIconPriority),
            nameof(ProfileSettings.SnapAspectRatioToCommon), nameof(ProfileSettings.ParentalRatingIconAlignment), nameof(ProfileSettings.ParentalRatingOverlayHorizontal),
            nameof(ProfileSettings.ParentalRatingIconPriority), nameof(ProfileSettings.SourceIconAlignment), nameof(ProfileSettings.SourceOverlayHorizontal),
            nameof(ProfileSettings.SourceIconPriority), nameof(ProfileSettings.FavoriteCountIconAlignment), nameof(ProfileSettings.FavoriteCountOverlayHorizontal),
            nameof(ProfileSettings.FavoriteCountIconPriority), nameof(ProfileSettings.FavoriteCountBackgroundShape), nameof(ProfileSettings.FavoriteCountBackgroundColor),
            nameof(ProfileSettings.FavoriteCountBackgroundOpacity), nameof(ProfileSettings.FrameRateIconAlignment), nameof(ProfileSettings.FrameRateOverlayHorizontal),
            nameof(ProfileSettings.FrameRateIconPriority), nameof(ProfileSettings.SnapFrameRateToCommon), nameof(ProfileSettings.OriginalLanguageIconAlignment),
            nameof(ProfileSettings.OriginalLanguageOverlayHorizontal), nameof(ProfileSettings.OriginalLanguageIconPriority), nameof(ProfileSettings.SeriesStatusIconAlignment),
            nameof(ProfileSettings.SeriesStatusOverlayHorizontal), nameof(ProfileSettings.SeriesStatusIconPriority), nameof(ProfileSettings.SampleRateIconAlignment),
            nameof(ProfileSettings.SampleRateOverlayHorizontal), nameof(ProfileSettings.SampleRateIconPriority), nameof(ProfileSettings.AudioBitRateIconAlignment),
            nameof(ProfileSettings.AudioBitRateOverlayHorizontal), nameof(ProfileSettings.AudioBitRateIconPriority), nameof(ProfileSettings.BitDepthIconAlignment),
            nameof(ProfileSettings.BitDepthOverlayHorizontal), nameof(ProfileSettings.BitDepthIconPriority), nameof(ProfileSettings.CommunityScoreBackgroundShape),
            nameof(ProfileSettings.CommunityScoreBackgroundColor), nameof(ProfileSettings.CommunityScoreBackgroundOpacity), nameof(ProfileSettings.IconSize),
            nameof(ProfileSettings.TopLeftIconSize), nameof(ProfileSettings.TopRightIconSize), nameof(ProfileSettings.BottomLeftIconSize),
            nameof(ProfileSettings.BottomRightIconSize), nameof(ProfileSettings.IconSpacing), nameof(ProfileSettings.EdgePaddingHorizontal),
            nameof(ProfileSettings.EdgePaddingVertical), nameof(ProfileSettings.TopLeftHorizontalPadding), nameof(ProfileSettings.TopLeftVerticalPadding),
            nameof(ProfileSettings.TopRightHorizontalPadding), nameof(ProfileSettings.TopRightVerticalPadding), nameof(ProfileSettings.BottomLeftHorizontalPadding),
            nameof(ProfileSettings.BottomLeftVerticalPadding), nameof(ProfileSettings.BottomRightHorizontalPadding), nameof(ProfileSettings.BottomRightVerticalPadding),
            nameof(ProfileSettings.RatingFontSizeMultiplier), nameof(ProfileSettings.RatingPercentageSuffix), nameof(ProfileSettings.RatingTextVerticalOffset),
            nameof(ProfileSettings.EnableTopIconBar), nameof(ProfileSettings.TopIconBarHeight), nameof(ProfileSettings.TopIconBarColor),
            nameof(ProfileSettings.TopIconBarOpacity), nameof(ProfileSettings.TopIconBarOverlay), nameof(ProfileSettings.EnableBottomIconBar),
            nameof(ProfileSettings.BottomIconBarHeight), nameof(ProfileSettings.BottomIconBarColor), nameof(ProfileSettings.BottomIconBarOpacity),
            nameof(ProfileSettings.BottomIconBarOverlay), nameof(ProfileSettings.OnlyDrawBarsWhenIconsPresent), nameof(ProfileSettings.UseSeriesLiteMode),
            nameof(ProfileSettings.FilenameBasedIcons), nameof(ProfileSettings.TagBasedIcons), nameof(ProfileSettings.SafeZones),
        };

        internal static string ComputeStable(ProfileSettings settings, PluginOptions options, DrawableContent content, BaseItem item, ImageType type)
        {
            var node = BuildNode(settings, content);
            foreach (string name in node.Select(p => p.Key).ToArray())
                if (!VisualProperties.Contains(name)) node.Remove(name);
            foreach (string name in VisualProperties.Where(n => n.EndsWith("IconAlignment", StringComparison.Ordinal)))
            {
                bool disabled = !node.TryGetPropertyValue(name, out var alignment) || alignment == null || alignment.GetValue<int>() == (int)IconAlignment.Disabled;
                if (!disabled) continue;
                string prefix = name.Substring(0, name.Length - "IconAlignment".Length);
                foreach (string suffix in new[] { "OverlayHorizontal", "IconPriority", "BackgroundShape", "BackgroundColor", "BackgroundOpacity" })
                    node.Remove(prefix + suffix);
            }
            if (!OverlayRequirements.VideoChildren(item, settings))
                foreach (string name in new[] { nameof(ProfileSettings.UseSeriesLiteMode), nameof(ProfileSettings.ExcludeSpecialsFromSeriesAggregation),
                    nameof(ProfileSettings.ShowSeriesIconsIfAllEpisodesHaveLanguage), nameof(ProfileSettings.UseCollectionLiteMode),
                    nameof(ProfileSettings.ShowCollectionIconsIfAllChildrenHaveLanguage) }) node.Remove(name);
            if (!(item is MusicAlbum || item is MusicArtist) || !OverlayRequirements.MusicChildren(item, settings))
                foreach (string name in new[] { nameof(ProfileSettings.EnableMusicAlbumAggregation), nameof(ProfileSettings.UseMusicAlbumLiteMode) }) node.Remove(name);
            node["FilenameBasedIcons"] = JsonSerializer.SerializeToNode(settings.FilenameBasedIcons.Where(m =>
                m.IconAlignment != IconAlignment.Disabled && !string.IsNullOrWhiteSpace(m.Keyword) && !string.IsNullOrWhiteSpace(m.IconName) &&
                (item is Movie ? m.ApplyToMovies : item is Episode ? m.ApplyToEpisodes :
                 item is Series ? m.ApplyToSeries || m.ApplyToEpisodes : item is Season ? m.ApplyToSeasons || m.ApplyToEpisodes :
                 item is Audio ? m.ApplyToTracks : item is MusicAlbum ? m.ApplyToAlbums || (settings.EnableMusicAlbumAggregation && m.ApplyToTracks) :
                 item is MusicArtist && (m.ApplyToArtists || (settings.EnableMusicAlbumAggregation && (m.ApplyToAlbums || m.ApplyToTracks))))).ToArray());
            node["TagBasedIcons"] = JsonSerializer.SerializeToNode(settings.TagBasedIcons.Where(m =>
                m.IconAlignment != IconAlignment.Disabled && !string.IsNullOrWhiteSpace(m.TagName) &&
                (item is Movie ? m.ApplyToMovies : item is Episode ? m.ApplyToEpisodes : item is Audio ? m.ApplyToTracks :
                 item is MusicAlbum ? m.ApplyToAlbums : !(item is MusicArtist) || m.ApplyToArtists)).ToArray());
            TrimTargetFlags(node["FilenameBasedIcons"], item, settings, false);
            TrimTargetFlags(node["TagBasedIcons"], item, settings, true);
            node["RenderCompatibility"] = RenderCompatibilityVersion;
            node["Supported"] = OverlayRequirements.Supports(item, type, settings);
            node["IconsFolder"] = options.IconsFolder ?? string.Empty;
            node["IconLoadingMode"] = (int)options.IconLoadingMode;
            node["OutputFormat"] = (int)options.OutputFormat;
            node["JpegQuality"] = options.JpegQuality;
            node["MaxRenderDimension"] = options.MaxRenderDimension;
            node["EnableImageSmoothing"] = options.EnableImageSmoothing;
            node["ManualRefresh"] = options.PersistedVersion;
            using var document = JsonDocument.Parse(node.ToJsonString());
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
            return ImageInputProjection.Hash(Encoding.UTF8.GetString(stream.ToArray()));
        }

        private static void TrimTargetFlags(JsonNode? value, BaseItem item, ProfileSettings settings, bool tags)
        {
            if (!(value is JsonArray array)) return;
            var flags = item is Movie ? new[] { "ApplyToMovies" } : item is Episode ? new[] { "ApplyToEpisodes" } :
                item is Audio ? new[] { "ApplyToTracks" } : item is MusicAlbum ?
                    (!tags && settings.EnableMusicAlbumAggregation ? new[] { "ApplyToAlbums", "ApplyToTracks" } : new[] { "ApplyToAlbums" }) :
                item is MusicArtist ? (!tags && settings.EnableMusicAlbumAggregation ? new[] { "ApplyToArtists", "ApplyToAlbums", "ApplyToTracks" } : new[] { "ApplyToArtists" }) :
                !tags && item is Series ? new[] { "ApplyToSeries", "ApplyToEpisodes" } :
                !tags && item is Season ? new[] { "ApplyToSeasons", "ApplyToEpisodes" } : Array.Empty<string>();
            foreach (var entry in array.OfType<JsonObject>())
                foreach (string name in entry.Select(p => p.Key).Where(n => n.StartsWith("ApplyTo", StringComparison.Ordinal) && !flags.Contains(n)).ToArray()) entry.Remove(name);
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray()) WriteCanonical(writer, value);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }
}
