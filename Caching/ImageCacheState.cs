using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace EmbyIcons.Caching
{
    // Per-dependency revisions for images whose look depends on other items (series, seasons,
    // collections, albums, artists and multi-version movies). A library event bumps only the
    // parents it affects. Revisions are saved, so a clean restart keeps every image key and
    // nothing has to scan the library. GetDataVersion performs no I/O.
    internal sealed class ImageCacheState : IDisposable
    {
        private const int FormatVersion = 2;
        private const int MaxRevisions = 1000000;
        private const int MaxEpisodeParents = 500000;
        private const int MaxCollections = 100000;
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(30);

        private readonly object _gate = new object();
        private readonly object _saveGate = new object();
        private readonly ConcurrentDictionary<string, long> _revisions = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, CollectionProfile> _collectionProfiles = new ConcurrentDictionary<long, CollectionProfile>();
        private readonly Dictionary<long, long[]> _collectionMembers = new Dictionary<long, long[]>();
        private readonly Dictionary<long, HashSet<long>> _memberCollections = new Dictionary<long, HashSet<long>>();
        // Session-only: lets an episode that moves between seasons also refresh its old parents.
        private readonly Dictionary<long, (long Parent, long Series)> _episodeParents = new Dictionary<long, (long, long)>();
        private readonly string _path;
        private readonly string _pendingPath;
        private readonly ILogger _logger;
        private readonly Timer _saveTimer;
        private string _epoch = NewEpoch();
        private long _next;
        private long _profileGeneration;
        private bool _dirty;
        private int _disposed;

        internal ImageCacheState(string folder, ILogger logger)
        {
            _path = Path.Combine(folder, "image-revisions.json");
            _pendingPath = _path + ".pending";
            _logger = logger;
            _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            Load();
        }

        internal string Epoch => Volatile.Read(ref _epoch);

        internal string GetDataVersion(BaseItem item, ProfileSettings settings)
        {
            var version = item.DateLastSaved.UtcTicks + ":" + item.DateModified.UtcTicks;
            string? dependency = null;
            if (item is BoxSet)
                // Members also decide which profile a collection uses, so this applies to parent-only overlays too.
                dependency = Revision(ItemKey(item.InternalId)) + "g" + ProfileGeneration;
            else if ((item is Series || item is Season) && OverlayRequirements.VideoChildren(item, settings))
                dependency = Revision(ItemKey(item.InternalId)).ToString();
            else if ((item is MusicAlbum || item is MusicArtist) && settings.EnableMusicAlbumAggregation && OverlayRequirements.MusicChildren(item, settings))
                dependency = Revision(ItemKey(item.InternalId)).ToString();
            else if (item is Movie && OverlayRequirements.MoviePaths(settings))
                dependency = MovieDependency(item);
            return dependency == null ? version : version + ":" + Epoch + ":" + dependency;
        }

        internal long GetMovieGroupRevision(string provider, string value) => Revision(MovieKey(provider, value));

        internal long CollectionRevision(long collectionId) => Revision(ItemKey(collectionId));

        internal long ProfileGeneration => Interlocked.Read(ref _profileGeneration);

        // Supplied by the image key service: a stamp of what a collection's profile-unknown key covers.
        internal Func<BaseItem, string>? CollectionStampProvider { get; set; }

        internal bool TryGetCollectionStamp(long collectionId, out string? stamp)
        {
            stamp = _collectionProfiles.TryGetValue(collectionId, out var stored) ? stored.Stamp : null;
            return stamp != null;
        }

        // The image key may use the last known profile even after members changed; rendering
        // re-resolves it and a different result changes the key once.
        internal bool TryGetCollectionProfile(long collectionId, bool requireCurrent, out Guid profileId)
        {
            profileId = Guid.Empty;
            if (!_collectionProfiles.TryGetValue(collectionId, out var stored)) return false;
            if (requireCurrent && (stored.Revision != CollectionRevision(collectionId) || stored.Generation != ProfileGeneration)) return false;
            profileId = stored.ProfileId;
            return true;
        }

        // The first resolution also records the stamp, because the collection was drawn (or is about to be)
        // under its profile-unknown key. Later resolutions keep that stamp, which no longer matches.
        internal void SetCollectionProfile(long collectionId, Guid profileId, long revision, BaseItem? collection = null)
        {
            if (collectionId <= 0) return;
            lock (_gate)
            {
                var generation = ProfileGeneration;
                var known = _collectionProfiles.TryGetValue(collectionId, out var stored);
                if (known && stored.ProfileId == profileId && stored.Revision == revision && stored.Generation == generation) return;
                if (_collectionProfiles.Count >= MaxCollections && !known) return;
                var stamp = known ? stored.Stamp : collection != null ? CollectionStampProvider?.Invoke(collection) : null;
                _collectionProfiles[collectionId] = new CollectionProfile { ProfileId = profileId, Revision = revision, Generation = generation, Stamp = stamp };
                MarkDirty();
            }
        }

        // Library mappings or profiles changed: every collection must re-resolve its profile.
        // Keys keep the last known profile but include the generation, so each collection redraws once.
        internal void InvalidateCollectionProfiles()
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _profileGeneration);
                MarkDirty();
            }
        }

        // Called while a collection is aggregated, so later removals can refresh it.
        internal void RecordCollectionMembers(long collectionId, IEnumerable<long> memberIds)
        {
            if (collectionId <= 0) return;
            var members = memberIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
            lock (_gate)
            {
                if (_collectionMembers.TryGetValue(collectionId, out var previous) && previous.SequenceEqual(members)) return;
                if (_collectionMembers.Count >= MaxCollections && previous == null) return;
                SetMembers(collectionId, members);
                MarkDirty();
            }
        }

        internal void RecordEpisodeParents(IEnumerable<BaseItem> items)
        {
            lock (_gate)
            {
                foreach (var item in items)
                    if (item is Episode) RememberEpisode(item);
            }
        }

        internal void Changed(BaseItem item, bool removed = false, bool added = false)
        {
            if (item == null || Volatile.Read(ref _disposed) != 0) return;
            lock (_gate)
            {
                var affected = new HashSet<string>(StringComparer.Ordinal);
                void Parent(long id) { if (id > 0) affected.Add(ItemKey(id)); }
                void CollectionsOf(long id)
                {
                    if (_memberCollections.TryGetValue(id, out var collections))
                        foreach (var collection in collections) Parent(collection);
                }

                switch (item)
                {
                    case Episode _:
                        Parent(item.ParentId);
                        Parent(item.SeriesId);
                        CollectionsOf(item.SeriesId);
                        if (_episodeParents.TryGetValue(item.InternalId, out var old))
                        {
                            Parent(old.Parent);
                            Parent(old.Series);
                            CollectionsOf(old.Series);
                        }
                        if (removed) _episodeParents.Remove(item.InternalId);
                        else RememberEpisode(item);
                        break;
                    case Season _:
                        Parent(item.InternalId);
                        Parent(item.ParentId);
                        Parent(item.SeriesId);
                        break;
                    case Series _:
                        Parent(item.InternalId);
                        break;
                    case Movie _:
                        foreach (var key in MovieKeys(item)) affected.Add(key);
                        break;
                    case Audio audio:
                        Parent(item.ParentId);
                        Parent(Read(() => item.AlbumId));
                        foreach (var id in LinkedIds(Read(() => audio.ArtistItems)).Concat(LinkedIds(Read(() => audio.AlbumArtistItems)))) Parent(id);
                        break;
                    case MusicAlbum album:
                        Parent(item.InternalId);
                        Parent(item.ParentId);
                        foreach (var id in LinkedIds(Read(() => album.ArtistItems)).Concat(LinkedIds(Read(() => album.AlbumArtistItems)))) Parent(id);
                        break;
                    case MusicArtist _:
                        Parent(item.InternalId);
                        break;
                    case BoxSet _:
                        Parent(item.InternalId);
                        if (removed)
                        {
                            SetMembers(item.InternalId, Array.Empty<long>());
                            _collectionProfiles.TryRemove(item.InternalId, out _);
                        }
                        break;
                }

                if (item is Movie || item is Series)
                {
                    foreach (var id in LinkedIds(Read(() => item.Collections))) Parent(id);
                    CollectionsOf(item.InternalId);
                }

                if (affected.Count == 0) return;
                if (_revisions.Count + affected.Count > MaxRevisions)
                {
                    // Revisions cannot be dropped individually without risking reuse of an old key.
                    _revisions.Clear();
                    Volatile.Write(ref _epoch, NewEpoch());
                }
                foreach (var key in affected) _revisions[key] = ++_next;
                if (removed && !(item is Movie)) _revisions.TryRemove(ItemKey(item.InternalId), out _);
                MarkDirty();
            }
        }

        // Linked items (collections, artists, album) can load from the database on first access.
        // Losing one link only means that parent is not refreshed by this event.
        private T? Read<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex)
            {
                _logger.Debug($"[EmbyIcons] Could not read item relationships for image revisions: {ex.Message}");
                return default;
            }
        }

        private long Revision(string key) => _revisions.TryGetValue(key, out var revision) ? revision : 0;

        private string MovieDependency(BaseItem item)
        {
            var keys = MovieKeys(item);
            return keys.Count == 0 ? "0" : string.Join(".", keys.Select(Revision));
        }

        private static List<string> MovieKeys(BaseItem item)
        {
            var keys = new List<string>(2);
            if (item.ProviderIds == null) return keys;
            foreach (var pair in item.ProviderIds)
            {
                if (string.IsNullOrEmpty(pair.Value)) continue;
                if (pair.Key.Equals(StringConstants.ImdbProvider, StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals(StringConstants.TmdbProvider, StringComparison.OrdinalIgnoreCase))
                    keys.Add(MovieKey(pair.Key, pair.Value));
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static IEnumerable<long> LinkedIds(LinkedItemInfo[]? items) =>
            items == null ? Enumerable.Empty<long>() : items.Where(i => i != null && i.Id > 0).Select(i => i.Id);

        private static string ItemKey(long id) => "i" + id;

        private static string MovieKey(string provider, string value) => "m" + provider.ToLowerInvariant() + ":" + value.ToLowerInvariant();

        private static string NewEpoch() => Guid.NewGuid().ToString("N");

        private void RememberEpisode(BaseItem item)
        {
            if (item.InternalId <= 0) return;
            if (_episodeParents.Count >= MaxEpisodeParents && !_episodeParents.ContainsKey(item.InternalId)) _episodeParents.Clear();
            _episodeParents[item.InternalId] = (item.ParentId, item.SeriesId);
        }

        private void SetMembers(long collectionId, long[] members)
        {
            if (_collectionMembers.TryGetValue(collectionId, out var previous))
                foreach (var member in previous)
                    if (_memberCollections.TryGetValue(member, out var set) && set.Remove(collectionId) && set.Count == 0)
                        _memberCollections.Remove(member);

            if (members.Length == 0)
            {
                _collectionMembers.Remove(collectionId);
                return;
            }

            _collectionMembers[collectionId] = members;
            foreach (var member in members)
            {
                if (!_memberCollections.TryGetValue(member, out var set))
                    _memberCollections[member] = set = new HashSet<long>();
                set.Add(collectionId);
            }
        }

        private void MarkDirty()
        {
            if (_dirty || Volatile.Read(ref _disposed) != 0) return;
            _dirty = true;
            // If the server stops before the next save, the next start cannot trust saved revisions.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_pendingPath, string.Empty);
            }
            catch (Exception ex) { _logger.Warn($"[EmbyIcons] Could not mark image revisions as pending: {ex.Message}"); }
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        private void Load()
        {
            var unclean = File.Exists(_pendingPath);
            var restored = false;
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length <= 64 * 1024 * 1024)
                {
                    var saved = JsonSerializer.Deserialize<StoredState>(File.ReadAllText(_path));
                    if (saved != null && saved.Format == FormatVersion && Guid.TryParseExact(saved.Epoch, "N", out _))
                    {
                        _next = saved.Next;
                        _profileGeneration = saved.ProfileGeneration;
                        if (saved.Revisions != null)
                            foreach (var pair in saved.Revisions) _revisions[pair.Key] = pair.Value;
                        if (saved.Collections != null)
                            foreach (var pair in saved.Collections) SetMembers(pair.Key, pair.Value ?? Array.Empty<long>());
                        if (saved.CollectionProfiles != null)
                            foreach (var pair in saved.CollectionProfiles) _collectionProfiles[pair.Key] = pair.Value;
                        if (!unclean)
                        {
                            _epoch = saved.Epoch!;
                            restored = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EmbyIcons] Ignoring unreadable image revisions; affected images will be redrawn once: {ex.Message}");
                _revisions.Clear();
                _collectionProfiles.Clear();
                _collectionMembers.Clear();
                _memberCollections.Clear();
            }

            if (unclean)
                _logger.Warn("[EmbyIcons] The server stopped before image revisions were saved; images that depend on other items will be redrawn once.");

            if (!restored)
            {
                // A new epoch must be on disk before any image key uses it. Otherwise a session without
                // library changes never saves it, and every restart would redraw TV shows and collections.
                lock (_gate) _dirty = true;
                Save();
            }
        }

        // Writes pending changes now, for example when the server shuts down.
        internal void Flush()
        {
            if (Volatile.Read(ref _disposed) == 0) Save();
        }

        private void Save()
        {
            // The timer and Dispose can both save; only one may write the file at a time.
            lock (_saveGate) SaveCore();
        }

        private void SaveCore()
        {
            try
            {
                StoredState snapshot;
                lock (_gate)
                {
                    if (!_dirty) return;
                    snapshot = new StoredState
                    {
                        Format = FormatVersion,
                        Epoch = Epoch,
                        Next = _next,
                        ProfileGeneration = ProfileGeneration,
                        Revisions = new Dictionary<string, long>(_revisions, StringComparer.Ordinal),
                        Collections = new Dictionary<long, long[]>(_collectionMembers),
                        CollectionProfiles = new Dictionary<long, CollectionProfile>(_collectionProfiles)
                    };
                    _dirty = false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);

                lock (_gate)
                {
                    // A change that arrived while writing keeps the pending marker for the next save.
                    if (!_dirty && File.Exists(_pendingPath)) File.Delete(_pendingPath);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _dirty = true;
                    if (Volatile.Read(ref _disposed) == 0) _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
                }
                _logger.Warn($"[EmbyIcons] Could not save image revisions: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _saveTimer.Dispose();
            Save();
        }

        public sealed class CollectionProfile
        {
            public Guid ProfileId { get; set; }
            public long Revision { get; set; }
            public long Generation { get; set; }
            public string? Stamp { get; set; }
        }

        public sealed class StoredState
        {
            public int Format { get; set; }
            public string? Epoch { get; set; }
            public long Next { get; set; }
            public long ProfileGeneration { get; set; }
            public Dictionary<string, long>? Revisions { get; set; }
            public Dictionary<long, long[]>? Collections { get; set; }
            public Dictionary<long, CollectionProfile>? CollectionProfiles { get; set; }
        }
    }
}
