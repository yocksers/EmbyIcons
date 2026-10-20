using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Services
{
    public class ProfileManagerService : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private volatile PluginOptions _configuration;

        private volatile Trie<string>? _libraryPathTrie;
        private readonly SemaphoreSlim _libraryPathTrieSemaphore = new SemaphoreSlim(1, 1);
        [ThreadStatic]
        private static bool _isCreatingTrie;

        private MemoryCache _itemToProfileIdCache;
        private MemoryCache _collectionToProfileIdCache;
        private Timer? _cacheMaintenanceTimer;
        private long _collectionGeneration;
        private readonly ImageCacheState? _state;

        // With saved image state, resolved collection profiles survive restarts. Rendering requires a
        // result for the collection's current revision; image keys accept the last known profile.
        private bool TryGetCollectionProfile(long id, bool requireCurrent, out Guid profileId)
        {
            if (_state != null) return _state.TryGetCollectionProfile(id, requireCurrent, out profileId);
            profileId = Guid.Empty;
            if (!_collectionToProfileIdCache.TryGetValue(id, out (Guid Id, long Generation) cached) ||
                cached.Generation != Volatile.Read(ref _collectionGeneration)) return false;
            profileId = cached.Id;
            return true;
        }

        public ProfileManagerService(ILibraryManager libraryManager, ILogger logger, PluginOptions configuration)
            : this(libraryManager, logger, configuration, null)
        {
        }

        internal ProfileManagerService(ILibraryManager libraryManager, ILogger logger, PluginOptions configuration, ImageCacheState? state)
        {
            _state = state;
            _libraryManager = libraryManager;
            _logger = logger;
            _configuration = configuration;
            var maxItemCacheSize = Math.Max(1000, configuration.MaxItemToProfileCacheSize);
            var maxCollectionCacheSize = Math.Max(100, configuration.MaxCollectionToProfileCacheSize);
            _itemToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = maxItemCacheSize });
            _collectionToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = maxCollectionCacheSize });
            
            var maintenanceInterval = TimeSpan.FromHours(Math.Max(0.5, configuration.CacheMaintenanceIntervalHours));
            _cacheMaintenanceTimer = new Timer(_ => CompactCaches(), null, maintenanceInterval, maintenanceInterval);
        }

        // Cache-only path for image tags. Never build the trie or resolve collection members here.
        public bool TryGetCachedProfile(BaseItem item, out IconProfile? profile)
        {
            profile = null;
            if (item is BoxSet boxSet)
            {
                var mapping = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == boxSet.ParentId.ToString());
                if (mapping != null)
                {
                    profile = _configuration.Profiles.FirstOrDefault(p => p.Id == mapping.ProfileId);
                    return true;
                }
                if (!_configuration.EnableCollectionProfileLookup) return true;
                if (!TryGetCollectionProfile(boxSet.InternalId, requireCurrent: false, out Guid id)) return false;
                profile = _configuration.Profiles.FirstOrDefault(p => p.Id == id);
                return true;
            }
            var trie = _libraryPathTrie;
            if (trie == null) return false;
            if (string.IsNullOrEmpty(item.Path)) return false;
            var libraryId = trie.FindLongestPrefix(item.Path);
            var match = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == libraryId);
            if (match != null) profile = _configuration.Profiles.FirstOrDefault(p => p.Id == match.ProfileId);
            return true;
        }

        public Task WarmLibraryPathsAsync() => Task.Run(WarmLibraryPaths);

        public void WarmLibraryPaths()
        {
            try { GetOrCreateLibraryPathTrie(); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { _logger.Warn($"[EmbyIcons] Library profile warm-up will retry on image access: {ex.Message}"); }
        }

        internal void InvalidateCollectionProfiles() => Interlocked.Increment(ref _collectionGeneration);

        public void UseConfiguration(PluginOptions configuration)
        {
            _configuration = configuration;
        }

        public void InvalidateLibraryCache()
        {
            // Build the replacement before swapping it in, so image keys never see a missing trie.
            _libraryPathTrieSemaphore.Wait();
            try
            {
                _isCreatingTrie = true;
                try
                {
                    _libraryPathTrie = CreateLibraryPathTrie();
                }
                catch (Exception ex)
                {
                    _libraryPathTrie = null;
                    _logger.Warn($"[EmbyIcons] Library profile paths will be rebuilt on image access: {ex.Message}");
                }
                finally
                {
                    _isCreatingTrie = false;
                }
            }
            finally
            {
                _libraryPathTrieSemaphore.Release();
            }

            _state?.InvalidateCollectionProfiles();

            var maxItemCacheSize = Math.Max(1000, _configuration.MaxItemToProfileCacheSize);
            var maxCollectionCacheSize = Math.Max(100, _configuration.MaxCollectionToProfileCacheSize);
            
            var oldCache = Interlocked.Exchange(ref _itemToProfileIdCache, new MemoryCache(new MemoryCacheOptions { SizeLimit = maxItemCacheSize }));
            try { oldCache?.Dispose(); } catch (Exception ex) { _logger.Debug($"[EmbyIcons] Error disposing old item cache: {ex.Message}"); }

            var oldCollectionCache = Interlocked.Exchange(ref _collectionToProfileIdCache, new MemoryCache(new MemoryCacheOptions { SizeLimit = maxCollectionCacheSize }));
            try { oldCollectionCache?.Dispose(); } catch (Exception ex) { _logger.Debug($"[EmbyIcons] Error disposing old collection cache: {ex.Message}"); }

            _logger.Info("[EmbyIcons] Library path and item profile caches have been invalidated.");
        }

        public void Dispose()
        {
            try
            {
                _itemToProfileIdCache?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug($"[EmbyIcons] Error disposing item profile cache: {ex.Message}");
            }

            try
            {
                _collectionToProfileIdCache?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug($"[EmbyIcons] Error disposing collection profile cache: {ex.Message}");
            }
            
            try { _cacheMaintenanceTimer?.Dispose(); } catch (Exception ex) { _logger.Debug($"[EmbyIcons] Error disposing cache maintenance timer: {ex.Message}"); }

            try { _libraryPathTrieSemaphore?.Dispose(); } catch (Exception ex) { _logger.Debug($"[EmbyIcons] Error disposing library path trie semaphore: {ex.Message}"); }
        }

        private void CompactCaches()
        {
            try
            {
                _collectionToProfileIdCache?.Compact(0.1);
                _itemToProfileIdCache?.Compact(0.05);
                if (Helpers.PluginHelper.IsDebugLoggingEnabled) 
                    _logger.Debug("[EmbyIcons] Performed cache compaction for profile/collection caches.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] Error during cache compaction.", ex);
            }
        }

        private Trie<string> GetOrCreateLibraryPathTrie()
        {
            if (_libraryPathTrie != null) return _libraryPathTrie;
            if (_isCreatingTrie) return new Trie<string>();

            _libraryPathTrieSemaphore.Wait();
            try
            {
                if (_libraryPathTrie != null) return _libraryPathTrie;
                _isCreatingTrie = true;
                try
                {
                    _libraryPathTrie = CreateLibraryPathTrie();
                }
                finally
                {
                    _isCreatingTrie = false;
                }
                return _libraryPathTrie;
            }
            finally
            {
                _libraryPathTrieSemaphore.Release();
            }
        }

        private async Task<Trie<string>> GetOrCreateLibraryPathTrieAsync()
        {
            if (_libraryPathTrie != null) return _libraryPathTrie;
            if (_isCreatingTrie) return new Trie<string>();

            await _libraryPathTrieSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_libraryPathTrie != null) return _libraryPathTrie;
                _isCreatingTrie = true;
                try
                {
                    _libraryPathTrie = CreateLibraryPathTrie();
                }
                finally
                {
                    _isCreatingTrie = false;
                }
                return _libraryPathTrie;
            }
            finally
            {
                _libraryPathTrieSemaphore.Release();
            }
        }

        private Trie<string> CreateLibraryPathTrie()
        {
            _logger.Debug("[EmbyIcons] Populating library path cache using Trie.");
            var newTrie = new Trie<string>();
            try
            {
                var libraries = _libraryManager.GetVirtualFolders()
                    .Where(lib => lib != null && lib.Locations != null)
                    .SelectMany(lib => lib.Locations!.Select(loc => (Path: loc, lib.Name, lib.Id)))
                    .Where(libInfo => !string.IsNullOrEmpty(libInfo.Path))
                    .OrderByDescending(libInfo => libInfo.Path.Length);

                foreach (var libInfo in libraries)
                {
                    newTrie.Insert(libInfo.Path, libInfo.Id.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] CRITICAL: Failed to get virtual folders from LibraryManager.", ex);
                throw;
            }

            return newTrie;
        }

        private IconProfile? GetProfileForPath(string? path)
        {
            if (path == null || path.Length == 0)
            {
                return null;
            }

            var currentLibraryTrie = GetOrCreateLibraryPathTrie();

            var libraryId = currentLibraryTrie.FindLongestPrefix(path);

            if (libraryId != null)
            {
                var mapping = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == libraryId);
                if (mapping != null)
                {
                    return _configuration.Profiles?.FirstOrDefault(p => p.Id == mapping.ProfileId);
                }
            }

            return null;
        }

        private async Task<IconProfile?> GetProfileForPathAsync(string? path)
        {
            if (path == null || path.Length == 0)
            {
                return null;
            }

            var currentLibraryTrie = await GetOrCreateLibraryPathTrieAsync().ConfigureAwait(false);

            var libraryId = currentLibraryTrie.FindLongestPrefix(path);

            if (libraryId != null)
            {
                var mapping = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == libraryId);
                if (mapping != null)
                {
                    return _configuration.Profiles?.FirstOrDefault(p => p.Id == mapping.ProfileId);
                }
            }

            return null;
        }

        public IconProfile? GetProfileForItem(BaseItem item)
        {
            if (!(item is BoxSet) && item.Id != Guid.Empty && _itemToProfileIdCache.TryGetValue(item.Id, out Guid cachedProfileId))
            {
                if (cachedProfileId == Guid.Empty) return null; 
                return _configuration.Profiles?.FirstOrDefault(p => p.Id == cachedProfileId);
            }

            IconProfile? foundProfile;

            if (item is BoxSet boxSet)
            {
                var mapping = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == boxSet.ParentId.ToString());
                if (mapping != null)
                {
                    foundProfile = _configuration.Profiles?.FirstOrDefault(p => p.Id == mapping.ProfileId);
                }
                else if (_configuration.EnableCollectionProfileLookup)
                {
                    if (TryGetCollectionProfile(boxSet.InternalId, requireCurrent: true, out Guid cachedCollectionProfileId))
                    {
                        foundProfile = cachedCollectionProfileId == Guid.Empty ? null : _configuration.Profiles?.FirstOrDefault(p => p.Id == cachedCollectionProfileId);
                    }
                    else
                    {
                        var generation = Volatile.Read(ref _collectionGeneration);
                        var revision = _state?.CollectionRevision(boxSet.InternalId) ?? 0;
                        var firstChild = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            CollectionIds = new[] { boxSet.InternalId },
                            Limit = 1,
                            Recursive = true,
                            IncludeItemTypes = new[] { "Movie", "Series", "Episode" }
                        }).FirstOrDefault();

                        if (firstChild != null)
                        {
                            foundProfile = GetProfileForPath(firstChild.Path);
                        }
                        else
                        {
                            if (_configuration.EnableDebugLogging)
                                _logger.Warn($"[EmbyIcons] Collection '{boxSet.Name}' (ID: {boxSet.Id}) is empty. Cannot determine library profile.");
                            foundProfile = null;
                        }

                        var profileIdToCache = foundProfile?.Id ?? Guid.Empty;
                        var cacheEntryOptions = new MemoryCacheEntryOptions()
                            .SetSize(1)
                            .SetSlidingExpiration(TimeSpan.FromHours(6));
                        if (_state != null) _state.SetCollectionProfile(boxSet.InternalId, profileIdToCache, revision, boxSet);
                        else _collectionToProfileIdCache.Set(boxSet.InternalId, (profileIdToCache, generation), cacheEntryOptions);
                    }
                }
                else
                {
                    foundProfile = null;
                }
            }
            else
            {
                foundProfile = GetProfileForPath(item.Path);
            }

            if (!(item is BoxSet) && item.Id != Guid.Empty)
            {
                var profileIdToCache = foundProfile?.Id ?? Guid.Empty;
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetSlidingExpiration(TimeSpan.FromDays(1));

                _itemToProfileIdCache.Set(item.Id, profileIdToCache, cacheEntryOptions);
            }

            return foundProfile;
        }

        public async Task<IconProfile?> GetProfileForItemAsync(BaseItem item)
        {
            if (!(item is BoxSet) && item.Id != Guid.Empty && _itemToProfileIdCache.TryGetValue(item.Id, out Guid cachedProfileId))
            {
                if (cachedProfileId == Guid.Empty) return null;
                return _configuration.Profiles?.FirstOrDefault(p => p.Id == cachedProfileId);
            }

            IconProfile? foundProfile;

            if (item is BoxSet boxSet)
            {
                var mapping = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == boxSet.ParentId.ToString());
                if (mapping != null)
                {
                    foundProfile = _configuration.Profiles?.FirstOrDefault(p => p.Id == mapping.ProfileId);
                }
                else if (_configuration.EnableCollectionProfileLookup)
                {
                    if (TryGetCollectionProfile(boxSet.InternalId, requireCurrent: true, out Guid cachedCollectionProfileId))
                    {
                        foundProfile = cachedCollectionProfileId == Guid.Empty ? null : _configuration.Profiles?.FirstOrDefault(p => p.Id == cachedCollectionProfileId);
                    }
                    else
                    {
                        var generation = Volatile.Read(ref _collectionGeneration);
                        var revision = _state?.CollectionRevision(boxSet.InternalId) ?? 0;
                        var firstChild = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            CollectionIds = new[] { boxSet.InternalId },
                            Limit = 1,
                            Recursive = true,
                            IncludeItemTypes = new[] { "Movie", "Series", "Episode" }
                        }).FirstOrDefault();

                        if (firstChild != null)
                        {
                            foundProfile = await GetProfileForPathAsync(firstChild.Path).ConfigureAwait(false);
                        }
                        else
                        {
                            if (_configuration.EnableDebugLogging)
                                _logger.Warn($"[EmbyIcons] Collection '{boxSet.Name}' (ID: {boxSet.Id}) is empty. Cannot determine library profile.");
                            foundProfile = null;
                        }

                        var profileIdToCache = foundProfile?.Id ?? Guid.Empty;
                        var cacheEntryOptions = new MemoryCacheEntryOptions()
                            .SetSize(1)
                            .SetSlidingExpiration(TimeSpan.FromHours(6));
                        if (_state != null) _state.SetCollectionProfile(boxSet.InternalId, profileIdToCache, revision, boxSet);
                        else _collectionToProfileIdCache.Set(boxSet.InternalId, (profileIdToCache, generation), cacheEntryOptions);
                    }
                }
                else
                {
                    foundProfile = null;
                }
            }
            else
            {
                foundProfile = await GetProfileForPathAsync(item.Path).ConfigureAwait(false);
            }

            if (!(item is BoxSet) && item.Id != Guid.Empty)
            {
                var profileIdToCache = foundProfile?.Id ?? Guid.Empty;
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetSize(1)
                    .SetSlidingExpiration(TimeSpan.FromDays(1));
                _itemToProfileIdCache.Set(item.Id, profileIdToCache, cacheEntryOptions);
            }

            return foundProfile;
        }
    }
}