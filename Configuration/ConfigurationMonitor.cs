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

namespace EmbyIcons.Configuration
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

            // Check each library for profile changes or profile mapping changes
            foreach (var libId in allLibraryIds)
            {
                oldMappings.TryGetValue(libId, out var oldMap);
                newMappings.TryGetValue(libId, out var newMap);

                IconProfile? oldProfile = null;
                if (oldMap != null) oldProfileDict.TryGetValue(oldMap.ProfileId, out oldProfile);

                IconProfile? newProfile = null;
                if (newMap != null) newProfileDict.TryGetValue(newMap.ProfileId, out newProfile);

                // Profile mapping changed (different profile assigned)
                if (oldProfile?.Id != newProfile?.Id)
                {
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
                // Same profile but settings might have changed
                else if (oldProfile != null && newProfile != null && ProfileSettingsChanged(oldProfile, newProfile))
                {
                    if (!libsForSoftRefresh.Contains(libId))
                    {
                        libsForSoftRefresh.Add(libId);
                        _logger.Info($"[EmbyIcons] Profile '{newProfile.Name}' settings changed for library {libId}");
                    }
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

        private bool ProfileSettingsChanged(IconProfile oldProfile, IconProfile newProfile)
        {
            var oldS = oldProfile.Settings;
            var newS = newProfile.Settings;

            // Check if any icon alignment settings changed
            if (oldS.AudioIconAlignment != newS.AudioIconAlignment) return true;
            if (oldS.SubtitleIconAlignment != newS.SubtitleIconAlignment) return true;
            if (oldS.ChannelIconAlignment != newS.ChannelIconAlignment) return true;
            if (oldS.VideoFormatIconAlignment != newS.VideoFormatIconAlignment) return true;
            if (oldS.ResolutionIconAlignment != newS.ResolutionIconAlignment) return true;
            if (oldS.AudioCodecIconAlignment != newS.AudioCodecIconAlignment) return true;
            if (oldS.VideoCodecIconAlignment != newS.VideoCodecIconAlignment) return true;
            if (oldS.TagIconAlignment != newS.TagIconAlignment) return true;
            if (oldS.CommunityScoreIconAlignment != newS.CommunityScoreIconAlignment) return true;
            if (oldS.AspectRatioIconAlignment != newS.AspectRatioIconAlignment) return true;
            if (oldS.ParentalRatingIconAlignment != newS.ParentalRatingIconAlignment) return true;
            if (oldS.SourceIconAlignment != newS.SourceIconAlignment) return true;
            if (oldS.RottenTomatoesScoreIconAlignment != newS.RottenTomatoesScoreIconAlignment) return true;

            // Check if any layout/behavior settings changed that affect appearance
            if (oldS.AudioOverlayHorizontal != newS.AudioOverlayHorizontal) return true;
            if (oldS.SubtitleOverlayHorizontal != newS.SubtitleOverlayHorizontal) return true;
            if (oldS.ChannelOverlayHorizontal != newS.ChannelOverlayHorizontal) return true;
            if (oldS.VideoFormatOverlayHorizontal != newS.VideoFormatOverlayHorizontal) return true;
            if (oldS.ResolutionOverlayHorizontal != newS.ResolutionOverlayHorizontal) return true;
            if (oldS.AudioCodecOverlayHorizontal != newS.AudioCodecOverlayHorizontal) return true;
            if (oldS.VideoCodecOverlayHorizontal != newS.VideoCodecOverlayHorizontal) return true;
            if (oldS.TagOverlayHorizontal != newS.TagOverlayHorizontal) return true;
            if (oldS.AspectRatioOverlayHorizontal != newS.AspectRatioOverlayHorizontal) return true;
            if (oldS.ParentalRatingOverlayHorizontal != newS.ParentalRatingOverlayHorizontal) return true;
            if (oldS.SourceOverlayHorizontal != newS.SourceOverlayHorizontal) return true;
            if (oldS.IconSize != newS.IconSize) return true;

            // Check priority changes (affects order)
            if (oldS.AudioIconPriority != newS.AudioIconPriority) return true;
            if (oldS.SubtitleIconPriority != newS.SubtitleIconPriority) return true;
            if (oldS.ChannelIconPriority != newS.ChannelIconPriority) return true;
            if (oldS.VideoFormatIconPriority != newS.VideoFormatIconPriority) return true;
            if (oldS.ResolutionIconPriority != newS.ResolutionIconPriority) return true;
            if (oldS.AudioCodecIconPriority != newS.AudioCodecIconPriority) return true;
            if (oldS.VideoCodecIconPriority != newS.VideoCodecIconPriority) return true;
            if (oldS.TagIconPriority != newS.TagIconPriority) return true;
            if (oldS.CommunityScoreIconPriority != newS.CommunityScoreIconPriority) return true;
            if (oldS.AspectRatioIconPriority != newS.AspectRatioIconPriority) return true;
            if (oldS.ParentalRatingIconPriority != newS.ParentalRatingIconPriority) return true;
            if (oldS.SourceIconPriority != newS.SourceIconPriority) return true;
            if (oldS.RottenTomatoesScoreIconPriority != newS.RottenTomatoesScoreIconPriority) return true;

            // Check behavior settings
            if (oldS.ShowOverlaysForEpisodes != newS.ShowOverlaysForEpisodes) return true;
            if (oldS.ShowOverlaysForSeasons != newS.ShowOverlaysForSeasons) return true;
            if (oldS.ShowSeriesIconsIfAllEpisodesHaveLanguage != newS.ShowSeriesIconsIfAllEpisodesHaveLanguage) return true;
            if (oldS.ExcludeSpecialsFromSeriesAggregation != newS.ExcludeSpecialsFromSeriesAggregation) return true;

            return false;
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