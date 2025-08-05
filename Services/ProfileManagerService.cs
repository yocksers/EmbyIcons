using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Services
{
    public class ProfileManagerService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly PluginOptions _configuration;

        private static List<(string Path, string Name, string Id)>? _libraryPathCache;
        private static readonly object _libraryPathCacheLock = new object();

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
                _libraryPathCache = null;
                _logger.Info("[EmbyIcons] Library path cache has been invalidated.");
            }
        }

        private void PopulateLibraryPathCache()
        {
            _logger.Debug("[EmbyIcons] Populating library path cache.");
            var newCache = new List<(string Path, string Name, string Id)>();
            try
            {
                foreach (var lib in _libraryManager.GetVirtualFolders())
                {
                    if (lib?.Locations == null) continue;
                    foreach (var loc in lib.Locations)
                    {
                        if (!string.IsNullOrEmpty(loc))
                        {
                            newCache.Add((loc, lib.Name, lib.Id.ToString()));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[EmbyIcons] CRITICAL: Failed to get virtual folders from LibraryManager.", ex);
            }

            lock (_libraryPathCacheLock)
            {
                _libraryPathCache = newCache.OrderByDescending(i => i.Path.Length).ToList();
            }
        }

        private IconProfile? GetProfileForPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            lock (_libraryPathCacheLock)
            {
                if (_libraryPathCache == null)
                {
                    PopulateLibraryPathCache();
                }
            }

            var currentLibraryCache = _libraryPathCache;
            if (currentLibraryCache == null)
            {
                _logger.Warn("[EmbyIcons] Library path cache is null, cannot check library restrictions.");
                return null;
            }

            foreach (var libInfo in currentLibraryCache)
            {
                if (path.StartsWith(libInfo.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var mapping = _configuration.LibraryProfileMappings.FirstOrDefault(m => m.LibraryId == libInfo.Id);
                    if (mapping != null)
                    {
                        return _configuration.Profiles?.FirstOrDefault(p => p.Id == mapping.ProfileId);
                    }
                    return null;
                }
            }

            return null;
        }

        public IconProfile? GetProfileForItem(BaseItem item)
        {
            if (item is BoxSet boxSet)
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
                    return GetProfileForPath(firstChild.Path);
                }

                _logger.Warn($"[EmbyIcons] Collection '{boxSet.Name}' (ID: {boxSet.Id}) is empty. Cannot determine library profile for icon processing.");
                return null;
            }

            return GetProfileForPath(item.Path);
        }
    }
}