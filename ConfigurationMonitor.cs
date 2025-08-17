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

        public ConfigurationMonitor(ILogger logger, ILibraryManager libraryManager, IFileSystem fileSystem)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
        }

        public void CheckForChangesAndTriggerRefreshes(PluginOptions oldOptions, PluginOptions newOptions)
        {
            var oldProfileDict = oldOptions.Profiles.ToDictionary(p => p.Id);
            var newProfileDict = newOptions.Profiles.ToDictionary(p => p.Id);

            var oldMappings = oldOptions.LibraryProfileMappings.ToDictionary(m => m.LibraryId);
            var newMappings = newOptions.LibraryProfileMappings.ToDictionary(m => m.LibraryId);

            var allLibraryIds = oldMappings.Keys.Union(newMappings.Keys).Distinct().ToList();

            var libsForHardRefresh = new List<string>();
            var libsForSoftRefresh = new List<string>();

            foreach (var libId in allLibraryIds)
            {
                oldMappings.TryGetValue(libId, out var oldMap);
                newMappings.TryGetValue(libId, out var newMap);

                IconProfile? oldProfile = null;
                if (oldMap != null) oldProfileDict.TryGetValue(oldMap.ProfileId, out oldProfile);

                IconProfile? newProfile = null;
                if (newMap != null) newProfileDict.TryGetValue(newMap.ProfileId, out newProfile);

                if (oldProfile?.Id == newProfile?.Id) continue;

                bool wasSupported = IsAnyIconEnabled(oldProfile);
                bool isSupported = IsAnyIconEnabled(newProfile);

                if (wasSupported && !isSupported)
                {
                    libsForHardRefresh.Add(libId);
                }
                else if (wasSupported != isSupported || (wasSupported && isSupported))
                {
                    libsForSoftRefresh.Add(libId);
                }
            }

            if (libsForHardRefresh.Any())
            {
                var items = GetItemsForLibraries(libsForHardRefresh);
                HardRefreshItems(items);
            }
            if (libsForSoftRefresh.Any())
            {
                var items = GetItemsForLibraries(libsForSoftRefresh);
                SoftRefreshItems(items);
            }
        }

        private bool IsAnyIconEnabled(IconProfile? profile)
        {
            if (profile == null) return false;
            var s = profile.Settings;

            return s.AudioIconAlignment != IconAlignment.Disabled || s.SubtitleIconAlignment != IconAlignment.Disabled ||
                   s.ChannelIconAlignment != IconAlignment.Disabled || s.VideoFormatIconAlignment != IconAlignment.Disabled ||
                   s.ResolutionIconAlignment != IconAlignment.Disabled || s.CommunityScoreIconAlignment != IconAlignment.Disabled ||
                   s.AudioCodecIconAlignment != IconAlignment.Disabled || s.VideoCodecIconAlignment != IconAlignment.Disabled ||
                   s.TagIconAlignment != IconAlignment.Disabled || s.AspectRatioIconAlignment != IconAlignment.Disabled ||
                   s.ParentalRatingIconAlignment != IconAlignment.Disabled;
        }

        private List<BaseItem> GetItemsForLibraries(List<string> libraryIds)
        {
            var ancestorIds = libraryIds
                .Select(guidString => Guid.TryParse(guidString, out var guid) ? guid : Guid.Empty)
                .Where(guid => guid != Guid.Empty)
                .Select(guid => _libraryManager.GetItemById(guid))
                .Where(item => item != null)
                .Select(item => item!.InternalId)
                .ToArray();

            if (!ancestorIds.Any()) return new List<BaseItem>();

            return _libraryManager.GetItemList(new InternalItemsQuery { AncestorIds = ancestorIds, Recursive = true })
                .DistinctBy(i => i.Id)
                .ToList();
        }

        private void SoftRefreshItems(IReadOnlyList<BaseItem> items)
        {
            if (!items.Any()) return;

            _logger.Info($"[EmbyIcons] Triggering soft refresh (image-only) for {items.Count} items due to a configuration change.");

            Task.Run(() => {
                var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = false
                };

                foreach (var item in items)
                {
                    item.RefreshMetadata(refreshOptions, CancellationToken.None);
                }
                _logger.Info($"[EmbyIcons] Queued soft refresh for {items.Count} items.");
            });
        }

        private void HardRefreshItems(IReadOnlyList<BaseItem> items)
        {
            if (!items.Any()) return;

            _logger.Info($"[EmbyIcons] Triggering hard refresh (metadata scan) for {items.Count} items to clean up removed overlays.");

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
                _logger.Info($"[EmbyIcons] Queued hard refresh for {items.Count} items.");
            });
        }
    }
}