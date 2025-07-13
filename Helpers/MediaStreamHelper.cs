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
        public static string? GetAudioCodecIconName(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            codec = codec.ToLowerInvariant();

            if (codec.Contains("aac")) return "aac";
            if (codec.Contains("pcm")) return "pcm";
            if (codec.Contains("flac")) return "flac";
            if (codec.Contains("mp3")) return "mp3";
            if (codec.Contains("ac-3") || codec == "ac3") return "ac3";
            if (codec.Contains("e-ac-3") || codec == "eac3") return "eac3";
            if (codec.Contains("dts") || codec == "dca") return "dts";
            if (codec.Contains("truehd")) return "truehd";

            return null;
        }

        public static string? GetVideoCodecIconName(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            codec = codec.ToLowerInvariant();

            if (codec.Contains("av1")) return "av1";
            if (codec.Contains("avc")) return "avc";
            if (codec.Contains("h264")) return "h264";
            if (codec.Contains("hevc") || codec.Contains("h265")) return "h265";
            if (codec.Contains("mpeg4")) return "mp4";
            if (codec.Contains("vc1")) return "vc1";
            if (codec.Contains("vp9")) return "vp9";
            if (codec.Contains("vvc") || codec.Contains("h266")) return "h266";

            return null;
        }

        public static string? GetChannelIconName(int channels) => channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => null };

        public static string? GetResolutionIconNameFromStream(MediaStream? videoStream)
        {
            if (videoStream?.DisplayTitle == null) return null;

            string lowerTitle = videoStream.DisplayTitle.ToLowerInvariant();
            if (lowerTitle.Contains("4k") || lowerTitle.Contains("2160p")) return "4k";
            if (lowerTitle.Contains("1080")) return "1080p";
            if (lowerTitle.Contains("720")) return "720p";
            if (lowerTitle.Contains("576")) return "576p";
            if (lowerTitle.Contains("480")) return "480p";

            return null;
        }

        public static bool HasDolbyVision(BaseItem item, IReadOnlyList<MediaStream> streams) =>
            streams.Any(s => s.Type == MediaStreamType.Video && ((s.VideoRange?.Contains("dolby", System.StringComparison.OrdinalIgnoreCase) ?? false) || (s.DisplayTitle?.Contains("dolby", System.StringComparison.OrdinalIgnoreCase) ?? false)));

        public static bool HasHdr10Plus(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            if (!string.IsNullOrEmpty(item.Path))
            {
                var fileName = Path.GetFileName(item.Path);
                if (fileName != null)
                {
                    if (fileName.Contains("hdr10+", System.StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("hdr10plus", System.StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return streams.Any(s => s.Type == MediaStreamType.Video && s.VideoRange?.Contains("hdr10plus", System.StringComparison.OrdinalIgnoreCase) == true);
        }

        public static bool HasHdr(BaseItem item, IReadOnlyList<MediaStream> streams) =>
            streams.Any(s => s.Type == MediaStreamType.Video && ((s.VideoRange?.Contains("hdr", System.StringComparison.OrdinalIgnoreCase) ?? false) || (s.DisplayTitle?.Contains("hdr", System.StringComparison.OrdinalIgnoreCase) ?? false)));

        public static string GetItemMediaStreamHash(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            var parts = new List<string>(7);

            parts.Add(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l)));
            parts.Add(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l)));

            var audioCodecs = streams
                .Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Codec))
                .Select(s => GetAudioCodecIconName(s.Codec))
                .Where(c => c != null)
                .Select(c => c!)
                .Distinct()
                .OrderBy(c => c);
            parts.Add(string.Join(",", audioCodecs));

            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            parts.Add(videoStream != null ? GetVideoCodecIconName(videoStream.Codec) ?? "none" : "none");

            int maxChannels = streams.Where(s => s.Type == MediaStreamType.Audio && s.Channels.HasValue).Select(s => s.Channels!.Value).DefaultIfEmpty(0).Max();
            parts.Add(GetChannelIconName(maxChannels) ?? "none");

            if (HasDolbyVision(item, streams)) parts.Add("dv");
            else if (HasHdr10Plus(item, streams)) parts.Add("hdr10plus");
            else if (HasHdr(item, streams)) parts.Add("hdr");
            else parts.Add("none");

            parts.Add(videoStream != null ? (GetResolutionIconNameFromStream(videoStream) ?? "none") : "none");

            var combinedString = string.Join(";", parts);
            using var md5 = MD5.Create();
            return System.BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(combinedString))).Replace("-", "").Substring(0, 8);
        }
    }
}
