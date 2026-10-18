using EmbyIcons.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class OverlayRequirements
    {
        internal static bool VideoChildren(BaseItem item, ProfileSettings settings) =>
            settings.AudioIconAlignment != IconAlignment.Disabled ||
            settings.SubtitleIconAlignment != IconAlignment.Disabled ||
            settings.AudioCodecIconAlignment != IconAlignment.Disabled ||
            settings.VideoCodecIconAlignment != IconAlignment.Disabled ||
            settings.ChannelIconAlignment != IconAlignment.Disabled ||
            settings.AspectRatioIconAlignment != IconAlignment.Disabled ||
            settings.ResolutionIconAlignment != IconAlignment.Disabled ||
            settings.VideoFormatIconAlignment != IconAlignment.Disabled ||
            settings.FilenameBasedIcons.Any(m => Valid(m) &&
                ((item is Series && (m.ApplyToSeries || m.ApplyToEpisodes)) ||
                 (item is Season && (m.ApplyToSeasons || m.ApplyToEpisodes))));

        internal static bool MusicChildren(BaseItem item, ProfileSettings settings) =>
            settings.AudioIconAlignment != IconAlignment.Disabled ||
            settings.AudioCodecIconAlignment != IconAlignment.Disabled ||
            settings.ChannelIconAlignment != IconAlignment.Disabled ||
            settings.SampleRateIconAlignment != IconAlignment.Disabled ||
            settings.AudioBitRateIconAlignment != IconAlignment.Disabled ||
            settings.BitDepthIconAlignment != IconAlignment.Disabled ||
            settings.FilenameBasedIcons.Any(m => Valid(m) &&
                ((item is MusicAlbum && (m.ApplyToAlbums || m.ApplyToTracks)) ||
                 (item is MusicArtist && (m.ApplyToArtists || m.ApplyToAlbums || m.ApplyToTracks))));

        internal static string DataSignature(ProfileSettings settings, PluginOptions options) =>
            SettingsChangeAnalyzer.DataSignature(settings) + "|" + options.IconsFolder + "|" +
            (int)options.IconLoadingMode + "|" + options.PersistedVersion;

        internal static bool MoviePaths(ProfileSettings settings) =>
            settings.FilenameBasedIcons.Any(m => Valid(m) && m.ApplyToMovies);

        private static bool Valid(FilenameIconMapping mapping) =>
            mapping.IconAlignment != IconAlignment.Disabled &&
            !string.IsNullOrWhiteSpace(mapping.Keyword) && !string.IsNullOrWhiteSpace(mapping.IconName);

        internal static bool Supports(BaseItem item, ImageType type, ProfileSettings settings)
        {
            if (!(type == ImageType.Primary ? settings.EnableForPosters :
                  type == ImageType.Thumb ? settings.EnableForThumbs :
                  type == ImageType.Banner && settings.EnableForBanners)) return false;
            if (item is Episode && !settings.ShowOverlaysForEpisodes) return false;
            if (item is Season && !settings.ShowOverlaysForSeasons) return false;
            if (item is Series && !settings.UseSeriesLiteMode && !settings.ShowSeriesIconsIfAllEpisodesHaveLanguage) return false;
            if (item is BoxSet && !settings.UseCollectionLiteMode && !settings.ShowCollectionIconsIfAllChildrenHaveLanguage) return false;
            return settings.AudioIconAlignment != IconAlignment.Disabled ||
                settings.SubtitleIconAlignment != IconAlignment.Disabled ||
                settings.ChannelIconAlignment != IconAlignment.Disabled ||
                settings.VideoFormatIconAlignment != IconAlignment.Disabled ||
                settings.ResolutionIconAlignment != IconAlignment.Disabled ||
                settings.CommunityScoreIconAlignment != IconAlignment.Disabled ||
                settings.AudioCodecIconAlignment != IconAlignment.Disabled ||
                settings.VideoCodecIconAlignment != IconAlignment.Disabled ||
                settings.TagIconAlignment != IconAlignment.Disabled ||
                settings.AspectRatioIconAlignment != IconAlignment.Disabled ||
                settings.ParentalRatingIconAlignment != IconAlignment.Disabled ||
                settings.SourceIconAlignment != IconAlignment.Disabled ||
                settings.FavoriteCountIconAlignment != IconAlignment.Disabled ||
                settings.FrameRateIconAlignment != IconAlignment.Disabled ||
                settings.OriginalLanguageIconAlignment != IconAlignment.Disabled ||
                settings.SeriesStatusIconAlignment != IconAlignment.Disabled ||
                settings.RottenTomatoesScoreIconAlignment != IconAlignment.Disabled ||
                settings.PopcornScoreIconAlignment != IconAlignment.Disabled ||
                settings.MyAnimeListScoreIconAlignment != IconAlignment.Disabled ||
                settings.SampleRateIconAlignment != IconAlignment.Disabled ||
                settings.AudioBitRateIconAlignment != IconAlignment.Disabled ||
                settings.BitDepthIconAlignment != IconAlignment.Disabled ||
                settings.FilenameBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled) ||
                settings.TagBasedIcons.Any(m => m.IconAlignment != IconAlignment.Disabled);
        }

        internal static DrawableContent Content(BaseItem item, ImageType type, PluginOptions options)
        {
            var content = DrawableContent.AudioStreams | DrawableContent.VideoStreams |
                DrawableContent.Subtitle | DrawableContent.Tags | DrawableContent.ParentalRating | DrawableContent.SeriesStatus;
            if (item is Audio || item is MusicAlbum || item is MusicArtist)
            {
                content |= DrawableContent.Music;
                if (type == ImageType.Primary) content |= DrawableContent.MusicPoster;
            }
            else content |= type == ImageType.Primary ? DrawableContent.PosterImage :
                type == ImageType.Thumb ? DrawableContent.ThumbImage : DrawableContent.BannerImage;
            if (item is Series || item is Season) content |= DrawableContent.TvShow;
            if (item is BoxSet) content |= DrawableContent.Collection;
            if (!string.IsNullOrWhiteSpace(options.MDBListApiKey)) content |= DrawableContent.MdbListRatings;
            return content;
        }
    }
}
