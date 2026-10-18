using EmbyIcons.Configuration;
using EmbyIcons.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace EmbyIcons.Tests
{
    public class DependencyTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CollectionProfileChangesAreVisibleWithoutWaitingForExpiration(bool asynchronous)
        {
            using var h = await ImageQueryTests.Harness.Create();
            var second = new IconProfile();
            h.Options.Profiles.Add(second);
            h.Options.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = "other", ProfileId = second.Id });
            h.Library.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo { Id = "library", Locations = new[] { "/media" } },
                new VirtualFolderInfo { Id = "other", Locations = new[] { "/other" } }
            });
            using var profiles = new ProfileManagerService(h.Library.Object, Mock.Of<ILogger>(), h.Options);
            await profiles.WarmLibraryPathsAsync();
            var member = new Movie { Path = "/media/movie" };
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => new BaseItem[] { member });
            var collection = new BoxSet { InternalId = 100, Id = Guid.NewGuid() };
            var first = asynchronous ? await profiles.GetProfileForItemAsync(collection) : profiles.GetProfileForItem(collection);
            Assert.Equal(h.Options.Profiles[0].Id, first?.Id);
            member.Path = "/other/movie";
            profiles.InvalidateCollectionProfiles();
            Assert.False(profiles.TryGetCachedProfile(collection, out _));
            var next = asynchronous ? await profiles.GetProfileForItemAsync(collection) : profiles.GetProfileForItem(collection);
            Assert.Equal(second.Id, next?.Id);
        }

        [Fact]
        public async Task MovieFilenameRulesObserveNewVersionsDespiteWarmPathAndItemCaches()
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.FilenameBasedIcons.Add(new FilenameIconMapping { Keyword = "remux", IconName = "remux", ApplyToMovies = true });
            var movie = AggregationTests.Prepare(new Movie(), 200);
            movie.ProviderIds = new ProviderIdDictionary { ["Tmdb"] = "12345" };
            BaseItem[] versions = { movie };
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => versions);
            var enhancer = h.Plugin.Enhancer;
            Assert.Empty(enhancer._overlayDataService.GetBaseOverlayData(movie, h.Settings, h.Options).FilenameBasedIcons);
            var remux = AggregationTests.Prepare(new Movie(), 201);
            remux.Path = "/media/movie-remux.mkv";
            versions = new BaseItem[] { movie, remux };
            h.Plugin.ImageState.Changed(remux, added: true);
            Assert.Single(enhancer._overlayDataService.GetBaseOverlayData(movie, h.Settings, h.Options).FilenameBasedIcons);
        }

        [Fact]
        public async Task SubscribedEpisodeEventChangesImageTagAndInvalidatesAggregation()
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.AudioIconAlignment = IconAlignment.TopLeft;
            h.Plugin.ImageKeys.Configure(h.Options);
            h.Library.Setup(x => x.GetItemById(301, It.IsAny<MediaBrowser.Model.Data.IDataContext>())).Returns(AggregationTests.Prepare(new Season(), 301));
            h.Library.SetupGet(x => x.RootFolder).Returns((AggregateFolder)null!);
            var series = AggregationTests.Prepare(new Series(), 300);
            var episode = AggregationTests.Prepare(new Episode { ParentId = 301, SeriesId = 300 }, 302);
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { episode });
            var enhancer = h.Plugin.Enhancer;
            enhancer.GetOrBuildAggregatedDataForParent(series, h.Settings, h.Options);
            var before = enhancer.GetConfigurationCacheKey(series, ImageType.Primary);
            h.Library.Raise(x => x.ItemUpdated += null, h.Library.Object, new ItemChangeEventArgs(h.Library.Object) { Item = episode });
            Assert.NotEqual(before, enhancer.GetConfigurationCacheKey(series, ImageType.Primary));
            Assert.False(EmbyIconsEnhancer._seriesAggregationCache.ContainsKey(series.Id), string.Join("\n", h.Log.Invocations));
        }

        [Theory]
        [InlineData(UserDataSaveReason.UpdateUserRating, true)]
        [InlineData(UserDataSaveReason.PlaybackProgress, false)]
        public async Task FavoritesChangeImageVersionWithoutTurningPlaybackIntoImageUpdates(UserDataSaveReason reason, bool changes)
        {
            using var h = await ImageQueryTests.Harness.Create();
            var item = AggregationTests.Prepare(new Series(), 400);
            item.ImageInfos = new[] { new ItemImageInfo { Type = ImageType.Primary, Path = "poster.jpg" } };
            h.Library.Setup(x => x.UpdateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()));
            var before = h.Plugin.Enhancer.GetConfigurationCacheKey(item, ImageType.Primary);
            h.UserData.Raise(x => x.UserDataSaved += null, new UserDataSaveEventArgs { Item = item, SaveReason = reason });
            var after = h.Plugin.Enhancer.GetConfigurationCacheKey(item, ImageType.Primary);
            Assert.Equal(changes, before != after);
            h.Library.Verify(x => x.UpdateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()), changes ? Times.Once() : Times.Never());
        }
    }
}
