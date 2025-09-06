using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Linq;

namespace EmbyIcons.Services
{
    public class ProfileManagerService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly PluginOptions _configuration;

        private static Trie<string>? _libraryPathTrie;
        private static readonly object _libraryPathCacheLock = new object();

        private const int MaxItemToProfileCacheSize = 20000;
        private static MemoryCache _itemToProfileIdCache = new(new MemoryCacheOptions { SizeLimit = MaxItemToProfileCacheSize });

        public ProfileManagerService(ILibraryManager libraryManager, ILogger logger, PluginOptions configuration)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _configuration = configuration;
        }

        public void InvalidateLibraryCache()
        {
            lock (_libraryPathCacheLock)
            {
                _libraryPathTrie = null;
                var oldCache = _itemToProfileIdCache;
                _itemToProfileIdCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxItemToProfileCacheSize });
                oldCache.Dispose();

                _logger.Info("[EmbyIcons] Library path and item profile caches have been invalidated.");
            }
        }

        private void PopulateLibraryPathCache()
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

            lock (_libraryPathCacheLock)
            {
                _libraryPathTrie = newTrie;
            }
        }

        private IconProfile? GetProfileForPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_libraryPathCacheLock)
            {
                if (_libraryPathTrie == null)
                {
                    PopulateLibraryPathCache();
                }
            }

            var currentLibraryTrie = _libraryPathTrie;
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