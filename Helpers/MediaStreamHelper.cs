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

                // Normalize common variants
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

        public static string? GetAspectRatioIconName(MediaStream? videoStream)
        {
            if (videoStream == null) return null;

            if (!string.IsNullOrWhiteSpace(videoStream.AspectRatio))
            {
                var ar = videoStream.AspectRatio.Trim().ToLowerInvariant();
                ar = ar.Replace(':', 'x');
                return ar;
            }

            if (videoStream.Width.HasValue && videoStream.Height.HasValue && videoStream.Height.Value != 0)
            {
                var ratio = (double)videoStream.Width.Value / videoStream.Height.Value;
                // Snap to common ratios
                var pairs = new (double v, string name)[] {
                    (16.0/9.0, "16x9"),
                    (4.0/3.0, "4x3"),
                    (21.0/9.0, "21x9"),
                };
                var best = pairs.OrderBy(p => Math.Abs(p.v - ratio)).First();
                if (Math.Abs(best.v - ratio) < 0.03) return best.name;
            }
            return null;
        }

        public static string? GetResolutionIconNameFromStream(MediaStream? videoStream, IList<string> knownKeys)
        {
            if (videoStream == null) return null;

            // First try resolution by dimensions
            if (videoStream.Height.HasValue)
            {
                var h = videoStream.Height.Value;
                if (h >= 2000) return knownKeys.FirstOrDefault(k => k.Equals("4k", StringComparison.OrdinalIgnoreCase) || k.Equals("2160p", StringComparison.OrdinalIgnoreCase)) ?? "4k";
                if (h >= 1000) return knownKeys.FirstOrDefault(k => k.Equals("1080p", StringComparison.OrdinalIgnoreCase) || k.Equals("1080i", StringComparison.OrdinalIgnoreCase)) ?? "1080p";
                if (h >= 700) return knownKeys.FirstOrDefault(k => k.Equals("720p", StringComparison.OrdinalIgnoreCase)) ?? "720p";
                if (h >= 500) return knownKeys.FirstOrDefault(k => k.Equals("576p", StringComparison.OrdinalIgnoreCase)) ?? "576p";
                if (h >= 400) return knownKeys.FirstOrDefault(k => k.Equals("480p", StringComparison.OrdinalIgnoreCase)) ?? "480p";
            }

            // Fallback to DisplayTitle scanning if present
            var title = (videoStream.DisplayTitle ?? "").ToLowerInvariant();
            foreach (var key in knownKeys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (title.Contains(key)) return key;
            }

            return null;
        }

        public static string? GetVideoFormatIconName(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);

            bool hasDV = false;
            bool hasHDR10Plus = false;
            bool hasHDR = false;

            // --- Step 1: Check structured VideoRange property ---
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

            // --- Step 2: Check filename and title. This will augment the findings. ---
            var title = ((item.Path ?? "") + " " + (item.Name ?? "")).ToLowerInvariant();

            if (title.Contains("dolby vision") || title.Contains("dolbyvision"))
            {
                hasDV = true;
            }

            // Per user request, check for "hdr10+" and "hdr10plus" in the filename.
            if (title.Contains("hdr10+") || title.Contains("hdr10plus"))
            {
                hasHDR10Plus = true;
            }

            // If we found HDR10+ or the filename contains a generic HDR tag, set the HDR flag.
            if (hasHDR10Plus || title.Contains("hdr"))
            {
                hasHDR = true;
            }

            // --- Step 3: Apply priority to determine the final icon ---
            // The hierarchy is generally DV > HDR10+ > HDR.
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

        // NOTE: legacy hash kept for backwards-compat callers elsewhere in the codebase.
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
            parts.Add(videoStream != null ? GetAspectRatioIconName(videoStream) ?? "none" : "none");
            parts.Add(item.DateModified.Ticks.ToString());

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var input = string.Join("|", parts);
                var hashBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        // NEW: DisplayLanguage-based hash to align with overlay logic and avoid cache misses.
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

            var aspect = videoStream != null ? (GetAspectRatioIconName(videoStream) ?? "none") : "none";
            parts.Add(aspect);

            // Keep date modified to force refresh when metadata changes
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