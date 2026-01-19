using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Linq;
using System.Threading;

namespace EmbyIcons.Services
{
    public class ProfileManagerService : IDisposable
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly PluginOptions _configuration;

        private Lazy<Trie<string>> _libraryPathTrieLazy;

        private MemoryCache _itemToProfileIdCache;
        private MemoryCache _collectionToProfileIdCache;
        private Timer? _cacheMaintenanceTimer;

        public ProfileManagerService(ILibraryManager libraryManager, ILogger logger, PluginOptions configuration)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _configuration = configuration;
            _libraryPathTrieLazy = new Lazy<Trie<string>>(CreateLibraryPathTrie);
            
            var maxItemCacheSize = Math.Max(1000, configuration.MaxItemToProfileCacheSize);
            var maxCollectionCacheSize = Math.Max(100, configuration.MaxCollectionToProfileCacheSize);
            _itemToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = maxItemCacheSize });
            _collectionToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = maxCollectionCacheSize });
            
            var maintenanceInterval = TimeSpan.FromHours(Math.Max(0.5, configuration.CacheMaintenanceIntervalHours));
            _cacheMaintenanceTimer = new Timer(_ => CompactCaches(), null, maintenanceInterval, maintenanceInterval);
        }

        public void InvalidateLibraryCache()
        {
            if (_libraryPathTrieLazy.IsValueCreated)
            {
                try
                {
                    var oldTrie = _libraryPathTrieLazy.Value;
                    oldTrie?.Clear();
                }
                catch (Exception ex)
                {
                    _logger.Debug($"[EmbyIcons] Error clearing old trie: {ex.Message}");
                }
            }
            
            _libraryPathTrieLazy = new Lazy<Trie<string>>(CreateLibraryPathTrie);

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
            }

            return newTrie;
        }

        private IconProfile? GetProfileForPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var currentLibraryTrie = _libraryPathTrieLazy.Value;

            if (currentLibraryTrie == null)
            {
                _logger.Warn("[EmbyIcons] Library path Trie is null, cannot check library restrictions.");
                return null;
            }

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
            if (item.Id != Guid.Empty && _itemToProfileIdCache.TryGetValue(item.Id, out Guid cachedProfileId))
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
                    if (_collectionToProfileIdCache.TryGetValue(boxSet.InternalId, out Guid cachedCollectionProfileId))
                    {
                        foundProfile = cachedCollectionProfileId == Guid.Empty ? null : _configuration.Profiles?.FirstOrDefault(p => p.Id == cachedCollectionProfileId);
                    }
                    else
                    {
                        var firstChild = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            CollectionIds = new[] { boxSet.InternalId },
                            Limit = 1,
                            Recursive = true,
                            IncludeItemTypes = new[] { "Movie", "Episode" }
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
                        _collectionToProfileIdCache.Set(boxSet.InternalId, profileIdToCache, cacheEntryOptions);
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

            if (item.Id != Guid.Empty)
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