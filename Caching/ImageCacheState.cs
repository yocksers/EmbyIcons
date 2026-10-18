using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Caching
{
    // Only materialized image identities are tracked. The library's total item count
    // is irrelevant. No disk, library, stream or network calls are made by GetDataVersion.
    internal sealed class ImageCacheState : IDisposable
    {
        internal const int PartitionCount = 1024;
        private readonly object _gate = new object();
        private readonly object _saveGate = new object();
        private readonly ConcurrentDictionary<string, ItemState> _items = new ConcurrentDictionary<string, ItemState>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ItemState>[] _buckets = Enumerable.Range(0, PartitionCount).Select(_ => new ConcurrentDictionary<string, ItemState>(StringComparer.Ordinal)).ToArray();
        private readonly Dictionary<string, HashSet<string>> _movieGroups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<long, HashSet<string>> _owners = new Dictionary<long, HashSet<string>>();
        private volatile HashSet<string>? _activeProjections;
        private readonly ConcurrentDictionary<string, ImageInputProjection> _projections = new ConcurrentDictionary<string, ImageInputProjection>();
        private readonly ConcurrentDictionary<string, BaseItem?> _pending = new ConcurrentDictionary<string, BaseItem?>(StringComparer.Ordinal);
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly string[] _recovery = new string[PartitionCount];
        private readonly string _folder;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private ILibraryManager? _library;
        private int _disposed;
        private int _started;
        private int _working;
        private int _relationshipChanges;
        private long _lastRelationshipSweep;
        private string _movies = "0";
        private long _eventSequence;
        private long _lastVerificationWarning;
        private readonly ConcurrentDictionary<long, SourceStamp> _sources = new ConcurrentDictionary<long, SourceStamp>();
        private Timer? _timer;
        private Timer? _saveTimer;
        private Task? _initialization;
        private Task? _work;

        // Inspections run serially in the background or under the renderer's concurrency
        // limit. The delegate must return the actual enabled overlay inputs and members.
        internal Func<BaseItem, ProfileSettings, Inspection, CancellationToken, Task<Inspection>>? Inspect { get; set; }
        internal Action<BaseItem>? ResolveProfile { get; set; }
        internal bool CollectionEventsAvailable { get; set; }

        internal ImageCacheState(string folder, ILogger logger)
        {
            _folder = Path.Combine(folder, "image-state-v2");
            _logger = logger;
            Load(); // Startup only: paths are supplied by IApplicationPaths, not plugin Configuration.
        }

        internal static string Identity(BaseItem item) => item.InternalId > 0 ? "i" + item.InternalId.ToString(CultureInfo.InvariantCulture) :
            "g" + item.Id.ToString("N") + ":" + item.GetType().Name;

        internal static int Partition(string identity)
        {
            uint hash = 2166136261;
            foreach (char c in identity) hash = unchecked((hash ^ c) * 16777619);
            return (int)(hash % PartitionCount);
        }

        private static string Seed(BaseItem item) => item.DateLastSaved.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" +
            item.DateModified.UtcTicks.ToString(CultureInfo.InvariantCulture);
        internal static string Source(BaseItem item) => ImageInputProjection.Hash(Seed(item) + "|" + item.Path + "|" + item.ParentId + "|" + item.SeriesId + "|" + item.IndexNumber);

        private ItemState GetItem(BaseItem item)
        {
            string identity = Identity(item);
            if (_items.TryGetValue(identity, out var found)) return found;
            lock (_gate)
            {
                if (_items.TryGetValue(identity, out found)) return found;
                var entry = new ItemState { Identity = identity, InternalId = item.InternalId, Kind = ImageInputProjection.Kind(item),
                    Seed = Seed(item), Source = Source(item) };
                _items[identity] = entry;
                _buckets[Partition(identity)][identity] = entry;
                _dirty.Add(Partition(identity));
                return entry;
            }
        }

        internal ImageInputProjection Projection(BaseItem item, ProfileSettings settings)
        {
            string id = ImageInputProjection.Key(item, settings);
            return _projections.GetOrAdd(id, _ => new ImageInputProjection(item, settings));
        }

        internal void UseProjections(IEnumerable<string> ids) => _activeProjections = new HashSet<string>(ids, StringComparer.Ordinal);

        private InputState GetInput(ItemState entry, BaseItem item, ImageInputProjection projection)
        {
            if (!entry.Inputs.TryGetValue(projection.Id, out var input))
            {
                input = new InputState { Light = projection.Light(item), Source = Source(item), Verified = !projection.Heavy };
                entry.Inputs.Add(projection.Id, input);
                _dirty.Add(Partition(entry.Identity));
            }
            return input;
        }

        internal string GetDataVersion(BaseItem item, ProfileSettings settings)
        {
            var projection = Projection(item, settings);
            var entry = GetItem(item);
            lock (_gate)
            {
                var input = GetInput(entry, item, projection);
                if (item is Movie && OverlayRequirements.MoviePaths(settings)) IndexMovie(entry, ProviderGroups(item));
                var recovery = _recovery[Partition(entry.Identity)];
                return entry.Seed + (string.IsNullOrEmpty(recovery) ? "" : "~" + recovery) +
                    (string.IsNullOrEmpty(input.Version) ? "" : "~" + input.Version);
            }
        }

        internal void TransferProjection(BaseItem item, string from, string to)
        {
            var entry = GetItem(item);
            lock (_gate)
                if (entry.Inputs.TryGetValue(from, out var input))
                {
                    entry.Inputs[to] = input.Copy();
                    _dirty.Add(Partition(entry.Identity));
                    Reindex(entry);
                }
        }

        internal string ProjectionVersion(BaseItem item, string projectionId)
        {
            var entry = GetItem(item);
            lock (_gate)
            {
                entry.Inputs.TryGetValue(projectionId, out var input);
                string recovery = _recovery[Partition(entry.Identity)];
                return entry.Seed + (string.IsNullOrEmpty(recovery) ? "" : "~" + recovery) +
                    (string.IsNullOrEmpty(input?.Version) ? "" : "~" + input!.Version);
            }
        }

        internal string GetAggregationVersion(BaseItem item, ProfileSettings settings)
        {
            string version = GetDataVersion(item, settings);
            var entry = GetItem(item);
            lock (_gate) return version + ":pending:" + entry.Generation +
                (item is Movie && OverlayRequirements.MoviePaths(settings) ? ":versions:" + MovieRevision : string.Empty);
        }

        // The caller performs only precomputed settings comparisons while holding this lock.
        internal T WithBinding<T>(BaseItem item, int imageType, Func<KeyBinding?, KeyBinding> update, Func<KeyBinding, T> result)
        {
            var entry = GetItem(item);
            lock (_gate)
            {
                entry.Bindings.TryGetValue(imageType, out var old);
                var binding = update(old);
                if (!ReferenceEquals(binding, old))
                {
                    entry.Bindings[imageType] = binding;
                    Reindex(entry);
                    _dirty.Add(Partition(entry.Identity));
                }
                return result(binding);
            }
        }

        internal int[] BoundImageTypes(BaseItem item)
        {
            if (!_items.TryGetValue(Identity(item), out var entry)) return Array.Empty<int>();
            lock (_gate) return entry.Bindings.Keys.ToArray();
        }

        internal Guid? KnownProfile(BaseItem item, int imageType)
        {
            if (!_items.TryGetValue(Identity(item), out var entry)) return null;
            lock (_gate) return entry.Bindings.TryGetValue(imageType, out var binding) ? binding.ProfileId : null;
        }

        internal long BeginInspection(BaseItem item) { var entry = GetItem(item); lock (_gate) return entry.Generation; }

        internal void Observed(BaseItem item, ProfileSettings settings, Inspection inspection, long generation, bool rendering = false)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var projection = Projection(item, settings);
            var entry = GetItem(item);
            var light = projection.Light(item);
            string heavy = projection.Heavy ? inspection.Fingerprint : string.Empty;
            lock (_gate)
            {
                if (entry.Generation != generation || Volatile.Read(ref _disposed) != 0)
                {
                    _pending.TryAdd(entry.Identity, null);
                    return;
                }
                var input = GetInput(entry, item, projection);
                var dependencies = inspection.Dependencies.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
                bool graphChanged = !input.Dependencies.SequenceEqual(dependencies);
                string source = Source(item);
                bool persist = input.Light != light || input.Heavy != heavy || input.Source != source || !input.Verified || input.ForceInspection ||
                    (rendering && !input.Rendered) || graphChanged || input.DependencyStamp != inspection.DependencyStamp;
                bool changed = !(rendering && !input.Rendered) && (input.Light != light || (input.Heavy != null && input.Heavy != heavy));
                if (rendering) input.Rendered = true;
                input.Light = light;
                input.Heavy = heavy;
                input.Source = source;
                input.Verified = true;
                input.ForceInspection = false;
                input.DependencyStamp = inspection.DependencyStamp;
                if (changed) input.Version = ImageInputProjection.Hash(input.Light + "|" + input.Heavy);
                input.Dependencies = dependencies;
                entry.Source = input.Source;
                entry.Recent = new WeakReference<BaseItem>(item);
                if (graphChanged) Reindex(entry);
                if (persist) _dirty.Add(Partition(entry.Identity));
            }
        }

        private static bool CurrentInput(ItemState entry, string projection) => entry.Bindings.Count == 0 ||
            entry.Bindings.Values.Any(binding => binding.DataProjection == projection);

        private long[] ActiveDependencies()
        {
            var ids = new HashSet<long>();
            foreach (var entry in _items.Values)
                lock (_gate) ids.UnionWith(entry.Inputs.Where(p => CurrentInput(entry, p.Key)).SelectMany(p => p.Value.Dependencies));
            return ids.ToArray();
        }

        private void Reindex(ItemState entry)
        {
            var next = new HashSet<long>(entry.Inputs.Where(p => CurrentInput(entry, p.Key)).SelectMany(p => p.Value.Dependencies));
            foreach (long old in entry.Dependencies)
                if (!next.Contains(old) && _owners.TryGetValue(old, out var owners))
                {
                    owners.Remove(entry.Identity);
                    if (owners.Count == 0) { _owners.Remove(old); if (!_items.ContainsKey("i" + old)) _sources.TryRemove(old, out _); }
                }
            foreach (long id in next)
            {
                if (!_owners.TryGetValue(id, out var owners)) _owners[id] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(entry.Identity);
            }
            entry.Dependencies = next.ToArray();
        }

        private static string[] ProviderGroups(BaseItem item)
        {
            return MovieVersions.Groups(item).Select(ImageInputProjection.Hash).ToArray();
        }

        private void IndexMovie(ItemState entry, string[] groups, bool dirty = true)
        {
            if (entry.ProviderGroups.SequenceEqual(groups)) return;
            foreach (string old in entry.ProviderGroups)
                if (_movieGroups.TryGetValue(old, out var owners))
                { owners.Remove(entry.Identity); if (owners.Count == 0) _movieGroups.Remove(old); }
            entry.ProviderGroups = groups;
            foreach (string group in groups)
            {
                if (!_movieGroups.TryGetValue(group, out var owners)) _movieGroups[group] = owners = new HashSet<string>(StringComparer.Ordinal);
                owners.Add(entry.Identity);
            }
            if (dirty) _dirty.Add(Partition(entry.Identity));
        }

        internal void CollectionChanged(BaseItem collection)
        {
            if (_disposed != 0 || !_items.TryGetValue(Identity(collection), out var entry)) return;
            lock (_gate)
            {
                entry.Generation++;
                foreach (var input in entry.Inputs.Values) input.ForceInspection = true;
                _pending[entry.Identity] = null; // Membership events may carry a partial item.
            }
        }

        internal string MovieRevision => Volatile.Read(ref _movies);

        internal void Changed(BaseItem item, bool removed = false, bool added = false)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            string identity = Identity(item);
            long eventSequence = Interlocked.Increment(ref _eventSequence);
            lock (_gate)
            {
                if (_items.ContainsKey(identity) || _owners.ContainsKey(item.InternalId) || _sources.ContainsKey(item.InternalId))
                    _sources[item.InternalId] = new SourceStamp(removed ? "-" : Source(item), eventSequence);
                var affected = new HashSet<string>(StringComparer.Ordinal);
                if (item is Movie)
                {
                    var groups = ProviderGroups(item).Concat(_items.TryGetValue(identity, out var indexed) ? indexed.ProviderGroups : Array.Empty<string>()).Distinct();
                    foreach (string group in groups)
                        if (_movieGroups.TryGetValue(group, out var movies))
                            foreach (string movie in movies)
                                if (_items.TryGetValue(movie, out var candidate) && candidate.Inputs.Keys.Any(id => CurrentInput(candidate, id) && _projections.TryGetValue(id, out var projection) && OverlayRequirements.MoviePaths(projection.Settings))) affected.Add(movie);
                }
                foreach (long id in new[] { item.InternalId, item.ParentId, item.SeriesId })
                    if (id > 0 && _owners.TryGetValue(id, out var owners)) affected.UnionWith(owners);
                foreach (long id in new[] { item.ParentId, item.SeriesId })
                    if (id > 0 && _items.ContainsKey("i" + id)) affected.Add("i" + id);

                if (_items.TryGetValue(identity, out var entry))
                {
                    entry.Generation++;
                    if (removed)
                    {
                        entry.Inputs.Clear();
                        Reindex(entry);
                        IndexMovie(entry, Array.Empty<string>());
                        _items.TryRemove(identity, out _);
                        _buckets[Partition(identity)].TryRemove(identity, out _);
                    }
                    else
                    {
                        foreach (var pair in entry.Inputs)
                        {
                            if (!_projections.TryGetValue(pair.Key, out var projection)) continue;
                            var input = pair.Value;
                            string light = projection.Light(item);
                            if (input.Light != light)
                            {
                                input.Light = light;
                                input.Version = ImageInputProjection.Hash(light + "|" + input.Heavy);
                            }
                            // A legacy image without an inspected heavy baseline cannot be
                            // certified unchanged. Its first real source update is conservative.
                            if (projection.Heavy && !input.Verified && input.Source != Source(item))
                                input.Version = ImageInputProjection.Hash(light + "|" + Source(item));
                        }
                        entry.Source = Source(item);
                        if (item is Movie && entry.ProviderGroups.Length > 0) IndexMovie(entry, ProviderGroups(item));
                        affected.Add(identity);
                    }
                    _dirty.Add(Partition(identity));
                }
                foreach (string owner in affected)
                {
                    if (!_items.TryGetValue(owner, out var target)) continue;
                    if (owner != identity) target.Generation++;
                    foreach (var input in target.Inputs.Values) input.ForceInspection = true;
                    _pending[owner] = owner == identity && !removed ? item : target.Recent != null && target.Recent.TryGetTarget(out var recent) ? recent : null;
                }
            }
            if (item is Movie) Volatile.Write(ref _movies, Source(item));
            // Link tables need not update a BoxSet/artist timestamp. Coalesce discovery
            // sweeps; they compare actual inputs instead of invalidating a whole domain.
            if (!CollectionEventsAvailable && (item is Video || item is Series || item is Season || item is BoxSet))
                MarkRelationships(1);
            if (item is Audio || item is MusicAlbum || item is MusicArtist)
                MarkRelationships(2);
        }

        private void MarkRelationships(int mask)
        {
            int old;
            do { old = Volatile.Read(ref _relationshipChanges); }
            while (Interlocked.CompareExchange(ref _relationshipChanges, old | mask, old) != old);
        }

        internal void RequestVerification()
        {
            foreach (var entry in _items.Values) _pending.TryAdd(entry.Identity, null);
        }

        internal void Start(ILibraryManager libraryManager)
        {
            lock (_gate)
            {
                if (_started != 0 || _disposed != 0) return;
                _started = 1;
                _library = libraryManager;
                _working = 1;
                RequestVerification();
                _initialization = _work = Task.Run(async () =>
                {
                    try { await RefreshSources(ActiveDependencies(), _stop.Token, true).ConfigureAwait(false); await Drain().ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { ReportVerificationFailure(ex); }
                    finally { Interlocked.Exchange(ref _working, 0); }
                });
                _timer = new Timer(_ => Pump(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                _saveTimer = new Timer(_ => Save(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
        }

        private void DiscoverRelationships(bool force = false)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!force && now - Interlocked.Read(ref _lastRelationshipSweep) < TimeSpan.FromSeconds(30).Ticks) return;
            int changes = Interlocked.Exchange(ref _relationshipChanges, 0);
            if (changes == 0) return;
            Interlocked.Exchange(ref _lastRelationshipSweep, now);
            foreach (var entry in _items.Values)
            {
                bool related = ((changes & 1) != 0 && entry.Kind == nameof(BoxSet)) ||
                    ((changes & 2) != 0 && (entry.Kind == nameof(MusicArtist) || entry.Kind == nameof(MusicAlbum)));
                if (!related) continue;
                lock (_gate)
                    if (entry.Kind == nameof(BoxSet) || entry.Inputs.Any(p =>
                        p.Value.Rendered && CurrentInput(entry, p.Key) && (_activeProjections == null || _activeProjections.Contains(p.Key)) &&
                        _projections.TryGetValue(p.Key, out var projection) &&
                        (projection.Children || (entry.Kind == nameof(Movie) && OverlayRequirements.MoviePaths(projection.Settings)))))
                        _pending.TryAdd(entry.Identity, null);
            }
        }

        private void Pump()
        {
            if (_disposed != 0 || _library == null) return;
            DiscoverRelationships();
            if (_pending.IsEmpty || Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
            _work = Task.Run(async () =>
            {
                try { await Drain().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                finally { Interlocked.Exchange(ref _working, 0); }
            });
        }

        private void ReportVerificationFailure(Exception exception)
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastVerificationWarning);
            if (now - last < TimeSpan.FromSeconds(30).Ticks || Interlocked.CompareExchange(ref _lastVerificationWarning, now, last) != last) return;
            _logger.Warn($"[EmbyIcons] Image input verification will retry: {exception.Message}");
        }

        private async Task Drain()
        {
            var token = _stop.Token;
            // Snapshot the queue: events arriving while we inspect are handled on the
            // next pass. A busy importer must not create an unbounded worker turn.
            var keys = _pending.Keys.ToArray();
            for (int offset = 0; offset < keys.Length; offset += 500)
            {
                if (token.IsCancellationRequested) return;
                var batch = new Dictionary<string, BaseItem?>(StringComparer.Ordinal);
                foreach (string key in keys.Skip(offset).Take(500))
                    if (_pending.TryRemove(key, out var item) && _items.ContainsKey(key)) batch[key] = item;
                Dictionary<string, long> generations;
                lock (_gate) generations = batch.Keys.ToDictionary(key => key, key => _items.TryGetValue(key, out var entry) ? entry.Generation : -1L);
                var ids = batch.Where(p => p.Value == null).Select(p => _items.TryGetValue(p.Key, out var e) ? e.InternalId : 0)
                    .Where(id => id > 0).Distinct().ToArray();
                try
                {
                    if (ids.Length > 0)
                    {
                        var rows = _library!.GetItemList(new InternalItemsQuery { ItemIds = ids, Limit = 500 }, token);
                        foreach (var row in rows) if (batch.ContainsKey(Identity(row))) batch[Identity(row)] = row;
                    }
                    foreach (var pair in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        if (pair.Value == null)
                        {
                            lock (_gate)
                            {
                                if (_items.TryGetValue(pair.Key, out var gone) && gone.Generation == generations[pair.Key])
                                {
                                    gone.Inputs.Clear(); Reindex(gone); IndexMovie(gone, Array.Empty<string>());
                                    _items.TryRemove(pair.Key, out _); _buckets[Partition(pair.Key)].TryRemove(pair.Key, out _);
                                    _dirty.Add(Partition(pair.Key));
                                }
                            }
                            continue;
                        }
                        await Verify(pair.Value, token, generations[pair.Key]).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    foreach (var pair in batch) _pending.TryAdd(pair.Key, null);
                    ReportVerificationFailure(ex);
                }
                await Task.Delay(5, token).ConfigureAwait(false);
            }
        }

        private async Task Verify(BaseItem item, CancellationToken token, long generation)
        {
            if (!_items.TryGetValue(Identity(item), out var entry)) return;
            lock (_gate)
                if (entry.Generation != generation) { _pending.TryAdd(entry.Identity, null); return; }
            ResolveProfile?.Invoke(item);
            KeyValuePair<string, InputState>[] inputs;
            lock (_gate) inputs = entry.Inputs.Where(p => CurrentInput(entry, p.Key)).ToArray();
            foreach (var pair in inputs)
            {
                if ((_activeProjections != null && !_activeProjections.Contains(pair.Key)) || !_projections.TryGetValue(pair.Key, out var projection)) continue;
                var light = projection.Light(item);
                if (!projection.Heavy)
                {
                    Observed(item, projection.Settings, new Inspection { Fingerprint = string.Empty }, generation);
                }
                else if (pair.Value.Verified && Inspect != null)
                {
                    var result = await Inspect(item, projection.Settings, new Inspection { Fingerprint = pair.Value.Heavy ?? string.Empty, Dependencies = pair.Value.Dependencies,
                        DependencyStamp = pair.Value.DependencyStamp, Source = pair.Value.Source, Force = pair.Value.ForceInspection }, token).ConfigureAwait(false);
                    Observed(item, projection.Settings, result, generation);
                }
                else
                {
                    lock (_gate)
                    {
                        if (entry.Generation != generation) { _pending.TryAdd(entry.Identity, null); continue; }
                        var input = pair.Value;
                        if (input.Light != light || input.Source != Source(item))
                            input.Version = ImageInputProjection.Hash(light + "|" + Source(item));
                        input.Light = light;
                        input.Source = Source(item);
                        _dirty.Add(Partition(entry.Identity));
                    }
                }
            }
        }

        private sealed class SourceStamp
        {
            internal readonly string Value;
            internal readonly long Sequence;
            internal SourceStamp(string value, long sequence) { Value = value; Sequence = sequence; }
        }

        private async Task RefreshSources(long[] ids, CancellationToken token, bool force)
        {
            var pending = ids.Where(id => id > 0 && (force || !_sources.ContainsKey(id))).Distinct().ToArray();
            for (int offset = 0; offset < pending.Length; offset += 500)
            {
                token.ThrowIfCancellationRequested();
                long sequence = Interlocked.Read(ref _eventSequence);
                var page = pending.Skip(offset).Take(500).ToArray();
                var rows = _library!.GetItemList(new InternalItemsQuery { ItemIds = page, Limit = 500 }, token).ToArray();
                var values = rows.ToDictionary(Identity, Source);
                foreach (long id in page)
                {
                    string value = values.TryGetValue("i" + id, out var current) ? current : "-";
                    var stamp = new SourceStamp(value, sequence);
                    _sources.AddOrUpdate(id, stamp, (_, old) => old.Sequence > sequence ? old : stamp);
                }
                await Task.Delay(5, token).ConfigureAwait(false);
            }
        }

        internal async Task<string> DependencyStamp(long[] ids, CancellationToken token)
        {
            if (ids.Length == 0) return string.Empty;
            if (_library == null) return ImageInputProjection.Hash(string.Join("|", ids.OrderBy(id => id)));
            await RefreshSources(ids, token, false).ConfigureAwait(false);
            return ImageInputProjection.Hash(string.Join("|", ids.OrderBy(id => id).Select(id => id + ":" + (_sources.TryGetValue(id, out var value) ? value.Value : "-"))));
        }

        internal async Task VerifyPendingAsync()
        {
            if (_library == null) return;
            for (int i = 0; i < 4; i++)
            {
                var work = _work;
                if (work != null) await work.ConfigureAwait(false);
                DiscoverRelationships(force: true);
                if (_pending.IsEmpty) return;
                Pump();
            }
        }

        private void Load()
        {
            if (!Directory.Exists(_folder)) return;
            for (int partition = 0; partition < PartitionCount; partition++)
            {
                string path = Path.Combine(_folder, partition.ToString("x3") + ".json");
                string epochPath = path + ".epoch";
                if (File.Exists(epochPath))
                {
                    try { var value = File.ReadAllText(epochPath); if (Guid.TryParseExact(value, "N", out _)) _recovery[partition] = value; }
                    catch (Exception ex) { _logger.Warn($"[EmbyIcons] Could not read image state recovery marker: {ex.Message}"); }
                }
                if (!File.Exists(path)) continue;
                ItemState[]? entries = ReadPartition(path, partition);
                if (entries == null)
                {
                    entries = ReadPartition(path + ".bak", partition);
                    _recovery[partition] = Guid.NewGuid().ToString("N");
                    try { File.WriteAllText(epochPath, _recovery[partition]); }
                    catch (Exception ex) { _logger.Warn($"[EmbyIcons] Could not persist image state recovery marker: {ex.Message}"); }
                    _dirty.Add(partition);
                    _logger.Warn($"[EmbyIcons] Rebuilding image input partition {partition:x3}; other image keys remain unchanged.");
                }
                if (entries == null) continue;
                foreach (var entry in entries)
                {
                    _items[entry.Identity] = entry; _buckets[partition][entry.Identity] = entry; Reindex(entry);
                    var groups = entry.ProviderGroups; entry.ProviderGroups = Array.Empty<string>(); IndexMovie(entry, groups, false);
                }
            }
        }

        private ItemState[]? ReadPartition(string path, int partition)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
                var file = JsonSerializer.Deserialize<PartitionFile>(File.ReadAllText(path));
                if (file?.Format != 2 || file.Data == null || file.Checksum != ImageInputProjection.Hash(file.Data)) return null;
                var rows = JsonSerializer.Deserialize<ItemState[]>(file.Data);
                if (rows == null || rows.Any(e => string.IsNullOrEmpty(e.Identity) || Partition(e.Identity) != partition ||
                    e.Seed == null || e.Inputs == null || e.Bindings == null || e.Bindings.Count > 3 ||
                    e.Inputs.Values.Any(i => i.Light == null || i.Version == null || i.Dependencies == null))) return null;
                return rows;
            }
            catch (Exception) { return null; }
        }

        internal void Save(bool final = false)
        {
            if (_disposed != 0 && !final) return;
            lock (_saveGate)
            {
                int[] partitions;
                lock (_gate) { partitions = _dirty.ToArray(); _dirty.Clear(); }
                foreach (int partition in partitions)
                {
                    try
                    {
                        ItemState[] snapshot;
                        lock (_gate) snapshot = _buckets[partition].Values.Select(e => e.Copy()).OrderBy(e => e.Identity, StringComparer.Ordinal).ToArray();
                        var data = JsonSerializer.Serialize(snapshot);
                        string text = JsonSerializer.Serialize(new PartitionFile { Format = 2, Data = data, Checksum = ImageInputProjection.Hash(data) });
                        if (text.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Image state partition exceeds the file-size limit.");
                        Directory.CreateDirectory(_folder);
                        string path = Path.Combine(_folder, partition.ToString("x3") + ".json");
                        string temp = path + ".tmp";
                        File.WriteAllText(temp, text);
                        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
                        else File.Move(temp, path);
                    }
                    catch (Exception ex)
                    {
                        lock (_gate) _dirty.Add(partition);
                        _logger.Warn($"[EmbyIcons] Could not save image input partition {partition:x3}; it will retry: {ex.Message}");
                    }
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer?.Dispose();
            _saveTimer?.Dispose();
            _stop.Cancel();
            Save(final: true);
            var work = _work;
            if (work == null) _stop.Dispose();
            else _ = work.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        }

        internal Task? Initialization => _initialization;
        internal int TrackedCount => _items.Count;

        internal sealed class Inspection
        {
            internal string Fingerprint = string.Empty;
            internal long[] Dependencies = Array.Empty<long>();
            internal string DependencyStamp = string.Empty;
            internal string Source = string.Empty;
            internal bool Force;
        }
        public sealed class PartitionFile
        {
            public int Format { get; set; }
            public string? Data { get; set; }
            public string? Checksum { get; set; }
        }
        public sealed class ItemState
        {
            public string Identity { get; set; } = string.Empty;
            public long InternalId { get; set; }
            public string Kind { get; set; } = string.Empty;
            public string Seed { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string[] ProviderGroups { get; set; } = Array.Empty<string>();
            public Dictionary<string, InputState> Inputs { get; set; } = new Dictionary<string, InputState>(StringComparer.Ordinal);
            public Dictionary<int, KeyBinding> Bindings { get; set; } = new Dictionary<int, KeyBinding>();
            [System.Text.Json.Serialization.JsonIgnore] public WeakReference<BaseItem>? Recent { get; set; }
            [System.Text.Json.Serialization.JsonIgnore] public long Generation { get; set; }
            [System.Text.Json.Serialization.JsonIgnore] public long[] Dependencies { get; set; } = Array.Empty<long>();
            internal ItemState Copy() => new ItemState { Identity = Identity, InternalId = InternalId, Kind = Kind, Seed = Seed, Source = Source, ProviderGroups = ProviderGroups.ToArray(),
                Inputs = Inputs.ToDictionary(p => p.Key, p => p.Value.Copy()), Bindings = Bindings.ToDictionary(p => p.Key, p => p.Value.Copy()) };
        }
        public sealed class InputState
        {
            public string Light { get; set; } = string.Empty;
            public string? Heavy { get; set; }
            public string Version { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public bool Verified { get; set; }
            public bool Rendered { get; set; }
            public bool ForceInspection { get; set; }
            public string DependencyStamp { get; set; } = string.Empty;
            public long[] Dependencies { get; set; } = Array.Empty<long>();
            internal InputState Copy() => new InputState { Light = Light, Heavy = Heavy, Version = Version, Source = Source, Verified = Verified, Rendered = Rendered, ForceInspection = ForceInspection, DependencyStamp = DependencyStamp, Dependencies = Dependencies.ToArray() };
        }
        public sealed class KeyBinding
        {
            public bool Fallback { get; set; }
            public Guid? ProfileId { get; set; }
            public Guid OriginalProfileId { get; set; }
            public string Token { get; set; } = string.Empty;
            public string RatingHash { get; set; } = string.Empty;
            public long RatingVersion { get; set; }
            public string DataProjection { get; set; } = string.Empty;
            public string DataBaseline { get; set; } = string.Empty;
            public string DataAlias { get; set; } = string.Empty;
            public Dictionary<Guid, string> Fingerprints { get; set; } = new Dictionary<Guid, string>();
            internal KeyBinding Copy() => new KeyBinding { Fallback = Fallback, ProfileId = ProfileId, OriginalProfileId = OriginalProfileId,
                Token = Token, RatingHash = RatingHash, RatingVersion = RatingVersion, DataProjection = DataProjection, DataBaseline = DataBaseline, DataAlias = DataAlias, Fingerprints = new Dictionary<Guid, string>(Fingerprints) };
        }
    }
}
