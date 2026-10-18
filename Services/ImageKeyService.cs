using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyIcons.Services
{
    internal sealed class ImageKeyService
    {
        private sealed class Snapshot
        {
            internal PluginOptions Options = null!;
            internal string Fallback = string.Empty;
            internal string Ratings = string.Empty;
            internal bool NeedsVerification;
            internal readonly Dictionary<(Guid, DrawableContent), string> Keys = new Dictionary<(Guid, DrawableContent), string>();
        }

        private volatile Snapshot _snapshot = null!;
        private readonly ImageCacheState _state;

        internal ImageKeyService(PluginOptions options, ImageCacheState state)
        {
            _state = state;
            Configure(options);
        }

        internal void Configure(PluginOptions options)
        {
            // Serialize once when settings change, never while Emby builds a list of image tags.
            var json = JsonSerializer.Serialize(options);
            var snapshot = new Snapshot { Options = JsonSerializer.Deserialize<PluginOptions>(json)! };
            snapshot.Fallback = Hash(json);
            snapshot.Ratings = Hash(snapshot.Options.MDBListApiKey ?? string.Empty);
            BaseItem[] kinds = { new Movie(), new Series(), new BoxSet(), new Audio() };
            foreach (var profile in snapshot.Options.Profiles)
                foreach (var item in kinds)
                    foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner })
                    {
                        var content = OverlayRequirements.Content(item, type, snapshot.Options);
                        snapshot.Keys[(profile.Id, content)] = RenderSettingsKey.Compute(profile.Settings, snapshot.Options, content);
                    }
            BaseItem[] parents = { new Series(), new Season(), new BoxSet(), new MusicAlbum(), new MusicArtist(), new Movie() };
            snapshot.NeedsVerification = snapshot.Options.Profiles.Any(profile => parents.Any(item =>
                new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner }.Any(type => OverlayRequirements.Supports(item, type, profile.Settings)) &&
                ((item is Movie && OverlayRequirements.MoviePaths(profile.Settings)) ||
                 (item is BoxSet || ((item is Series || item is Season) && OverlayRequirements.VideoChildren(item, profile.Settings))) ||
                 ((item is MusicAlbum || item is MusicArtist) && profile.Settings.EnableMusicAlbumAggregation && OverlayRequirements.MusicChildren(item, profile.Settings)))));
            _snapshot = snapshot;
        }

        internal bool NeedsVerification => _snapshot.NeedsVerification;

        internal bool Supports(BaseItem item, ImageType type, IconProfile? profile, bool resolved)
        {
            var options = _snapshot.Options;
            var profiles = resolved ? options.Profiles.Where(p => p.Id == profile?.Id) : options.Profiles;
            return profiles.Any(p => OverlayRequirements.Supports(item, type, p.Settings));
        }

        internal string GetKey(BaseItem item, ImageType type, IconProfile? profile, bool resolved)
        {
            var snapshot = _snapshot;
            var selected = resolved ? snapshot.Options.Profiles.FirstOrDefault(p => p.Id == profile?.Id) : null;
            var identity = item.InternalId > 0 ? "i" + item.InternalId : "g" + item.Id.ToString("N");
            if (selected == null || item is BoxSet)
            {
                // A cold collection may not have a profile yet. Resolving it belongs to rendering.
                // Include every possible dependency until its profile is known.
                return "ei11_" + identity + "_" + (int)type + "_u" + snapshot.Fallback + "_" +
                    string.Join(";", snapshot.Options.Profiles.Select(p => _state.GetDataVersion(item, p.Settings)).Distinct()) +
                    "_r" + snapshot.Ratings + "_" + MDBListService.GetImageVersion(item);
            }
            var content = OverlayRequirements.Content(item, type, snapshot.Options);
            var settings = snapshot.Keys[(selected.Id, content)];
            var key = "ei11_" + identity + "_" + (int)type + "_p" + selected.Id.ToString("N") +
                "_v" + snapshot.Options.PersistedVersion + "_s" + settings + "_d" + _state.GetDataVersion(item, selected.Settings);
            if ((selected.Settings.PopcornScoreIconAlignment != IconAlignment.Disabled || selected.Settings.MyAnimeListScoreIconAlignment != IconAlignment.Disabled) &&
                !string.IsNullOrWhiteSpace(snapshot.Options.MDBListApiKey))
                key += "_r" + snapshot.Ratings + "_" + MDBListService.GetImageVersion(item);
            return key;
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 16).Replace("-", string.Empty);
        }
    }
}
