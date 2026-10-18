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
using System.Linq;
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
            var data = enhancer._overlayDataService.GetBaseOverlayData(series, h.Settings, h.Options);
            h.Observe(series, data, episode.InternalId);
            episode.MediaStreams = AggregationTests.Streams("French", false);
            var before = enhancer.GetConfigurationCacheKey(series, ImageType.Primary);
            h.Library.Raise(x => x.ItemUpdated += null, h.Library.Object, new ItemChangeEventArgs(h.Library.Object) { Item = episode });
            Assert.False(EmbyIconsEnhancer._seriesAggregationCache.ContainsKey(series.Id), string.Join("\n", h.Log.Invocations));
            await h.Plugin.ImageState.VerifyPendingAsync();
            Assert.NotEqual(before, enhancer.GetConfigurationCacheKey(series, ImageType.Primary));
        }

        [Theory]
        [InlineData(UserDataSaveReason.UpdateUserRating, true)]
        [InlineData(UserDataSaveReason.PlaybackProgress, false)]
        public async Task FavoritesChangeImageVersionWithoutTurningPlaybackIntoImageUpdates(UserDataSaveReason reason, bool changes)
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.FavoriteCountIconAlignment = IconAlignment.TopLeft;
            h.Plugin.ImageKeys.Configure(h.Options);
            var item = AggregationTests.Prepare(new Series(), 400);
            item.ImageInfos = new[] { new ItemImageInfo { Type = ImageType.Primary, Path = "poster.jpg" } };
            h.Library.Setup(x => x.UpdateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()));
            var before = item.ImageInfos[0].DateModified;
            h.UserData.Raise(x => x.UserDataSaved += null, new UserDataSaveEventArgs { Item = item, SaveReason = reason });
            var after = item.ImageInfos[0].DateModified;
            Assert.Equal(changes, before != after);
            h.Library.Verify(x => x.UpdateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()), changes ? Times.Once() : Times.Never());
        }
        [Theory]
        [InlineData("Tmdb")]
        [InlineData("Imdb")]
        [InlineData("douban")]
        [InlineData("JavDB")]
        [InlineData("JavBus")]
        [InlineData("emby")]
        public async Task ANewMovieVersionChangesOnlyTheMatchingRenderedMovie(string provider)
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.FilenameBasedIcons.Add(new FilenameIconMapping { Keyword = "remux", IconName = "remux", ApplyToMovies = true });
            h.Plugin.ImageKeys.Configure(h.Options);
            var first = AggregationTests.Prepare(new Movie(), 500);
            var other = AggregationTests.Prepare(new Movie(), 501);
            if (provider == "emby") { first.PresentationUniqueKey = "explicit-group-123"; other.PresentationUniqueKey = "explicit-group-456"; }
            else { first.ProviderIds = new ProviderIdDictionary { [provider] = "123" }; other.ProviderIds = new ProviderIdDictionary { [provider] = "456" }; }
            var rows = new List<BaseItem> { first, other };
            BaseItem[] Matching(InternalItemsQuery q) => rows.Where(i => !string.IsNullOrEmpty(q.PresentationUniqueKey) ? i.PresentationUniqueKey == q.PresentationUniqueKey :
                q.AnyProviderIdEquals != null && q.AnyProviderIdEquals.Any(p => i.ProviderIds.Any(v => v.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase) && v.Value == p.Value))).ToArray();
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Matching(q));
            h.Library.Setup(x => x.GetInternalItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => Matching(q).Select(i => i.InternalId).ToArray());
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns((InternalItemsQuery q, System.Threading.CancellationToken _) => rows.Where(i => q.ItemIds.Contains(i.InternalId)).ToArray());
            var enhancer = h.Plugin.Enhancer;
            var beforeFirst = enhancer.GetConfigurationCacheKey(first, ImageType.Primary);
            var beforeOther = enhancer.GetConfigurationCacheKey(other, ImageType.Primary);
            h.Observe(first, enhancer._overlayDataService.GetBaseOverlayData(first, h.Settings, h.Options), first.InternalId);
            h.Observe(other, enhancer._overlayDataService.GetBaseOverlayData(other, h.Settings, h.Options), other.InternalId);
            var added = AggregationTests.Prepare(new Movie(), 502);
            added.ProviderIds = first.ProviderIds; added.PresentationUniqueKey = first.PresentationUniqueKey; added.Path = "/media/remux-version.mkv";
            rows.Add(added); h.Plugin.ImageState.Changed(added, added: true);
            await h.Plugin.ImageState.VerifyPendingAsync();
            h.Library.Invocations.Clear();
            Assert.NotEqual(beforeFirst, enhancer.GetConfigurationCacheKey(first, ImageType.Primary));
            Assert.Equal(beforeOther, enhancer.GetConfigurationCacheKey(other, ImageType.Primary));
            Assert.Empty(h.Library.Invocations);
        }

        [Fact]
        public async Task MovingACollectionMemberRebindsItsProfileWithoutChangingUnrelatedImages()
        {
            using var h = await ImageQueryTests.Harness.Create();
            var second = new IconProfile { Settings = ImageQueryTests.RatingsOnly() };
            second.Settings.CommunityScoreIconAlignment = IconAlignment.BottomRight;
            h.Options.Profiles.Add(second);
            h.Options.LibraryProfileMappings.Add(new LibraryMapping { LibraryId = "other", ProfileId = second.Id });
            h.Library.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
            { new VirtualFolderInfo { Id = "library", Locations = new[] { "/media" } }, new VirtualFolderInfo { Id = "other", Locations = new[] { "/other" } } });
            h.Plugin.SaveCurrentConfiguration();
            await h.Plugin.GetProfileForItemAsync(new Series { Path = "/other/warm" });
            var collection = AggregationTests.Prepare(new BoxSet(), 600);
            var member = AggregationTests.Prepare(new Movie(), 601);
            var unrelated = AggregationTests.Prepare(new Series(), 602);
            member.Path = "/media/member.mkv";
            h.Library.SetupGet(x => x.RootFolder).Returns((AggregateFolder)null!);
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { member });
            h.Library.Setup(x => x.GetInternalItemIds(It.IsAny<InternalItemsQuery>())).Returns(new long[] { 601 });
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns((InternalItemsQuery q, System.Threading.CancellationToken _) => new BaseItem[] { collection, member, unrelated }.Where(i => q.ItemIds.Contains(i.InternalId)).ToArray());
            Assert.Equal(h.Options.Profiles[0].Id, h.Plugin.GetProfileForItem(collection)?.Id);
            Assert.Equal(h.Options.Profiles[0].Id, h.Plugin.GetProfileForItem(member)?.Id);
            var enhancer = h.Plugin.Enhancer;
            var before = enhancer.GetConfigurationCacheKey(collection, ImageType.Primary);
            var beforeUnrelated = enhancer.GetConfigurationCacheKey(unrelated, ImageType.Primary);
            h.Observe(collection, enhancer._overlayDataService.GetBaseOverlayData(collection, h.Settings, h.Options), member.InternalId);
            member.Path = "/other/member.mkv";
            h.Library.Raise(x => x.ItemUpdated += null, h.Library.Object, new ItemChangeEventArgs(h.Library.Object) { Item = member });
            await h.Plugin.ImageState.VerifyPendingAsync();
            Assert.Equal(second.Id, h.Plugin.GetProfileForItem(member)?.Id);
            Assert.Equal(second.Id, h.Plugin.ImageState.KnownProfile(collection, (int)ImageType.Primary));
            Assert.NotEqual(before, enhancer.GetConfigurationCacheKey(collection, ImageType.Primary));
            Assert.Equal(beforeUnrelated, enhancer.GetConfigurationCacheKey(unrelated, ImageType.Primary));
        }

        [Fact]
        public async Task CollectionMembershipEventsCheckOnlyTheChangedCollection()
        {
            using var h = await ImageQueryTests.Harness.Create(collectionEvents: true);
            Assert.True(h.Plugin.ImageState.CollectionEventsAvailable);
            var first = AggregationTests.Prepare(new BoxSet(), 700);
            var other = AggregationTests.Prepare(new BoxSet(), 701);
            var member = AggregationTests.Prepare(new Movie(), 702);
            var added = AggregationTests.Prepare(new Movie(), 703);
            var rows = new BaseItem[] { first, other, member, added };
            h.Library.SetupGet(x => x.RootFolder).Returns((AggregateFolder)null!);
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { member });
            h.Library.Setup(x => x.GetInternalItemIds(It.IsAny<InternalItemsQuery>())).Returns(new long[] { 702, 703 });
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns((InternalItemsQuery q, System.Threading.CancellationToken _) => rows.Where(i => q.ItemIds.Contains(i.InternalId)).ToArray());
            var enhancer = h.Plugin.Enhancer;
            var before = enhancer.GetConfigurationCacheKey(other, ImageType.Primary);
            enhancer.GetConfigurationCacheKey(first, ImageType.Primary);
            h.Observe(first, enhancer._overlayDataService.GetBaseOverlayData(first, h.Settings, h.Options), member.InternalId);
            h.Observe(other, enhancer._overlayDataService.GetBaseOverlayData(other, h.Settings, h.Options), member.InternalId);
            var checkedItems = new List<long>();
            var inspect = h.Plugin.ImageState.Inspect!;
            h.Plugin.ImageState.Inspect = async (item, settings, baseline, token) => { checkedItems.Add(item.InternalId); return await inspect(item, settings, baseline, token); };
            h.Library.Raise(x => x.ItemUpdated += null, h.Library.Object, new ItemChangeEventArgs(h.Library.Object) { Item = new Movie { InternalId = 9999 } });
            await h.Plugin.ImageState.VerifyPendingAsync();
            Assert.Empty(checkedItems);
            h.Collections.Raise(x => x.ItemsAddedToCollection += null, h.Collections.Object,
                new MediaBrowser.Controller.Collections.CollectionModifiedEventArgs { Collection = first, ItemsChanged = new List<long> { added.InternalId } });
            await h.Plugin.ImageState.VerifyPendingAsync();
            Assert.Equal(new long[] { 700 }, checkedItems);
            Assert.Equal(before, enhancer.GetConfigurationCacheKey(other, ImageType.Primary));
        }

    }
}
