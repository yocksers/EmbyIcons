using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmbyIcons.Helpers
{
    internal static class MediaStreamHelper
    {
        public static string? GetAudioCodecIconName(MediaStream stream)
        {
            if (!string.IsNullOrEmpty(stream.Codec)) return stream.Codec.ToLowerInvariant();
            return null;
        }

        public static string? GetVideoCodecIconName(MediaStream stream)
        {
            if (!string.IsNullOrEmpty(stream.Codec)) return stream.Codec.ToLowerInvariant();
            return null;
        }

        public static string? GetChannelIconName(MediaStream stream)
        {
            if (!string.IsNullOrEmpty(stream.ChannelLayout))
            {
                return stream.ChannelLayout.ToLowerInvariant();
            }

            return null;
        }

        public static string? GetAspectRatioIconName(MediaStream? stream)
        {
            if (stream != null && !string.IsNullOrEmpty(stream.AspectRatio))
            {
                return stream.AspectRatio.Replace(":", "x");
            }
            return null;
        }

        public static string? GetResolutionIconNameFromStream(MediaStream? videoStream, IEnumerable<string> knownResolutionKeys)
        {
            if (videoStream?.DisplayTitle == null || !knownResolutionKeys.Any()) return null;
            string lowerTitle = videoStream.DisplayTitle.ToLowerInvariant();
            foreach (var key in knownResolutionKeys)
            {
                if (lowerTitle.Contains(key)) return key;
            }
            return null;
        }

        public static string? GetVideoFormatIconName(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            if (streams.Any(s => s.Type == MediaStreamType.Video && s.VideoRange?.Contains("dolby", System.StringComparison.OrdinalIgnoreCase) == true))
            {
                return "dv";
            }

            if (!string.IsNullOrEmpty(item.Path) && Path.GetFileName(item.Path).Contains("hdr10plus", System.StringComparison.OrdinalIgnoreCase))
            {
                return "hdr10plus";
            }

            if (streams.Any(s => s.Type == MediaStreamType.Video && s.VideoRange?.Contains("hdr", System.StringComparison.OrdinalIgnoreCase) == true))
            {
                return "hdr";
            }

            return null;
        }

        public static string? GetParentalRatingIconName(string? officialRating)
        {
            if (string.IsNullOrWhiteSpace(officialRating))
            {
                return null;
            }
            return officialRating.ToLowerInvariant().Replace('/', '-');
        }

        public static string GetItemMediaStreamHash(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            var parts = new List<string>(8);
            parts.Add(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l)));
            parts.Add(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l)));
            var audioCodecs = streams.Where(s => s.Type == MediaStreamType.Audio).Select(GetAudioCodecIconName).Where(c => c != null).Select(c => c!).Distinct().OrderBy(c => c);
            parts.Add(string.Join(",", audioCodecs));
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            parts.Add(videoStream != null ? GetVideoCodecIconName(videoStream) ?? "none" : "none");
            var audioStreamForChannels = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels).FirstOrDefault();
            parts.Add(audioStreamForChannels != null ? GetChannelIconName(audioStreamForChannels) ?? "none" : "none");
            parts.Add(GetVideoFormatIconName(item, streams) ?? "none");
            parts.Add(GetAspectRatioIconName(videoStream) ?? "none");
            var combinedString = string.Join(";", parts);
            using var md5 = MD5.Create();
            return System.BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(combinedString))).Replace("-", "").Substring(0, 8);
        }
    }
}