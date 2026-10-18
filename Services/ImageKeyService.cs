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
using System.Text.Json;

namespace EmbyIcons.Services
{
    internal sealed class ImageKeyService
    {
        private sealed class Snapshot
        {
            internal PluginOptions Options = null!;
            internal ProfileSettings Union = null!;
            internal string LegacyFallback = string.Empty;
            internal string Ratings = string.Empty;
            internal readonly Dictionary<(Guid, DrawableContent), string> Legacy = new Dictionary<(Guid, DrawableContent), string>();
            internal readonly Dictionary<(Guid, string, ImageType), string> Signatures = new Dictionary<(Guid, string, ImageType), string>();
        }
        private volatile Snapshot _snapshot = null!;
        private readonly ImageCacheState _state;
        internal ImageKeyService(PluginOptions options, ImageCacheState state) { _state = state; Configure(options); }

        internal void Configure(PluginOptions options)
        {
            // Configuration serialization is a startup/settings operation, never a tag callback.
            var json = JsonSerializer.Serialize(options);
            var snapshot = new Snapshot { Options = JsonSerializer.Deserialize<PluginOptions>(json)!, LegacyFallback = ImageInputProjection.Hash(json) };
            snapshot.Ratings = ImageInputProjection.Hash(snapshot.Options.MDBListApiKey ?? string.Empty);
            snapshot.Union = Union(snapshot.Options.Profiles.Select(p => p.Settings));
            BaseItem[] kinds = { new Movie(), new Series(), new Season(), new Episode(), new BoxSet(), new Audio(), new MusicAlbum(), new MusicArtist(), new Photo(), new Video() };
            var projections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in kinds)
            {
                projections.Add(_state.Projection(item, snapshot.Union).Id);
                foreach (var profile in snapshot.Options.Profiles)
                {
                    projections.Add(_state.Projection(item, profile.Settings).Id);
                    foreach (var type in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner })
                    {
                        var content = OverlayRequirements.Content(item, type, snapshot.Options);
                        snapshot.Legacy[(profile.Id, content)] = RenderSettingsKey.Compute(profile.Settings, snapshot.Options, content);
                        snapshot.Signatures[(profile.Id, Family(item), type)] = RenderSettingsKey.ComputeStable(profile.Settings, snapshot.Options, content, item, type);
                    }
                }
            }
            _snapshot = snapshot;
            _state.UseProjections(projections);
            _state.RequestVerification();
        }

        internal bool NeedsVerification => _snapshot.Options.Profiles.Count > 0;

        private IconProfile? Effective(Snapshot snapshot, BaseItem item, ImageType type, IconProfile? profile, bool resolved)
        {
            var id = resolved ? profile?.Id : _state.KnownProfile(item, (int)type);
            return id.HasValue ? snapshot.Options.Profiles.FirstOrDefault(p => p.Id == id.Value) : null;
        }

        internal bool Supports(BaseItem item, ImageType type, IconProfile? profile, bool resolved)
        {
            var snapshot = _snapshot;
            var selected = Effective(snapshot, item, type, profile, resolved);
            if (selected != null) return OverlayRequirements.Supports(item, type, selected.Settings);
            if (resolved || _state.KnownProfile(item, (int)type) == Guid.Empty) return false;
            return snapshot.Options.Profiles.Any(p => OverlayRequirements.Supports(item, type, p.Settings));
        }

        internal string GetKey(BaseItem item, ImageType type, IconProfile? profile, bool resolved)
        {
            var snapshot = _snapshot;
            IconProfile? selected = null;
            string family = Family(item);
            var candidates = snapshot.Options.Profiles.ToDictionary(p => p.Id, p => snapshot.Signatures[(p.Id, family, type)]);
            return _state.WithBinding(item, (int)type, old =>
            {
                var selectedId = resolved ? profile?.Id : old?.ProfileId;
                selected = snapshot.Options.Profiles.FirstOrDefault(p => p.Id == selectedId);
                var settings = selected?.Settings ?? snapshot.Union;
                string projection = _state.Projection(item, settings).Id;
                string rawData = _state.GetDataVersion(item, settings);
                if (old == null)
                {
                    return new ImageCacheState.KeyBinding
                    {
                        Fallback = selected == null || item is BoxSet,
                        ProfileId = selected?.Id,
                        OriginalProfileId = selected?.Id ?? Guid.Empty,
                        Token = selected == null || item is BoxSet ? snapshot.LegacyFallback : snapshot.Legacy[(selected.Id, OverlayRequirements.Content(item, type, snapshot.Options))],
                        Fingerprints = candidates,
                        RatingHash = snapshot.Ratings,
                        RatingVersion = MDBListService.GetImageVersion(item),
                        DataProjection = projection, DataBaseline = rawData, DataAlias = rawData
                    };
                }
                Guid? nextId = resolved ? profile?.Id ?? Guid.Empty : old.ProfileId;
                bool newResolution = !old.ProfileId.HasValue && nextId.HasValue;
                bool profileChanged = old.ProfileId.HasValue && nextId.HasValue && old.ProfileId != nextId;
                bool settingsChanged;
                if (selected != null)
                    settingsChanged = !old.Fingerprints.TryGetValue(selected.Id, out var previous) || previous != candidates[selected.Id];
                else settingsChanged = old.Fingerprints.Count != candidates.Count || candidates.Any(p => !old.Fingerprints.TryGetValue(p.Key, out var previous) || previous != p.Value);
                bool ratings = UsesExternalRatings(selected?.Settings ?? snapshot.Union, snapshot.Options);
                long interval = ratings ? MDBListService.GetImageVersion(item) : old.RatingVersion;
                string ratingHash = ratings ? snapshot.Ratings : old.RatingHash;
                if (!newResolution && !profileChanged && !settingsChanged && interval == old.RatingVersion && ratingHash == old.RatingHash && old.DataProjection == projection) return old;
                var binding = old.Copy();
                binding.ProfileId = nextId;
                if (old.DataProjection != projection)
                {
                    string previousData = _state.ProjectionVersion(item, old.DataProjection);
                    string previousOutput = previousData == old.DataBaseline ? old.DataAlias : previousData;
                    if (!newResolution && !profileChanged && !settingsChanged)
                    {
                        _state.TransferProjection(item, old.DataProjection, projection);
                        rawData = _state.GetDataVersion(item, settings);
                    }
                    binding.DataProjection = projection;
                    binding.DataBaseline = rawData;
                    binding.DataAlias = !profileChanged && !settingsChanged ? previousOutput : rawData;
                }
                if (profileChanged || settingsChanged)
                    binding.Token = selected == null ? ImageInputProjection.Hash(old.Token + "|" + string.Join("|", candidates.OrderBy(p => p.Key).Select(p => p.Key.ToString("N") + p.Value))) :
                        ImageInputProjection.Hash(old.Token + "|" + selected.Id.ToString("N") + candidates[selected.Id]);
                binding.Fingerprints = selected == null ? candidates : new Dictionary<Guid, string> { [selected.Id] = candidates[selected.Id] };
                binding.RatingHash = ratingHash;
                binding.RatingVersion = interval;
                return binding;
            }, binding =>
            {
                var bound = binding.ProfileId.HasValue ? snapshot.Options.Profiles.FirstOrDefault(p => p.Id == binding.ProfileId.Value) : null;
                var settings = bound?.Settings ?? snapshot.Union;
                string identity = item.InternalId > 0 ? "i" + item.InternalId : "g" + item.Id.ToString("N");
                string rawData = _state.ProjectionVersion(item, binding.DataProjection);
                string data = rawData == binding.DataBaseline ? binding.DataAlias : rawData;
                if (binding.Fallback)
                    return "ei11_" + identity + "_" + (int)type + "_u" + binding.Token + "_" + data + "_r" + binding.RatingHash + "_" + binding.RatingVersion;
                var key = "ei11_" + identity + "_" + (int)type + "_p" + binding.OriginalProfileId.ToString("N") +
                    "_v" + snapshot.Options.PersistedVersion + "_s" + binding.Token + "_d" + data;
                if (UsesExternalRatings(settings, snapshot.Options)) key += "_r" + snapshot.Ratings + "_" + MDBListService.GetImageVersion(item);
                return key;
            });
        }

        internal void Bind(BaseItem item, IconProfile? profile)
        {
            foreach (int image in _state.BoundImageTypes(item)) GetKey(item, (ImageType)image, profile, true);
        }

        private static string Family(BaseItem item) => ImageInputProjection.Kind(item);

        private static bool UsesExternalRatings(ProfileSettings settings, PluginOptions options) =>
            !string.IsNullOrWhiteSpace(options.MDBListApiKey) &&
            (settings.PopcornScoreIconAlignment != IconAlignment.Disabled || settings.MyAnimeListScoreIconAlignment != IconAlignment.Disabled);

        private static ProfileSettings Union(IEnumerable<ProfileSettings> source)
        {
            var profiles = source.ToArray();
            var result = new ProfileSettings();
            foreach (var property in typeof(ProfileSettings).GetProperties())
            {
                if (property.PropertyType == typeof(IconAlignment))
                    property.SetValue(result, profiles.Select(p => (IconAlignment)property.GetValue(p)!).FirstOrDefault(a => a != IconAlignment.Disabled));
                else if (property.PropertyType == typeof(bool)) property.SetValue(result, profiles.Any(p => (bool)property.GetValue(p)!));
            }
            result.FilenameBasedIcons = profiles.SelectMany(p => p.FilenameBasedIcons).ToList();
            result.TagBasedIcons = profiles.SelectMany(p => p.TagBasedIcons).ToList();
            return result;
        }
    }
}
