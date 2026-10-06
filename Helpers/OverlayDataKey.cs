using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EmbyIcons.Models;

namespace EmbyIcons.Helpers
{
    internal static class OverlayDataKey
    {
        public static string Compute(OverlayData data)
        {
            var text = new StringBuilder(256);

            AppendSet(text, data.AudioLanguages);
            AppendSet(text, data.SubtitleLanguages);
            AppendSet(text, data.AudioCodecs);
            AppendSet(text, data.VideoCodecs);
            AppendSet(text, data.Tags);
            AppendSet(text, data.SourceIcons);
            AppendIcons(text, data.FilenameBasedIcons);
            AppendIcons(text, data.TagBasedIcons);
            AppendValue(text, data.ChannelIconName);
            AppendValue(text, data.VideoFormatIconName);
            AppendValue(text, data.ResolutionIconName);
            AppendValue(text, data.AspectRatioIconName);
            AppendValue(text, data.ParentalRatingIconName);
            AppendValue(text, data.FrameRateIconName);
            AppendValue(text, data.OriginalLanguageIconName);
            AppendValue(text, data.SeriesStatusIconName);
            AppendValue(text, data.SampleRateIconName);
            AppendValue(text, data.AudioBitRateIconName);
            AppendValue(text, data.BitDepthIconName);
            AppendValue(text, data.CommunityRating?.ToString("R", CultureInfo.InvariantCulture));
            AppendValue(text, data.RottenTomatoesRating?.ToString("R", CultureInfo.InvariantCulture));

            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static DrawableContent GetContent(OverlayData data)
        {
            var content = DrawableContent.None;
            if (data.AudioLanguages.Count > 0) content |= DrawableContent.AudioLanguage;
            if (data.SubtitleLanguages.Count > 0) content |= DrawableContent.Subtitle;
            if (data.ChannelIconName != null) content |= DrawableContent.Channel;
            if (data.AudioCodecs.Count > 0) content |= DrawableContent.AudioCodec;
            if (data.VideoFormatIconName != null) content |= DrawableContent.VideoFormat;
            if (data.VideoCodecs.Count > 0) content |= DrawableContent.VideoCodec;
            if (data.ResolutionIconName != null) content |= DrawableContent.Resolution;
            if (data.AspectRatioIconName != null) content |= DrawableContent.AspectRatio;
            if (data.FrameRateIconName != null) content |= DrawableContent.FrameRate;
            if (data.SampleRateIconName != null) content |= DrawableContent.SampleRate;
            if (data.AudioBitRateIconName != null) content |= DrawableContent.AudioBitRate;
            if (data.BitDepthIconName != null) content |= DrawableContent.BitDepth;
            if (data.Tags.Count > 0 || data.TagBasedIcons.Count > 0) content |= DrawableContent.Tags;
            if (data.ParentalRatingIconName != null) content |= DrawableContent.ParentalRating;
            if (data.SeriesStatusIconName != null) content |= DrawableContent.SeriesStatus;
            return content;
        }

        private static void AppendSet(StringBuilder text, IEnumerable<string>? values)
        {
            if (values != null)
            {
                foreach (var value in values.Select(v => v.ToLowerInvariant()).Distinct().OrderBy(v => v, StringComparer.Ordinal))
                {
                    text.Append(value).Append(',');
                }
            }
            text.Append('|');
        }

        private static void AppendIcons(StringBuilder text, List<FilenameBasedIconData>? icons)
        {
            if (icons != null)
            {
                foreach (var icon in icons)
                {
                    text.Append(icon.IconName).Append(':')
                        .Append((int)icon.Alignment).Append(':')
                        .Append(icon.Priority).Append(':')
                        .Append(icon.HorizontalLayout ? '1' : '0').Append(',');
                }
            }
            text.Append('|');
        }

        private static void AppendValue(StringBuilder text, string? value)
        {
            text.Append(value ?? "-").Append('|');
        }
    }
}
