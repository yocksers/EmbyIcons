using EmbyIcons;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbyIcons.Helpers
{
    public static class MediaStreamHelper
    {
        public static string? GetAudioCodecIconName(MediaStream stream)
        {
            try
            {
                if (stream == null) return null;
                var codec = (stream.Codec ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(codec)) return null;

                codec = codec.Replace("dts-hd ma", "dts-hdma")
                             .Replace("dts:x", "dtsx")
                             .Replace("e-ac-3", "eac3")
                             .Replace("ac-3", "ac3");

                return codec;
            }
            catch
            {
                return null;
            }
        }

        public static string? GetVideoCodecIconName(MediaStream stream)
        {
            try
            {
                if (stream == null) return null;
                var codec = (stream.Codec ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(codec)) return null;

                if (codec is "h265" or "x265") codec = "hevc";
                if (codec is "h264" or "x264") codec = "h264";

                return codec;
            }
            catch
            {
                return null;
            }
        }

        public static string? GetChannelIconName(MediaStream audioStream)
        {
            try
            {
                if (audioStream == null) return null;

                var ch = audioStream.Channels ?? 0;
                if (ch <= 1) return "mono";
                if (ch == 2) return "stereo";
                if (ch >= 8) return "7.1";
                if (ch >= 6) return "5.1";
                if (ch >= 3) return "surround";
                return null;
            }
            catch
            {
                return null;
            }
        }

        public static string? GetAspectRatioIconName(int width, int height, bool snapToCommon)
        {
            if (!snapToCommon || height <= 0) return null;

            var ratio = (double)width / height;
            var pairs = new (double v, string name)[] {
                (16.0/9.0, "16x9"),
                (21.0/9.0, "21x9"),
                (2.35, "2.35x1"),
                (2.39, "2.39x1"),
                (2.40, "2.40x1"),
                (1.85, "1.85x1"),
                (4.0/3.0, "4x3"),
            };

            var best = pairs.OrderBy(p => Math.Abs(p.v - ratio)).First();

            if (Math.Abs(best.v - ratio) < 0.1)
            {
                return best.name;
            }

            return null;
        }

        public static string? GetAspectRatioIconName(MediaStream? videoStream, bool snapToCommon)
        {
            if (videoStream == null) return null;

            if (snapToCommon && videoStream.Width.HasValue && videoStream.Height.HasValue)
            {
                var snappedName = GetAspectRatioIconName(videoStream.Width.Value, videoStream.Height.Value, true);
                if (snappedName != null) return snappedName;
            }

            if (!string.IsNullOrWhiteSpace(videoStream.AspectRatio))
            {
                return videoStream.AspectRatio.Trim().ToLowerInvariant().Replace(':', 'x');
            }

            return null;
        }

        public static string? GetResolutionIconNameFromStream(MediaStream? videoStream, IList<string> knownKeys)
        {
            if (videoStream == null) return null;

            var title = (videoStream.DisplayTitle ?? "").ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(title))
            {
                foreach (var key in knownKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key) && title.Contains(key))
                    {
                        return key;
                    }
                }
            }

            if (videoStream.Width.HasValue && videoStream.Height.HasValue)
            {
                var width = videoStream.Width.Value;
                var height = videoStream.Height.Value;
                var scanType = videoStream.IsInterlaced ? "i" : "p";

                string? resolutionName = null;

                if (width >= 3840 && width <= 4096)
                {
                    resolutionName = "4k";
                }
                else if (width >= 2560 && width < 3840)
                {
                    resolutionName = "1440p";
                }
                else if (width >= 1920 && width < 2560)
                {
                    resolutionName = "1080" + scanType;
                }
                else if (width >= 1280 && width < 1920)
                {
                    resolutionName = "720" + scanType;
                }
                else if (width < 1280)
                {
                    if (height >= 576)
                    {
                        resolutionName = "576" + scanType;
                    }
                    else if (height >= 480)
                    {
                        resolutionName = "480" + scanType;
                    }
                    else if (height >= 360)
                    {
                        resolutionName = "360" + scanType;
                    }
                }

                if (resolutionName != null)
                {
                    return resolutionName;
                }
            }

            return null;
        }

        public static string? GetVideoFormatIconName(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            bool hasDV = false;
            bool hasHDR10Plus = false;
            bool hasHDR = false;

            if (videoStream?.VideoRange != null)
            {
                var videoRange = videoStream.VideoRange.ToLowerInvariant();

                if (videoRange.Contains("dolby") || videoRange.Contains("dv"))
                {
                    hasDV = true;
                }
                if (videoRange.Contains("hdr10+"))
                {
                    hasHDR10Plus = true;
                }
                if (videoRange.Contains("hdr"))
                {
                    hasHDR = true;
                }
            }

            var title = ((item.Path ?? "") + " " + (item.Name ?? "")).ToLowerInvariant();

            if (title.Contains("dolby vision") || title.Contains("dolbyvision"))
            {
                hasDV = true;
            }

            if (title.Contains("hdr10+") || title.Contains("hdr10plus"))
            {
                hasHDR10Plus = true;
            }

            if (hasHDR10Plus || title.Contains("hdr"))
            {
                hasHDR = true;
            }

            if (hasDV) return "dv";
            if (hasHDR10Plus) return "hdr10plus";
            if (hasHDR) return "hdr";

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
            parts.Add(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio).Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l)));
            parts.Add(string.Join(",", streams.Where(s => s.Type == MediaStreamType.Subtitle).Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l)));
            var audioCodecs = streams.Where(s => s.Type == MediaStreamType.Audio).Select(GetAudioCodecIconName).Where(c => c != null).Select(c => c!).Distinct().OrderBy(c => c);
            parts.Add(string.Join(",", audioCodecs));
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            parts.Add(videoStream != null ? GetVideoCodecIconName(videoStream) ?? "none" : "none");
            var audioStreamForChannels = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
            parts.Add(audioStreamForChannels != null ? GetChannelIconName(audioStreamForChannels) ?? "none" : "none");
            parts.Add(videoStream != null ? GetAspectRatioIconName(videoStream, true) ?? "none" : "none");
            parts.Add(item.DateModified.Ticks.ToString());

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var input = string.Join("|", parts);
                var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string GetItemMediaStreamHashV2(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            var parts = new List<string>(8);

            var audioLangs = streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage))
                                    .Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage))
                                    .OrderBy(l => l);
            parts.Add(string.Join(",", audioLangs));

            var subLangs = streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage))
                                  .Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage))
                                  .OrderBy(l => l);
            parts.Add(string.Join(",", subLangs));

            var audioCodecs = streams.Where(s => s.Type == MediaStreamType.Audio)
                                     .Select(GetAudioCodecIconName)
                                     .Where(c => c != null)
                                     .Select(c => c!)
                                     .Distinct()
                                     .OrderBy(c => c);
            parts.Add(string.Join(",", audioCodecs));

            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            parts.Add(videoStream != null ? (GetVideoCodecIconName(videoStream) ?? "none") : "none");

            var audioStreamForChannels = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
            parts.Add(audioStreamForChannels != null ? (GetChannelIconName(audioStreamForChannels) ?? "none") : "none");

            var aspect = videoStream != null ? (GetAspectRatioIconName(videoStream, true) ?? "none") : "none";
            parts.Add(aspect);

            parts.Add(item.DateModified.Ticks.ToString());

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var input = string.Join("|", parts);
                var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}