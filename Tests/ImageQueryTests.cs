using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using EmbyIcons.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EmbyIcons.Tests
{
    public class ImageQueryTests
    {
        internal static ProfileSettings RatingsOnly()
        {
            var settings = new ProfileSettings();
            foreach (var property in typeof(ProfileSettings).GetProperties().Where(p => p.PropertyType == typeof(IconAlignment)))
                property.SetValue(settings, IconAlignment.Disabled);
            settings.CommunityScoreIconAlignment = IconAlignment.TopLeft;
            settings.ParentalRatingIconAlignment = IconAlignment.BottomLeft;
            settings.EnableForThumbs = settings.EnableForBanners = true;
            settings.ShowOverlaysForSeasons = true;
            return settings;
        }

        public static IEnumerable<object[]> Media()
        {
            foreach (var type in new[] { typeof(Movie), typeof(Series), typeof(Season), typeof(Episode), typeof(BoxSet), typeof(MusicAlbum), typeof(MusicArtist), typeof(Audio), typeof(Photo) })
                foreach (var image in new[] { ImageType.Primary, ImageType.Thumb, ImageType.Banner })
                    yield return new object[] { type, image };
        }

        [Theory]
        [MemberData(nameof(Media))]
        public async Task HostCallbacksDoNotQueryMediaOrFiles(Type mediaType, ImageType image)
        {
            using var h = await Harness.Create();
            var item = (BaseItem)Activator.CreateInstance(mediaType)!;
            item.InternalId = 321;
            item.Path = "/media/example";
            // Include the partial-item case: Guid remains empty.
            var enhancer = h.Plugin.Enhancer;
            h.Library.Invocations.Clear();
            h.Files.Invocations.Clear();
            h.Streams.Invocations.Clear();
            Assert.True(enhancer.Supports(item, image));
            var key = enhancer.GetConfigurationCacheKey(item, image);
            Assert.StartsWith("ei11_", key);
            Assert.DoesNotContain("ei_err", key);
            Assert.Equal(key, enhancer.GetConfigurationCacheKey(item, image));
            Assert.Empty(h.Library.Invocations);
            Assert.Empty(h.Files.Invocations);
            Assert.Empty(h.Streams.Invocations);
        }

        [Theory]
        [InlineData(typeof(Series))]
        [InlineData(typeof(Season))]
        [InlineData(typeof(BoxSet))]
        [InlineData(typeof(MusicAlbum))]
        [InlineData(typeof(MusicArtist))]
        public async Task ParentOnlyRenderingPreservesMetadataWithoutQueryingChildren(Type type)
        {
            using var h = await Harness.Create();
            var item = (BaseItem)Activator.CreateInstance(type)!;
            item.Id = Guid.NewGuid(); item.InternalId = 400;
            item.CommunityRating = 8.4f; item.CriticRating = 94; item.OfficialRating = "PG-13";
            var enhancer = h.Plugin.Enhancer;
            h.Library.Invocations.Clear();
            var data = enhancer._overlayDataService.GetBaseOverlayData(item, h.Settings, h.Options);
            Assert.Equal(8.4f, data.CommunityRating);
            Assert.Equal(94, data.RottenTomatoesRating);
            Assert.NotNull(data.ParentalRatingIconName);
            Assert.Empty(h.Library.Invocations);
        }

        [Fact]
        public async Task ColdProfileMatchingDoesNotBuildTrieOrQueryCollectionMembers()
        {
            using var h = await Harness.Create();
            var library = new Mock<ILibraryManager>(MockBehavior.Strict);
            using var profiles = new ProfileManagerService(library.Object, Mock.Of<ILogger>(), h.Options);
            Assert.False(profiles.TryGetCachedProfile(new Series { Path = "/media/show" }, out _));
            Assert.False(profiles.TryGetCachedProfile(new BoxSet { InternalId = 200 }, out _));
            Assert.Empty(library.Invocations);
        }

        [Fact]
        public async Task CollectionKeySurvivesProfileAndAggregationCacheEviction()
        {
            using var h = await Harness.Create();
            var item = new BoxSet { Id = Guid.NewGuid(), InternalId = 200 };
            var unknown = h.Plugin.ImageKeys.GetKey(item, ImageType.Primary, null, false);
            var resolved = h.Plugin.ImageKeys.GetKey(item, ImageType.Primary, h.Options.Profiles[0], true);
            Assert.Equal(unknown, resolved);
            h.Plugin.Enhancer.ClearAllItemDataCaches();
            Assert.Equal(resolved, h.Plugin.ImageKeys.GetKey(item, ImageType.Primary, h.Options.Profiles[0], true));
        }

        [Fact]
        public async Task MetadataAndSettingsChangeImageKeys()
        {
            using var h = await Harness.Create();
            var item = new Series { InternalId = 200, Path = "/media/show" };
            var before = h.Plugin.Enhancer.GetConfigurationCacheKey(item, ImageType.Primary);
            item.DateLastSaved = DateTimeOffset.UtcNow;
            var afterMetadata = h.Plugin.Enhancer.GetConfigurationCacheKey(item, ImageType.Primary);
            Assert.NotEqual(before, afterMetadata);
            h.Settings.CommunityScoreIconAlignment = IconAlignment.BottomRight;
            h.Plugin.ImageKeys.Configure(h.Options);
            Assert.NotEqual(afterMetadata, h.Plugin.Enhancer.GetConfigurationCacheKey(item, ImageType.Primary));
        }

        [Fact]
        public void ChildRevisionsInvalidateOnlyOverlaysWhichUseChildren()
        {
            using var state = NewState();
            var parent = new Series { InternalId = 200 };
            var settings = RatingsOnly();
            var ratings = state.GetDataVersion(parent, settings);
            settings.AudioIconAlignment = IconAlignment.TopLeft;
            var technical = state.GetDataVersion(parent, settings);
            state.Changed(new Episode { InternalId = 300, ParentId = 201, SeriesId = 200 });
            Assert.NotEqual(technical, state.GetDataVersion(parent, settings));
            settings.AudioIconAlignment = IconAlignment.Disabled;
            Assert.Equal(ratings, state.GetDataVersion(parent, settings));
        }

        [Fact]
        public void CollectionMusicAndMovieVersionDomainsCoverIndirectDependencies()
        {
            using var state = NewState();
            var settings = new ProfileSettings();
            var collection = new BoxSet { InternalId = 2 };
            var artist = new MusicArtist { InternalId = 3 };
            var beforeCollection = state.GetDataVersion(collection, settings);
            var beforeArtist = state.GetDataVersion(artist, settings);
            state.Changed(new Movie { InternalId = 5 }, removed: true);
            state.Changed(new Audio { InternalId = 6 }, removed: true);
            Assert.NotEqual(beforeCollection, state.GetDataVersion(collection, settings));
            Assert.NotEqual(beforeArtist, state.GetDataVersion(artist, settings));
            settings.FilenameBasedIcons.Add(new FilenameIconMapping { Keyword = "remux", IconName = "remux" });
            var movie = new Movie { InternalId = 7 };
            var beforeMovie = state.GetDataVersion(movie, settings);
            state.Changed(new Movie { InternalId = 8 });
            Assert.NotEqual(beforeMovie, state.GetDataVersion(movie, settings));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task VerifiedRestartsReuseUnchangedRevisionsAndRejectChangedLibraries(bool changed)
        {
            var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var parent = new Series { InternalId = 10, DateLastSaved = DateTimeOffset.UtcNow };
            var episode = new Episode { InternalId = 11, ParentId = 12, SeriesId = 10 };
            var library = Inventory(parent, episode);
            var settings = new ProfileSettings();
            string version;
            using (var state = new ImageCacheState(folder, Mock.Of<ILogger>()))
            {
                state.Start(library.Object);
                await state.Initialization!;
                state.Changed(episode);
                version = state.GetDataVersion(parent, settings);
            }
            if (changed) episode.DateLastSaved = DateTimeOffset.UtcNow;
            using (var state = new ImageCacheState(folder, Mock.Of<ILogger>()))
            {
                state.Start(library.Object);
                await state.Initialization!;
                if (changed) Assert.NotEqual(version, state.GetDataVersion(parent, settings));
                else Assert.Equal(version, state.GetDataVersion(parent, settings));
            }
            Directory.Delete(folder, true);
        }

        [Fact]
        public void RelationshipDomainsRemainSafeWhenLinkTablesCannotBeVerified()
        {
            using var first = NewState();
            using var second = NewState();
            var settings = new ProfileSettings();
            Assert.NotEqual(first.GetDataVersion(new BoxSet(), settings), second.GetDataVersion(new BoxSet(), settings));
            Assert.NotEqual(first.GetDataVersion(new MusicArtist(), settings), second.GetDataVersion(new MusicArtist(), settings));
        }

        [Fact]
        public void DisabledOrInapplicableFilenameRulesDoNotRequireChildren()
        {
            var settings = RatingsOnly();
            var mapping = new FilenameIconMapping { Keyword = "remux", IconName = "remux", IconAlignment = IconAlignment.Disabled };
            settings.FilenameBasedIcons.Add(mapping);
            Assert.False(OverlayRequirements.VideoChildren(new Series(), settings));
            mapping.IconAlignment = IconAlignment.TopLeft;
            Assert.True(OverlayRequirements.VideoChildren(new Series(), settings));
            mapping.ApplyToSeries = mapping.ApplyToEpisodes = false;
            Assert.False(OverlayRequirements.VideoChildren(new Series(), settings));
        }

        [Fact]
        public void ExternalRatingImageVersionChangesAtCacheRefreshBoundaries()
        {
            var now = new DateTime(2026, 10, 19, 1, 0, 0, DateTimeKind.Utc);
            var start = MDBListService.RatingInterval("12345", now);
            Assert.Equal(start + 1, MDBListService.RatingInterval("12345", now.AddDays(7)));
            Assert.Equal(start, MDBListService.RatingInterval("12345", now));
        }

        internal static ImageCacheState NewState() => new ImageCacheState(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Mock.Of<ILogger>());

        internal static Mock<ILibraryManager> Inventory(params BaseItem[] items)
        {
            var library = new Mock<ILibraryManager>(MockBehavior.Strict);
            library.Setup(x => x.GetInternalItemIds(It.IsAny<InternalItemsQuery>())).Returns(items.Select(i => i.InternalId).ToArray());
            library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<CancellationToken>())).Returns(items);
            return library;
        }

        internal sealed class Harness : IDisposable
        {
            internal Plugin Plugin = null!;
            internal PluginOptions Options = null!;
            internal ProfileSettings Settings => Options.Profiles[0].Settings;
            internal Mock<ILibraryManager> Library = null!;
            internal Mock<ILogger> Log = new Mock<ILogger>();
            internal Mock<IUserDataManager> UserData = new Mock<IUserDataManager>();
            internal Mock<IMediaSourceManager> Streams = new Mock<IMediaSourceManager>(MockBehavior.Strict);
            private IMediaSourceManager? _previousStreams;
            private ILibraryManager? _previousLibrary;
            internal Mock<IFileSystem> Files = new Mock<IFileSystem>(MockBehavior.Strict);
            private string _folder = null!;

            internal static async Task<Harness> Create()
            {
                var h = new Harness { _folder = Path.Combine(Path.GetTempPath(), "embyicons-tests-" + Guid.NewGuid().ToString("N")) };
                Directory.CreateDirectory(h._folder);
                File.WriteAllText(Path.Combine(h._folder, "EmbyIcons.xml"), "<test />");
                File.WriteAllText(Path.Combine(h._folder, "embyicons.xml"), "<test />");
                h.Options = new PluginOptions();
                var profile = new IconProfile { Settings = RatingsOnly() };
                h.Options.Profiles.Add(profile);
                h.Options.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = "library", ProfileId = profile.Id });
                h.Library = Inventory();
                h._previousStreams = BaseItem.MediaSourceManager;
                h._previousLibrary = BaseItem.LibraryManager;
                BaseItem.LibraryManager = h.Library.Object;
                h.Streams.Setup(x => x.GetMediaStreams(It.IsAny<BaseItem>(), It.IsAny<CancellationToken>()))
                    .Returns((BaseItem item, CancellationToken _) => item.MediaStreams ?? new List<MediaStream>());
                BaseItem.MediaSourceManager = h.Streams.Object;
                h.Library.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { new VirtualFolderInfo { Id = "library", Locations = new[] { "/media" } } });
                var paths = new Mock<IApplicationPaths>();
                paths.SetupGet(x => x.DataPath).Returns(h._folder);
                paths.SetupGet(x => x.PluginsPath).Returns(h._folder);
                paths.SetupGet(x => x.PluginConfigurationsPath).Returns(h._folder);
                var log = new Mock<ILogManager>(); log.Setup(x => x.GetLogger(It.IsAny<string>())).Returns(h.Log.Object);
                var xml = new Mock<IXmlSerializer>(); xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(h.Options);
                var appHost = new Mock<IApplicationHost>();
                appHost.Setup(x => x.Resolve<IUserDataManager>()).Returns(h.UserData.Object);
                h.Plugin = new Plugin(appHost.Object, paths.Object, h.Library.Object, Mock.Of<IUserManager>(), log.Object, h.Files.Object, xml.Object);
                h.Plugin.SetAttributes(Path.Combine(h._folder, "EmbyIcons.dll"), h._folder, new Version(5, 61, 1, 5));
                h.Plugin.SetStartupInfo(_ => { });
                new ImageCacheEntryPoint().Run();
                h.Plugin.UpdateConfiguration(h.Options);
                if (h.Plugin.ImageState.Initialization != null) await h.Plugin.ImageState.Initialization;
                h.Plugin.GetProfileForItem(new Series { Path = "/media/show" });
                return h;
            }

            public void Dispose()
            {
                Plugin.Dispose();
                BaseItem.MediaSourceManager = _previousStreams!;
                BaseItem.LibraryManager = _previousLibrary!;
                Directory.Delete(_folder, true);
            }
        }
    }
}
