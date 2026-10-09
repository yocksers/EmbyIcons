using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;

namespace EmbyIcons.Caching
{
    internal sealed class StoredSummary<T> where T : class
    {
        public string Signature { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public DateTime LastUsed { get; set; }
        public T? Data { get; set; }
    }

    internal sealed class SummaryStoreFile<T> where T : class
    {
        public int Version { get; set; }
        public Dictionary<Guid, StoredSummary<T>> Entries { get; set; } = new Dictionary<Guid, StoredSummary<T>>();
    }

    internal sealed class SummaryDiskStore<T> where T : class
    {
        private const int FormatVersion = 1;
        private const int MaxEntries = 100000;
        private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan LastUsedResolution = TimeSpan.FromDays(1);
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private readonly string _fileName;
        private readonly ConcurrentDictionary<Guid, StoredSummary<T>> _entries = new ConcurrentDictionary<Guid, StoredSummary<T>>();
        private readonly object _stateGate = new object();
        private readonly object _saveGate = new object();
        private Timer? _saveTimer;
        private Task? _loadTask;
        private int _generation;
        private int _dirty;

        public SummaryDiskStore(string fileName)
        {
            _fileName = fileName;
        }

        public static bool IsEnabled => Plugin.Instance?.Configuration.EnableSummaryDiskCache ?? false;

        public void StartLoading()
        {
            if (!IsEnabled) return;

            lock (_stateGate)
            {
                if (_saveTimer == null)
                {
                    _saveTimer = new Timer(_ => Save(), null, SaveInterval, SaveInterval);
                }

                if (_loadTask != null) return;
                var generation = Volatile.Read(ref _generation);
                _loadTask = Task.Run(() => Load(generation));
            }
        }

        public bool TryGet(Guid id, string signature, string fingerprint, out T? data)
        {
            data = null;
            if (!IsEnabled) return false;
            StartLoading();

            if (!_entries.TryGetValue(id, out var entry) || entry.Data == null) return false;
            if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                !string.Equals(entry.Signature, signature, StringComparison.Ordinal))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if (now - entry.LastUsed > LastUsedResolution)
            {
                var touched = new StoredSummary<T> { Signature = entry.Signature, Fingerprint = entry.Fingerprint, LastUsed = now, Data = entry.Data };
                if (_entries.TryUpdate(id, touched, entry))
                {
                    Interlocked.Exchange(ref _dirty, 1);
                }
            }

            data = entry.Data;
            return true;
        }

        public void Set(Guid id, string signature, string fingerprint, T data)
        {
            if (!IsEnabled) return;
            StartLoading();

            _entries[id] = new StoredSummary<T> { Signature = signature, Fingerprint = fingerprint, LastUsed = DateTime.UtcNow, Data = data };
            Interlocked.Exchange(ref _dirty, 1);
        }

        public void Remove(Guid id)
        {
            if (_entries.TryRemove(id, out _))
            {
                Interlocked.Exchange(ref _dirty, 1);
            }
        }

        public void Clear()
        {
            lock (_saveGate)
            {
                Interlocked.Increment(ref _generation);
                _entries.Clear();
                Interlocked.Exchange(ref _dirty, 0);

                var path = GetPath();
                if (path == null) return;

                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Could not delete '{path}': {ex.Message}");
                }
            }
        }

        public void Save()
        {
            try
            {
                if (!IsEnabled) return;

                var loadTask = _loadTask;
                if (loadTask != null && !loadTask.IsCompleted) return;

                lock (_saveGate)
                {
                    if (Interlocked.Exchange(ref _dirty, 0) == 0) return;

                    var path = GetPath();
                    if (path == null) return;

                    Prune();
                    var snapshot = new Dictionary<Guid, StoredSummary<T>>(_entries.Count);
                    foreach (var pair in _entries)
                    {
                        snapshot[pair.Key] = pair.Value;
                    }

                    var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                        {
                            JsonSerializer.Serialize(stream, new SummaryStoreFile<T> { Version = FormatVersion, Entries = snapshot }, JsonOptions);
                        }

                        if (File.Exists(path))
                        {
                            File.Replace(tempPath, path, null);
                        }
                        else
                        {
                            File.Move(tempPath, path);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref _dirty, 1);
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                        if (PluginHelper.IsDebugLoggingEnabled)
                        {
                            Plugin.Instance?.Logger.Debug($"[EmbyIcons] Could not save summaries to '{path}': {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (PluginHelper.IsDebugLoggingEnabled)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Error while saving summaries: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            lock (_stateGate)
            {
                try { _saveTimer?.Dispose(); } catch { }
                _saveTimer = null;
            }

            Save();
        }

        private string? GetPath()
        {
            var folder = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, _fileName);
        }

        private void Prune()
        {
            var cutoff = DateTime.UtcNow - UnusedLifetime;
            foreach (var pair in _entries)
            {
                if (pair.Value.LastUsed < cutoff)
                {
                    _entries.TryRemove(pair.Key, out _);
                }
            }

            var excess = _entries.Count - MaxEntries;
            if (excess > 0)
            {
                foreach (var key in _entries.ToArray().OrderBy(p => p.Value.LastUsed).Take(excess).Select(p => p.Key).ToList())
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }

        private void Load(int generation)
        {
            var path = GetPath();
            if (path == null || !File.Exists(path)) return;

            try
            {
                SummaryStoreFile<T>? file;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
                {
                    file = JsonSerializer.Deserialize<SummaryStoreFile<T>>(stream, JsonOptions);
                }
                if (file?.Entries == null || file.Version != FormatVersion) return;

                var cutoff = DateTime.UtcNow - UnusedLifetime;
                var sharedSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
                int loaded = 0;

                lock (_saveGate)
                {
                    if (Volatile.Read(ref _generation) != generation) return;

                    foreach (var pair in file.Entries)
                    {
                        var entry = pair.Value;
                        if (entry?.Data == null || entry.LastUsed < cutoff) continue;

                        if (sharedSignatures.TryGetValue(entry.Signature, out var shared))
                        {
                            entry.Signature = shared;
                        }
                        else
                        {
                            sharedSignatures[entry.Signature] = entry.Signature;
                        }

                        if (_entries.TryAdd(pair.Key, entry)) loaded++;
                    }
                }

                if (PluginHelper.IsDebugLoggingEnabled)
                {
                    Plugin.Instance?.Logger.Debug($"[EmbyIcons] Loaded {loaded} saved summaries from '{path}'.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger.Warn($"[EmbyIcons] The saved summaries in '{path}' could not be read and will be rebuilt: {ex.Message}");
            }
        }
    }

    internal static class SummaryStoreKeys
    {
        private static readonly ConditionalWeakTable<ProfileSettings, string> _profileSignatures = new ConditionalWeakTable<ProfileSettings, string>();
        private static readonly string _pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty;

        public static string GetSignature(ProfileSettings profileOptions, PluginOptions globalOptions, IEnumerable<string>? resolutionKeys)
        {
            var profilePart = _profileSignatures.GetValue(profileOptions, SettingsChangeAnalyzer.DataSignature);

            var text = new StringBuilder(profilePart.Length + 256)
                .Append(_pluginVersion)
                .Append('|').Append(globalOptions.IconsFolder ?? string.Empty)
                .Append('|').Append((int)globalOptions.IconLoadingMode)
                .Append('|').Append(profilePart)
                .Append('|');

            if (resolutionKeys != null)
            {
                foreach (var key in resolutionKeys)
                {
                    text.Append(key).Append(',');
                }
            }

            return Hash(text.ToString());
        }

        public static string GetFingerprint(BaseItem parent, IReadOnlyList<BaseItem> children)
        {
            var text = new StringBuilder(64 + children.Count * 96)
                .Append(parent.Path ?? string.Empty)
                .Append('|').Append(children.Count)
                .Append('|');

            foreach (var child in children.OrderBy(c => c.InternalId))
            {
                text.Append(child.InternalId)
                    .Append(':').Append(child.DateLastSaved.UtcTicks)
                    .Append(':').Append(child.DateModified.UtcTicks)
                    .Append(':').Append(child.Path ?? string.Empty)
                    .Append('|');
            }

            return Hash(text.ToString());
        }

        public static string[]? Pack(HashSet<string>? values)
        {
            return values == null || values.Count == 0 ? null : values.ToArray();
        }

        public static HashSet<string> Unpack(string[]? values)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values != null)
            {
                foreach (var value in values)
                {
                    set.Add(value);
                }
            }
            return set;
        }

        private static string Hash(string text)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }
    }
}
