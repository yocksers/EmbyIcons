using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Caching
{
    // Input revisions outlive evictable aggregation results. No I/O is allowed in GetDataVersion.
    internal sealed class ImageCacheState : IDisposable
    {
        private const int MaxEntries = 500000;
        private readonly object _gate = new object();
        private readonly object _saveGate = new object();
        private readonly Dictionary<long, byte[]> _items = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]?> _pending = new Dictionary<long, byte[]?>();
        private readonly Dictionary<long, (long Parent, long Series)> _membership = new Dictionary<long, (long, long)>();
        private readonly ConcurrentDictionary<long, string> _parents = new ConcurrentDictionary<long, string>();
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private string _epoch = Guid.NewGuid().ToString("N");
        // Metadata timestamps do not prove that collection/artist link tables are unchanged.
        // Keep those two domains session-scoped until a complete relationship snapshot is available.
        private string _collections = Guid.NewGuid().ToString("N");
        private string _music = Guid.NewGuid().ToString("N");
        private string _movies = "0";
        private byte[] _fingerprint = new byte[32];
        private long _changes;
        private bool _ready;
        private bool _verificationFailed;
        private int _disposed;
        private int _started;
        private Timer? _timer;
        private Task? _initialization;

        internal ImageCacheState(string folder, ILogger logger)
        {
            _path = Path.Combine(folder, "image-input-versions.json");
            _logger = logger;
        }

        internal string GetDataVersion(BaseItem item, ProfileSettings settings)
        {
            var version = item.DateLastSaved.UtcTicks + ":" + item.DateModified.UtcTicks;
            string? dependency = null;
            if ((item is Series || item is Season) && OverlayRequirements.VideoChildren(item, settings))
                dependency = (_parents.TryGetValue(item.InternalId, out var revision) ? revision : "0") + ":" +
                    (_parents.TryGetValue(0, out var fallback) ? fallback : "0");
            else if (item is BoxSet)
                dependency = Volatile.Read(ref _collections);
            else if ((item is MusicAlbum || item is MusicArtist) && settings.EnableMusicAlbumAggregation && OverlayRequirements.MusicChildren(item, settings))
                dependency = Volatile.Read(ref _music);
            else if (item is Movie && OverlayRequirements.MoviePaths(settings))
                dependency = Volatile.Read(ref _movies);
            return dependency == null ? version : version + ":" + Volatile.Read(ref _epoch) + ":" + dependency;
        }

        internal string MovieRevision => Volatile.Read(ref _movies);

        internal void Changed(BaseItem item, bool removed = false, bool added = false)
        {
            var stamp = removed ? null : Stamp(item);
            lock (_gate)
            {
                _changes++;
                if (_ready) UpdateFingerprint(item.InternalId, stamp);
                else if (!_verificationFailed) _pending[item.InternalId] = stamp;
                if (_items.Count > MaxEntries || _pending.Count > MaxEntries)
                {
                    _ready = false;
                    _verificationFailed = true;
                    _items.Clear();
                    _pending.Clear();
                }

                var revision = Guid.NewGuid().ToString("N");
                if (item is Episode || item is Season)
                {
                    if (_membership.TryGetValue(item.InternalId, out var old))
                    {
                        _parents[old.Parent] = revision;
                        _parents[old.Series] = revision;
                    }
                    else if (!added) _parents[0] = revision;
                    if (removed) _membership.Remove(item.InternalId);
                    else _membership[item.InternalId] = (item.ParentId, item.SeriesId);
                }
                if (item is Episode)
                {
                    _parents[item.ParentId] = revision;
                    _parents[item.SeriesId] = revision;
                }
                else if (item is Season)
                {
                    _parents[item.InternalId] = revision;
                    _parents[item.ParentId] = revision;
                }
                else if (item is Series) _parents[item.InternalId] = revision;

                // Membership/artist links are not always physical parents. Conservative domain
                // revisions also cover new, removed and moved members without synchronous reverse queries.
                if (item is Video || item is Series || item is Season || item is BoxSet)
                    Volatile.Write(ref _collections, revision);
                if (item is Movie) Volatile.Write(ref _movies, revision);
                if (item is Audio || item is MusicAlbum || item is MusicArtist)
                    Volatile.Write(ref _music, revision);
                if (_parents.Count > MaxEntries)
                {
                    Volatile.Write(ref _epoch, revision);
                    _parents.Clear();
                }
                if (_membership.Count > MaxEntries)
                {
                    _membership.Clear();
                    _parents[0] = revision;
                }
            }
        }

        internal void Start(ILibraryManager libraryManager)
        {
            lock (_gate)
            {
                if (_started != 0 || Volatile.Read(ref _disposed) != 0) return;
                _started = 1;
                var token = _stop.Token;
                _initialization = Task.Run(() => Initialize(libraryManager, token));
                _timer = new Timer(_ => Save(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
            }
        }

        private void Initialize(ILibraryManager libraryManager, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                StoredVersions? saved = null;
                if (File.Exists(_path) && new FileInfo(_path).Length <= 64 * 1024 * 1024)
                {
                    try { saved = JsonSerializer.Deserialize<StoredVersions>(File.ReadAllText(_path)); }
                    catch (Exception ex) { _logger.Warn($"[EmbyIcons] Ignoring invalid image version state: {ex.Message}"); }
                }

                var items = new Dictionary<long, byte[]>();
                var membership = new Dictionary<long, (long, long)>();
                var ids = libraryManager.GetInternalItemIds(new InternalItemsQuery { Recursive = true, Limit = MaxEntries + 1 });
                if (ids.Length > MaxEntries) throw new InvalidOperationException("Image version verification exceeds the tracked-item limit.");
                for (int offset = 0; offset < ids.Length; offset += 500)
                {
                    token.ThrowIfCancellationRequested();
                    if (_verificationFailed) throw new InvalidOperationException("Image input tracking limit exceeded.");
                    var page = libraryManager.GetItemList(new InternalItemsQuery
                    {
                        ItemIds = ids.Skip(offset).Take(500).ToArray(), Limit = 500
                    }, token);
                    foreach (var item in page)
                    {
                        items[item.InternalId] = Stamp(item);
                        if (item is Episode || item is Season) membership[item.InternalId] = (item.ParentId, item.SeriesId);
                    }
                }

                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_verificationFailed) throw new InvalidOperationException("Image input tracking limit exceeded.");
                    foreach (var pair in items) UpdateFingerprint(pair.Key, pair.Value);
                    foreach (var pair in _pending) UpdateFingerprint(pair.Key, pair.Value);
                    foreach (var pair in membership)
                        if (!_pending.ContainsKey(pair.Key)) _membership[pair.Key] = pair.Value;
                    _pending.Clear();
                    if (_changes == 0 && saved?.Format == 1 && saved.Count == _items.Count &&
                        saved.Fingerprint == Convert.ToBase64String(_fingerprint) && Guid.TryParseExact(saved.Epoch, "N", out _) &&
                        saved.Parents != null && saved.Parents.Count <= MaxEntries &&
                        saved.Parents.Values.All(value => Guid.TryParseExact(value, "N", out _)))
                    {
                        foreach (var pair in saved.Parents) _parents[pair.Key] = pair.Value;
                        _movies = saved.Movies ?? "0";
                        Volatile.Write(ref _epoch, saved.Epoch!);
                    }
                    _ready = true;
                }
                _logger.Info("[EmbyIcons] Image input versions verified in the background.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _verificationFailed = true;
                    _pending.Clear();
                }
                _logger.Warn($"[EmbyIcons] Image version verification deferred; using safe session revisions: {ex.Message}");
            }
        }

        private static byte[] Stamp(BaseItem item)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(item.InternalId + "|" + item.GetType().FullName + "|" +
                item.ParentId + "|" + item.DateLastSaved.UtcTicks + "|" + item.DateModified.UtcTicks + "|" + item.Path));
        }

        private void UpdateFingerprint(long id, byte[]? stamp)
        {
            if (_items.TryGetValue(id, out var previous))
                for (int i = 0; i < 32; i++) _fingerprint[i] ^= previous[i];
            _items.Remove(id);
            if (stamp == null) return;
            _items[id] = stamp;
            for (int i = 0; i < 32; i++) _fingerprint[i] ^= stamp[i];
        }

        private void Save(bool final = false)
        {
            try
            {
                lock (_saveGate)
                {
                    if (Volatile.Read(ref _disposed) != 0 && !final) return;
                    StoredVersions snapshot;
                    lock (_gate)
                    {
                        if (!_ready) return;
                        snapshot = new StoredVersions
                        {
                            Format = 1, Count = _items.Count, Fingerprint = Convert.ToBase64String(_fingerprint),
                            Epoch = _epoch, Movies = _movies,
                            Parents = new Dictionary<long, string>(_parents)
                        };
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    var temp = _path + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
                    if (File.Exists(_path)) File.Replace(temp, _path, null);
                    else File.Move(temp, _path);
                }
            }
            catch (Exception ex) { _logger.Warn($"[EmbyIcons] Could not save image input versions: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Task? initialization;
            lock (_gate)
            {
                _timer?.Dispose();
                initialization = _initialization;
            }
            _stop.Cancel();
            Save(final: true);
            if (initialization == null) _stop.Dispose();
            else _ = initialization.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
            // The worker may still be inside a synchronous host query. Do not block shutdown.
        }

        internal Task? Initialization => _initialization;

        public sealed class StoredVersions
        {
            public int Format { get; set; }
            public int Count { get; set; }
            public string? Fingerprint { get; set; }
            public string? Epoch { get; set; }
            public string? Movies { get; set; }
            public Dictionary<long, string>? Parents { get; set; }
        }
    }
}
