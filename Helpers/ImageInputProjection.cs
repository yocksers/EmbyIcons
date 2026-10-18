using EmbyIcons.Configuration;
using EmbyIcons.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EmbyIcons.Helpers
{
    // Only loaded scalar metadata is read by Light(). Tags, streams and relationships
    // are inspected by the renderer/background worker, never by an image-tag callback.
    internal sealed class ImageInputProjection
    {
        internal readonly string Id;
        internal readonly ProfileSettings Settings;
        internal readonly bool Heavy;
        internal readonly bool Children;

        internal ImageInputProjection(BaseItem item, ProfileSettings settings)
        {
            Settings = CopySettings(settings);
            Children = ((item is Series || item is Season || item is BoxSet) && OverlayRequirements.VideoChildren(item, settings)) ||
                ((item is MusicAlbum || item is MusicArtist) && settings.EnableMusicAlbumAggregation && OverlayRequirements.MusicChildren(item, settings));
            Heavy = item is BoxSet || Children || (item is Movie && OverlayRequirements.MoviePaths(settings)) ||
                settings.TagIconAlignment != IconAlignment.Disabled || settings.TagBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled) ||
                (!(item is Series || item is Season || item is BoxSet ||
                   ((item is MusicAlbum || item is MusicArtist) && settings.EnableMusicAlbumAggregation)) &&
                 (OverlayRequirements.VideoChildren(item, settings) || OverlayRequirements.MusicChildren(item, settings) ||
                  settings.FrameRateIconAlignment != IconAlignment.Disabled || settings.SourceIconAlignment != IconAlignment.Disabled || settings.OriginalLanguageIconAlignment != IconAlignment.Disabled));
            Id = Key(item, settings);
        }

        private static ProfileSettings CopySettings(ProfileSettings source)
        {
            var result = new ProfileSettings();
            foreach (var property in typeof(ProfileSettings).GetProperties())
                if (property.CanRead && property.CanWrite) property.SetValue(result, property.GetValue(source));
            result.FilenameBasedIcons = source.FilenameBasedIcons.Select(Clone).ToList();
            result.TagBasedIcons = source.TagBasedIcons.Select(Clone).ToList();
            return result;
        }

        private static T Clone<T>(T source) where T : new()
        {
            var result = new T();
            foreach (var property in typeof(T).GetProperties())
                if (property.CanRead && property.CanWrite) property.SetValue(result, property.GetValue(source));
            return result;
        }

        internal static string Kind(BaseItem item) => item is Episode ? nameof(Episode) : item is Movie ? nameof(Movie) : item is Video ? nameof(Video) :
            item is Series ? nameof(Series) : item is Season ? nameof(Season) : item is BoxSet ? nameof(BoxSet) :
            item is MusicAlbum ? nameof(MusicAlbum) : item is MusicArtist ? nameof(MusicArtist) : item is Photo ? nameof(Photo) : nameof(Audio);

        internal static string Key(BaseItem item, ProfileSettings s)
        {
            // A small, explicit projection: no reflection or JSON in the hot path.
            var b = new StringBuilder(Kind(item)).Append('|');
            foreach (var a in new[] { s.CommunityScoreIconAlignment, s.RottenTomatoesScoreIconAlignment,
                s.ParentalRatingIconAlignment, s.SeriesStatusIconAlignment, s.PopcornScoreIconAlignment,
                s.MyAnimeListScoreIconAlignment, s.AudioIconAlignment, s.SubtitleIconAlignment,
                s.ChannelIconAlignment, s.AudioCodecIconAlignment, s.VideoCodecIconAlignment,
                s.VideoFormatIconAlignment, s.ResolutionIconAlignment, s.AspectRatioIconAlignment,
                s.FrameRateIconAlignment, s.SourceIconAlignment, s.OriginalLanguageIconAlignment, s.SampleRateIconAlignment,
                s.AudioBitRateIconAlignment, s.BitDepthIconAlignment, s.TagIconAlignment })
                b.Append(a != IconAlignment.Disabled ? '1' : '0');
            b.Append('|').Append(s.UseSeriesLiteMode).Append('|').Append(s.UseCollectionLiteMode)
                .Append('|').Append(s.ShowSeriesIconsIfAllEpisodesHaveLanguage)
                .Append('|').Append(s.ShowCollectionIconsIfAllChildrenHaveLanguage)
                .Append('|').Append(s.ExcludeSpecialsFromSeriesAggregation)
                .Append('|').Append(s.EnableMusicAlbumAggregation).Append('|').Append(s.UseMusicAlbumLiteMode);
            foreach (var m in s.FilenameBasedIcons.Where(m => m.IconAlignment != IconAlignment.Disabled))
                b.Append('|').Append(m.Keyword).Append(':').Append(m.IconName).Append(':')
                    .Append(m.ApplyToMovies).Append(m.ApplyToSeries).Append(m.ApplyToSeasons).Append(m.ApplyToEpisodes)
                    .Append(m.ApplyToAlbums).Append(m.ApplyToTracks).Append(m.ApplyToArtists);
            foreach (var m in s.TagBasedIcons.Where(m => m.IconAlignment != IconAlignment.Disabled))
                b.Append('|').Append(m.TagName).Append(':').Append(m.ApplyToMovies).Append(m.ApplyToEpisodes)
                    .Append(m.ApplyToAlbums).Append(m.ApplyToTracks).Append(m.ApplyToArtists);
            return Hash(b.ToString());
        }

        internal string Light(BaseItem item)
        {
            var s = Settings;
            var b = new StringBuilder();
            if (s.CommunityScoreIconAlignment != IconAlignment.Disabled) b.Append("c=").Append(item.CommunityRating?.ToString("R", CultureInfo.InvariantCulture));
            if (s.RottenTomatoesScoreIconAlignment != IconAlignment.Disabled) b.Append("|r=").Append(item.CriticRating?.ToString("R", CultureInfo.InvariantCulture));
            if (s.ParentalRatingIconAlignment != IconAlignment.Disabled) b.Append("|p=").Append(item.OfficialRating);
            if (s.SeriesStatusIconAlignment != IconAlignment.Disabled && item is Series series) b.Append("|s=").Append(series.Status);
            if (s.PopcornScoreIconAlignment != IconAlignment.Disabled || s.MyAnimeListScoreIconAlignment != IconAlignment.Disabled ||
                (s.CommunityScoreIconAlignment != IconAlignment.Disabled && !item.CommunityRating.HasValue) ||
                (s.RottenTomatoesScoreIconAlignment != IconAlignment.Disabled && !item.CriticRating.HasValue))
                foreach (var pair in item.ProviderIds.OrderBy(p => p.Key, StringComparer.Ordinal))
                    b.Append('|').Append(pair.Key).Append('=').Append(pair.Value);
            return Hash(b.ToString());
        }

        internal string Overlay(OverlayData source)
        {
            var s = Settings;
            var data = new OverlayData();
            if (s.AudioIconAlignment != IconAlignment.Disabled) data.AudioLanguages = source.AudioLanguages;
            if (s.SubtitleIconAlignment != IconAlignment.Disabled) data.SubtitleLanguages = source.SubtitleLanguages;
            if (s.AudioCodecIconAlignment != IconAlignment.Disabled) data.AudioCodecs = source.AudioCodecs;
            if (s.VideoCodecIconAlignment != IconAlignment.Disabled) data.VideoCodecs = source.VideoCodecs;
            if (s.ChannelIconAlignment != IconAlignment.Disabled) data.ChannelIconName = source.ChannelIconName;
            if (s.VideoFormatIconAlignment != IconAlignment.Disabled) data.VideoFormatIconName = source.VideoFormatIconName;
            if (s.ResolutionIconAlignment != IconAlignment.Disabled) data.ResolutionIconName = source.ResolutionIconName;
            if (s.AspectRatioIconAlignment != IconAlignment.Disabled) data.AspectRatioIconName = source.AspectRatioIconName;
            if (s.FrameRateIconAlignment != IconAlignment.Disabled) data.FrameRateIconName = source.FrameRateIconName;
            if (s.OriginalLanguageIconAlignment != IconAlignment.Disabled) data.OriginalLanguageIconName = source.OriginalLanguageIconName;
            if (s.SampleRateIconAlignment != IconAlignment.Disabled) data.SampleRateIconName = source.SampleRateIconName;
            if (s.AudioBitRateIconAlignment != IconAlignment.Disabled) data.AudioBitRateIconName = source.AudioBitRateIconName;
            if (s.BitDepthIconAlignment != IconAlignment.Disabled) data.BitDepthIconName = source.BitDepthIconName;
            if (s.TagIconAlignment != IconAlignment.Disabled) data.Tags = source.Tags;
            if (s.SourceIconAlignment != IconAlignment.Disabled) data.SourceIcons = source.SourceIcons;
            data.FilenameBasedIcons = source.FilenameBasedIcons.Select(i => i.IconName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new FilenameBasedIconData { IconName = n }).ToList();
            data.TagBasedIcons = source.TagBasedIcons.Select(i => i.IconName).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new FilenameBasedIconData { IconName = n }).ToList();
            // Scalar ratings are compared by Light(). External ratings have their own
            // refresh interval; favorites are invalidated by Emby's user-data event.
            return OverlayDataKey.Compute(data);
        }

        internal static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 16).Replace("-", string.Empty);
        }
    }
}
