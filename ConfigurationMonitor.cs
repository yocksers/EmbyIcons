using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;

namespace EmbyIcons.Services
{
    public class ConfigurationMonitor
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;

        /// <param name="logger">The logger.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="fileSystem">The file system.</param>
        public ConfigurationMonitor(ILogger logger, ILibraryManager libraryManager, IFileSystem fileSystem)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
        }

        /// <param name="oldOptions">The plugin options before the change.</param>
        /// <param name="newOptions">The plugin options after the change.</param>
        public void CheckForChangesAndTriggerRefreshes(PluginOptions oldOptions, PluginOptions newOptions)
        {
            var oldProfileDict = oldOptions.Profiles.ToDictionary(p => p.Id);
            var newProfileDict = newOptions.Profiles.ToDictionary(p => p.Id);

            var oldMappings = oldOptions.LibraryProfileMappings.ToDictionary(m => m.LibraryId);
            var newMappings = newOptions.LibraryProfileMappings.ToDictionary(m => m.LibraryId);

            var allLibraryIds = oldMappings.Keys.Union(newMappings.Keys).Distinct().ToList();

            var seriesLibsToRefresh = new List<string>();
            var episodeLibsToRefresh = new List<string>();
            var collectionLibsToRefresh = new List<string>();

            foreach (var libId in allLibraryIds)
            {
                oldMappings.TryGetValue(libId, out var oldMap);
                newMappings.TryGetValue(libId, out var newMap);

                IconProfile? oldProfile = null;
                if (oldMap != null) oldProfileDict.TryGetValue(oldMap.ProfileId, out oldProfile);

                IconProfile? newProfile = null;
                if (newMap != null) newProfileDict.TryGetValue(newMap.ProfileId, out newProfile);

                if (oldProfile?.Id == newProfile?.Id) continue; // No change in profile assignment or still no profile

                if (IsTypeSupportedByProfile(oldProfile, "Series") && !IsTypeSupportedByProfile(newProfile, "Series"))
                {
                    seriesLibsToRefresh.Add(libId);
                }
                if (IsTypeSupportedByProfile(oldProfile, "Episode") && !IsTypeSupportedByProfile(newProfile, "Episode"))
                {
                    episodeLibsToRefresh.Add(libId);
                }
                if (IsTypeSupportedByProfile(oldProfile, "BoxSet") && !IsTypeSupportedByProfile(newProfile, "BoxSet"))
                {
                    collectionLibsToRefresh.Add(libId);
                }
            }

            var allItemsToRefresh = new List<BaseItem>();

            if (seriesLibsToRefresh.Any())
            {
                var ancestorIds = seriesLibsToRefresh
                    .Select(guidString => Guid.TryParse(guidString, out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty)
                    .Select(guid => _libraryManager.GetItemById(guid))
                    .Where(item => item != null)
                    .Select(item => item!.InternalId)
                    .ToArray();

                if (ancestorIds.Any())
                {
                    _logger.Info($"[EmbyIcons] Queuing Series in {ancestorIds.Length} libraries for refresh.");
                    allItemsToRefresh.AddRange(_libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = ancestorIds, IncludeItemTypes = new[] { "Series" }, Recursive = true }));
                }
            }
            if (episodeLibsToRefresh.Any())
            {
                var ancestorIds = episodeLibsToRefresh
                    .Select(guidString => Guid.TryParse(guidString, out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty)
                    .Select(guid => _libraryManager.GetItemById(guid))
                    .Where(item => item != null)
                    .Select(item => item!.InternalId)
                    .ToArray();

                if (ancestorIds.Any())
                {
                    _logger.Info($"[EmbyIcons] Queuing Episodes in {ancestorIds.Length} libraries for refresh.");
                    allItemsToRefresh.AddRange(_libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = ancestorIds, IncludeItemTypes = new[] { "Episode" }, Recursive = true }));
                }
            }
            if (collectionLibsToRefresh.Any())
            {
                var ancestorIds = collectionLibsToRefresh
                    .Select(guidString => Guid.TryParse(guidString, out var guid) ? guid : Guid.Empty)
                    .Where(guid => guid != Guid.Empty)
                    .Select(guid => _libraryManager.GetItemById(guid))
                    .Where(item => item != null)
                    .Select(item => item!.InternalId)
                    .ToArray();

                if (ancestorIds.Any())
                {
                    _logger.Info($"[EmbyIcons] Queuing BoxSets in {ancestorIds.Length} libraries for refresh.");
                    allItemsToRefresh.AddRange(_libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = ancestorIds, IncludeItemTypes = new[] { "BoxSet" }, Recursive = true }));
                }
            }

            RefreshItems(allItemsToRefresh.DistinctBy(i => i.Id).ToList());
        }

        private bool IsTypeSupportedByProfile(IconProfile? profile, string itemType)
        {
            if (profile == null) return false;

            var s = profile.Settings;
            if (!s.EnableForPosters) return false; // Assuming primary posters for now

            if (itemType == "Episode" && !s.ShowOverlaysForEpisodes) return false;
            if (itemType == "Series" && !s.ShowSeriesIconsIfAllEpisodesHaveLanguage) return false;
            if (itemType == "BoxSet" && !s.ShowCollectionIconsIfAllChildrenHaveLanguage) return false;

            return s.AudioIconAlignment != IconAlignment.Disabled || s.SubtitleIconAlignment != IconAlignment.Disabled ||
                   s.ChannelIconAlignment != IconAlignment.Disabled || s.VideoFormatIconAlignment != IconAlignment.Disabled ||
                   s.ResolutionIconAlignment != IconAlignment.Disabled || s.CommunityScoreIconAlignment != IconAlignment.Disabled ||
                   s.AudioCodecIconAlignment != IconAlignment.Disabled || s.VideoCodecIconAlignment != IconAlignment.Disabled ||
                   s.TagIconAlignment != IconAlignment.Disabled || s.AspectRatioIconAlignment != IconAlignment.Disabled ||
                   s.ParentalRatingIconAlignment != IconAlignment.Disabled;
        }

        private void RefreshItems(IReadOnlyList<BaseItem> items)
        {
            if (!items.Any()) return;

            _logger.Info($"[EmbyIcons] Triggering image refresh for {items.Count} items due to a configuration change. This may take some time.");

            Task.Run(() => {
                var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = true
                };

                foreach (var item in items)
                {
                    item.RefreshMetadata(refreshOptions, CancellationToken.None);
                }
                _logger.Info($"[EmbyIcons] Queued refresh for {items.Count} items.");
            });
        }
    }
}