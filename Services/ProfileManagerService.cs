﻿using EmbyIcons.Helpers;
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

        private const int MaxItemToProfileCacheSize = 20000;
        private MemoryCache _itemToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = MaxItemToProfileCacheSize });
    // Cache mapping from collection InternalId -> ProfileId (Guid.Empty means no profile)
    // This reduces repeated InternalItemsQuery calls for collections during a full scan.
    private MemoryCache _collectionToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = 5000 });
    private Timer? _cacheMaintenanceTimer;

        public ProfileManagerService(ILibraryManager libraryManager, ILogger logger, PluginOptions configuration)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _configuration = configuration;
            _libraryPathTrieLazy = new Lazy<Trie<string>>(CreateLibraryPathTrie);
            // Periodic maintenance to prevent cache buildup in long-running plugin instances
            _cacheMaintenanceTimer = new Timer(_ => CompactCaches(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        }

        public void InvalidateLibraryCache()
        {
            _libraryPathTrieLazy = new Lazy<Trie<string>>(CreateLibraryPathTrie);

            var oldCache = _itemToProfileIdCache;
            _itemToProfileIdCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxItemToProfileCacheSize });
            oldCache.Dispose();

            var oldCollectionCache = _collectionToProfileIdCache;
            _collectionToProfileIdCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 5000 });
            oldCollectionCache.Dispose();

            _logger.Info("[EmbyIcons] Library path and item profile caches have been invalidated.");
        }

        public void Dispose()
        {
            try
            {
                _itemToProfileIdCache?.Dispose();
            }
            catch { }

            try
            {
                _collectionToProfileIdCache?.Dispose();
            }
            catch { }
            try { _cacheMaintenanceTimer?.Dispose(); } catch { }
        }

        private void CompactCaches()
        {
            try
            {
                // Compact a small percentage to free unused entries; sizes are small so we use conservative values
                _collectionToProfileIdCache?.Compact(0.1);
                _itemToProfileIdCache?.Compact(0.05);
                if (Plugin.Instance?.Configuration.EnableDebugLogging ?? false) _logger.Debug("[EmbyIcons] Performed cache compaction for profile/collection caches.");
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
                if (cachedProfileId == Guid.Empty) return null; // A cached result of "no profile"
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
                    // Try collection-level cache first to avoid repeated expensive queries when scanning collections
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