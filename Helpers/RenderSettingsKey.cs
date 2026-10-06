using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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

        private static string Compute(ProfileSettings settings, PluginOptions globalOptions, DrawableContent content)
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
    }
}
