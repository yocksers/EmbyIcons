using EmbyIcons.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbyIcons.Helpers
{
    public static class MediaStreamHelper
    {
        private static readonly ConcurrentDictionary<(Type, string), System.Reflection.PropertyInfo?> _propertyCache = new ConcurrentDictionary<(Type, string), System.Reflection.PropertyInfo?>();

        private static System.Reflection.PropertyInfo? GetCachedProperty(Type type, string propertyName)
        {
            return _propertyCache.GetOrAdd((type, propertyName), key => key.Item1.GetProperty(key.Item2));
        }

        public static string? GetAudioCodecIconName(MediaStream stream)
        {
            try
            {
                if (stream == null) return null;
                var codec = stream.Codec?.Trim();
                if (string.IsNullOrEmpty(codec)) return null;

                var lowerCodec = codec.ToLowerInvariant();
                var displayTitle = (stream.DisplayTitle ?? "").ToLowerInvariant();
                var profile = (stream.Profile ?? "").ToLowerInvariant();
                
                if (lowerCodec.Contains("dts") || displayTitle.Contains("dts") || profile.Contains("dts"))
                {
                    if (displayTitle.Contains("dts:x") || profile.Contains("dts:x") || lowerCodec.Contains("dts:x") || 
                        displayTitle.Contains("dtsx") || profile.Contains("dtsx") || lowerCodec.Contains("dtsx"))
                    {
                        return "dtsx";
                    }
                    if (displayTitle.Contains("dts-hd ma") || profile.Contains("dts-hd ma") || lowerCodec.Contains("dts-hd ma") ||
                        displayTitle.Contains("dts-hd master") || profile.Contains("dts-hd master") ||
                        displayTitle.Contains("dts-hdma") || profile.Contains("dts-hdma") || lowerCodec.Contains("dts-hdma") ||
                        displayTitle.Contains("dts ma") || profile.Contains("dts ma"))
                    {
                        return "dts-hdma";
                    }
                    if (displayTitle.Contains("dts-hd hra") || profile.Contains("dts-hd hra") || lowerCodec.Contains("dts-hd hra") ||
                        displayTitle.Contains("dts-hra") || profile.Contains("dts-hra") || lowerCodec.Contains("dts-hra") ||
                        displayTitle.Contains("dts-hd hr") || profile.Contains("dts-hd hr") || lowerCodec.Contains("dts-hd hr"))
                    {
                        return "dts-hra";
                    }
                    return "dts";
                }
                
                return lowerCodec switch
                {
                    var c when c.Contains("e-ac-3") => c.Replace("e-ac-3", "eac3"),
                    var c when c.Contains("ac-3") => c.Replace("ac-3", "ac3"),
                    _ => lowerCodec
                };
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
                var codec = stream.Codec?.Trim();
                if (string.IsNullOrEmpty(codec)) return null;

                var lowerCodec = codec.ToLowerInvariant();
                
                return lowerCodec switch
                {
                    "h265" or "x265" => "hevc",
                    "h264" or "x264" => "h264",
                    _ => lowerCodec
                };
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
            if (!snapToCommon || height <= 0 || width <= 0) return null;

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

        public static string? GetResolutionIconNameFromStream(MediaStream? videoStream, IList<string> knownKeys, BaseItem? item = null)
        {
            if (videoStream == null) return null;

            var title = (videoStream.DisplayTitle ?? "").ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(title))
            {
                foreach (var key in knownKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key) && title.Contains(key.ToLowerInvariant()))
                    {
                        return key;
                    }
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
                var videoRange = videoStream.VideoRange;
                
                if (videoRange.Contains(StringConstants.DolbyShort, StringComparison.OrdinalIgnoreCase) || 
                    videoRange.Contains(StringConstants.DVShort, StringComparison.OrdinalIgnoreCase))
                {
                    hasDV = true;
                }
                if (videoRange.Contains(StringConstants.HDR10Plus, StringComparison.OrdinalIgnoreCase))
                {
                    hasHDR10Plus = true;
                }
                if (videoRange.Contains(StringConstants.HDR, StringComparison.OrdinalIgnoreCase))
                {
                    hasHDR = true;
                }
            }

            if (!hasDV || !hasHDR10Plus)
            {
                var path = item.Path ?? string.Empty;
                var name = item.Name ?? string.Empty;
                
                if (!hasDV && (path.Contains(StringConstants.DolbyVision, StringComparison.OrdinalIgnoreCase) || 
                              path.Contains(StringConstants.DolbyVisionCompact, StringComparison.OrdinalIgnoreCase) ||
                              name.Contains(StringConstants.DolbyVision, StringComparison.OrdinalIgnoreCase) ||
                              name.Contains(StringConstants.DolbyVisionCompact, StringComparison.OrdinalIgnoreCase)))
                {
                    hasDV = true;
                }

                if (!hasHDR10Plus && (path.Contains(StringConstants.HDR10Plus, StringComparison.OrdinalIgnoreCase) || 
                                     path.Contains(StringConstants.HDR10PlusCompact, StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains(StringConstants.HDR10Plus, StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains(StringConstants.HDR10PlusCompact, StringComparison.OrdinalIgnoreCase)))
                {
                    hasHDR10Plus = true;
                    hasHDR = true;
                }
                else if (!hasHDR && (path.Contains(StringConstants.HDR, StringComparison.OrdinalIgnoreCase) || 
                                     name.Contains(StringConstants.HDR, StringComparison.OrdinalIgnoreCase)))
                {
                    hasHDR = true;
                }
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

        public static string? GetFrameRateIconName(MediaStream? videoStream, bool snapToCommon = true)
        {
            if (videoStream == null) return null;

            var fps = videoStream.RealFrameRate ?? videoStream.AverageFrameRate;
            if (!fps.HasValue || fps.Value <= 0) return null;

            var fpsValue = fps.Value;

            if (snapToCommon)
            {
                var tolerance = 0.01f;
                var commonRates = new[] { 23.976f, 24f, 25f, 29.97f, 30f, 50f, 59.94f, 60f, 120f };
                foreach (var rate in commonRates)
                {
                    if (Math.Abs(fpsValue - rate) < tolerance)
                    {
                        return rate.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }

            return fpsValue.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string GetItemMediaStreamHash(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var encoding = System.Text.Encoding.UTF8;
                var separator = encoding.GetBytes("|");

                void HashPart(string part)
                {
                    var bytes = encoding.GetBytes(part);
                    md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    md5.TransformBlock(separator, 0, separator.Length, null, 0);
                }

                var audioLangs = string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio).Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l));
                HashPart(audioLangs);

                var subLangs = string.Join(",", streams.Where(s => s.Type == MediaStreamType.Subtitle).Where(s => !string.IsNullOrEmpty(s.Language)).Select(s => LanguageHelper.NormalizeLangCode(s.Language)).OrderBy(l => l));
                HashPart(subLangs);

                var audioCodecs = string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio).Select(GetAudioCodecIconName).Where(c => c != null).Select(c => c!).Distinct().OrderBy(c => c));
                HashPart(audioCodecs);

                var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                HashPart(videoStream != null ? GetVideoCodecIconName(videoStream) ?? "none" : "none");

                var audioStreamForChannels = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
                HashPart(audioStreamForChannels != null ? GetChannelIconName(audioStreamForChannels) ?? "none" : "none");

                HashPart(videoStream != null ? GetAspectRatioIconName(videoStream, true) ?? "none" : "none");
                HashPart(item.DateModified.Ticks.ToString());

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string GetItemMediaStreamHashV2(BaseItem item, IReadOnlyList<MediaStream> streams)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var encoding = System.Text.Encoding.UTF8;
                var separator = encoding.GetBytes("|");

                void HashPart(string part)
                {
                    var bytes = encoding.GetBytes(part);
                    md5.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    md5.TransformBlock(separator, 0, separator.Length, null, 0);
                }

                var audioLangs = string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.DisplayLanguage))
                                    .Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage))
                                    .OrderBy(l => l));
                HashPart(audioLangs);

                var subLangs = string.Join(",", streams.Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.DisplayLanguage))
                                  .Select(s => LanguageHelper.NormalizeLangCode(s.DisplayLanguage))
                                  .OrderBy(l => l));
                HashPart(subLangs);

                var audioCodecs = string.Join(",", streams.Where(s => s.Type == MediaStreamType.Audio)
                                     .Select(GetAudioCodecIconName)
                                     .Where(c => c != null)
                                     .Select(c => c!)
                                     .Distinct()
                                     .OrderBy(c => c));
                HashPart(audioCodecs);

                var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                HashPart(videoStream != null ? (GetVideoCodecIconName(videoStream) ?? "none") : "none");

                var audioStreamForChannels = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.Channels ?? 0).FirstOrDefault();
                HashPart(audioStreamForChannels != null ? (GetChannelIconName(audioStreamForChannels) ?? "none") : "none");

                var aspect = videoStream != null ? (GetAspectRatioIconName(videoStream, true) ?? "none") : "none";
                HashPart(aspect);

                HashPart(item.DateModified.Ticks.ToString());

                md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string? GetSeriesStatusIconName(MediaBrowser.Controller.Entities.TV.Series? series)
        {
            if (series == null) return null;

            try
            {
                var statusProperty = GetCachedProperty(series.GetType(), "Status");
                if (statusProperty != null)
                {
                    var statusValue = statusProperty.GetValue(series)?.ToString();
                    if (!string.IsNullOrEmpty(statusValue))
                    {
                        // Check if the status indicates the series has ended
                        if (statusValue.IndexOf("Ended", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            statusValue.IndexOf("Canceled", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return "ended";
                        }
                        // Check if the status indicates the series is continuing/running
                        else if (statusValue.IndexOf("Continuing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 statusValue.IndexOf("Running", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return "running";
                        }
                    }
                }

                var endDateProperty = GetCachedProperty(series.GetType(), "EndDate");
                if (endDateProperty != null)
                {
                    var endDate = endDateProperty.GetValue(series) as DateTime?;
                    if (endDate.HasValue && endDate.Value < DateTime.Now)
                    {
                        return "ended";
                    }
                }

                // If we have a PremiereDate but no EndDate or status, assume it's running
                if (series.PremiereDate.HasValue)
                {
                    return "running";
                }
            }
            catch
            {
                // If we can't determine the status, return null
            }

            return null;
        }
    }
}