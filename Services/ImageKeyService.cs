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
            internal string Ratings = string.Empty;
            internal readonly Dictionary<(Guid, DrawableContent), string> Keys = new Dictionary<(Guid, DrawableContent), string>();
            internal readonly Dictionary<DrawableContent, string> Fallbacks = new Dictionary<DrawableContent, string>();
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
            snapshot.Ratings = Hash(snapshot.Options.MDBListApiKey ?? string.Empty);
            BaseItem[] kinds = { new Movie(), new Series(), new BoxSet(), new Audio() };
            foreach (var profile in snapshot.Options.Profiles)
                foreach (var item in kinds)
                    foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner })
                    {
                        var content = OverlayRequirements.Content(item, type, snapshot.Options);
                        snapshot.Keys[(profile.Id, content)] = RenderSettingsKey.Compute(profile.Settings, snapshot.Options, content);
                    }
            // An item whose profile is not known yet may use any profile. Only the settings that can be
            // drawn for its kind of image belong in the key, so unrelated settings do not redraw it.
            foreach (var content in snapshot.Keys.Keys.Select(k => k.Item2).Distinct())
                snapshot.Fallbacks[content] = Hash(snapshot.Options.PersistedVersion + "|" + string.Join("|",
                    snapshot.Options.Profiles.Select(p => p.Id.ToString("N") + "=" + snapshot.Keys[(p.Id, content)])));
            _snapshot = snapshot;
        }

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
            var content = OverlayRequirements.Content(item, type, snapshot.Options);
            if (selected == null || !snapshot.Keys.TryGetValue((selected.Id, content), out var settings))
                return UnresolvedKey(snapshot, item, type, identity, content);
            // A collection first drawn before its profile was known keeps that key until something it covers
            // changes. It already includes every profile, and switching would draw the collection twice.
            if (item is BoxSet && _state.TryGetCollectionStamp(item.InternalId, out var stamp) && stamp == CollectionStamp(item))
                return UnresolvedKey(snapshot, item, type, identity, content);
            var key = "ei11_" + identity + "_" + (int)type + "_p" + selected.Id.ToString("N") +
                "_v" + snapshot.Options.PersistedVersion + "_s" + settings + "_d" + _state.GetDataVersion(item, selected.Settings);
            if ((selected.Settings.PopcornScoreIconAlignment != IconAlignment.Disabled || selected.Settings.MyAnimeListScoreIconAlignment != IconAlignment.Disabled) &&
                !string.IsNullOrWhiteSpace(snapshot.Options.MDBListApiKey))
                key += "_r" + snapshot.Ratings + "_" + MDBListService.GetImageVersion(item);
            return key;
        }

        // A collection that has never been drawn has no known profile yet; resolving it belongs to
        // rendering. Include every possible dependency until its profile is known.
        private string UnresolvedKey(Snapshot snapshot, BaseItem item, ImageType type, string identity, DrawableContent content)
        {
            snapshot.Fallbacks.TryGetValue(content, out var fallback);
            return "ei11_" + identity + "_" + (int)type + "_u" + fallback + "_" + UnresolvedDependencies(snapshot, item);
        }

        private string UnresolvedDependencies(Snapshot snapshot, BaseItem item) =>
            string.Join(";", snapshot.Options.Profiles.Select(p => _state.GetDataVersion(item, p.Settings)).Distinct()) +
            "_r" + snapshot.Ratings + "_" + MDBListService.GetImageVersion(item);

        // Everything the unresolved keys of a collection depend on, for every image type.
        internal string CollectionStamp(BaseItem item)
        {
            var snapshot = _snapshot;
            var fallbacks = new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner }
                .Select(type => snapshot.Fallbacks.TryGetValue(OverlayRequirements.Content(item, type, snapshot.Options), out var fallback) ? fallback : string.Empty);
            return Hash(string.Join(",", fallbacks) + "_" + UnresolvedDependencies(snapshot, item));
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 16).Replace("-", string.Empty);
        }
    }
}
