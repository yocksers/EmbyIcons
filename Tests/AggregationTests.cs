using EmbyIcons;
using EmbyIcons.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EmbyIcons.Tests
{
    public class AggregationTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task VideoLanguagesPreserveLiteAndIntersectionRules(bool season, bool lite)
        {
            using var h = await ImageQueryTests.Harness.Create();
            var parent = Prepare(season ? (BaseItem)new Season() : new Series(), 100);
            var first = Prepare(new Episode { ParentId = 100, SeriesId = 100 }, 101);
            var second = Prepare(new Episode { ParentId = 100, SeriesId = 100 }, 102);
            first.MediaStreams = Streams("English", subtitle: true);
            second.MediaStreams = Streams("English", subtitle: false);
            h.Settings.AudioIconAlignment = h.Settings.SubtitleIconAlignment = IconAlignment.TopLeft;
            h.Settings.UseSeriesLiteMode = lite;
            h.Plugin.ImageKeys.Configure(h.Options);
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) => new BaseItem[] { first, second }.Take(q.Limit ?? 2).ToArray());
            var enhancer = h.Plugin.Enhancer;
            h.Library.Invocations.Clear();
            var key = enhancer.GetConfigurationCacheKey(parent, ImageType.Primary);
            var data = enhancer._overlayDataService.GetBaseOverlayData(parent, h.Settings, h.Options);
            Assert.Contains("english", data.AudioLanguages);
            Assert.Equal(lite ? 1 : 0, data.SubtitleLanguages.Count);
            enhancer._overlayDataService.GetBaseOverlayData(parent, h.Settings, h.Options);
            Assert.Single(h.Library.Invocations);
            h.Observe(parent, data, first.InternalId, second.InternalId);

            first.MediaStreams = Streams("French", subtitle: false);
            h.Plugin.ImageState.Changed(first);
            await h.Plugin.ImageState.VerifyPendingAsync();
            Assert.NotEqual(key, enhancer.GetConfigurationCacheKey(parent, ImageType.Primary));
            var updated = enhancer._overlayDataService.GetBaseOverlayData(parent, h.Settings, h.Options);
            Assert.DoesNotContain("english", updated.AudioLanguages);
            if (lite) Assert.Contains("french", updated.AudioLanguages);
            h.Library.Invocations.Clear();
            enhancer.GetConfigurationCacheKey(parent, ImageType.Primary);
            Assert.Empty(h.Library.Invocations);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task MusicLanguagesPreserveLiteAndUnionRules(bool artist, bool lite)
        {
            using var h = await ImageQueryTests.Harness.Create();
            var parent = Prepare(artist ? (BaseItem)new MusicArtist() : new MusicAlbum(), 200);
            var first = Prepare(new Audio { ParentId = 200 }, 201);
            var second = Prepare(new Audio { ParentId = 200 }, 202);
            first.MediaStreams = Streams("English", false);
            second.MediaStreams = Streams("French", false);
            h.Settings.AudioIconAlignment = IconAlignment.TopLeft;
            h.Settings.UseMusicAlbumLiteMode = lite;
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery q) => new BaseItem[] { first, second }.Take(q.Limit ?? 2).ToArray());
            var enhancer = h.Plugin.Enhancer;
            h.Library.Invocations.Clear();
            var data = enhancer._overlayDataService.GetBaseOverlayData(parent, h.Settings, h.Options);
            Assert.Contains("english", data.AudioLanguages);
            Assert.Equal(lite ? 1 : 2, data.AudioLanguages.Count);
            enhancer._overlayDataService.GetBaseOverlayData(parent, h.Settings, h.Options);
            Assert.Single(h.Library.Invocations);
        }

        [Fact]
        public async Task InFlightInvalidationCannotRepublishAnOldAggregation()
        {
            using var h = await ImageQueryTests.Harness.Create();
            var parent = Prepare(new Series(), 300);
            var episode = Prepare(new Episode { ParentId = 300, SeriesId = 300 }, 301);
            episode.MediaStreams = Streams("English", false);
            h.Settings.AudioIconAlignment = IconAlignment.TopLeft;
            int queries = 0;
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() =>
            {
                if (Interlocked.Increment(ref queries) == 1) h.Plugin.ImageState.Changed(episode);
                return new BaseItem[] { episode };
            });
            var enhancer = h.Plugin.Enhancer;
            enhancer.GetOrBuildAggregatedDataForParent(parent, h.Settings, h.Options);
            Assert.False(EmbyIconsEnhancer._seriesAggregationCache.ContainsKey(parent.Id));
            enhancer.GetOrBuildAggregatedDataForParent(parent, h.Settings, h.Options);
            Assert.Equal(2, queries);
            Assert.True(EmbyIconsEnhancer._seriesAggregationCache.ContainsKey(parent.Id));
        }

        [Fact]
        public async Task ConcurrentRendersShareOneParentQuery()
        {
            using var h = await ImageQueryTests.Harness.Create();
            var parent = Prepare(new Series(), 400);
            var episode = Prepare(new Episode { ParentId = 400, SeriesId = 400 }, 401);
            episode.MediaStreams = Streams("English", false);
            h.Settings.AudioIconAlignment = IconAlignment.TopLeft;
            int queries = 0;
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() =>
            {
                Interlocked.Increment(ref queries);
                Thread.SpinWait(100000);
                return new BaseItem[] { episode };
            });
            var enhancer = h.Plugin.Enhancer;
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
                enhancer.GetOrBuildAggregatedDataForParent(parent, h.Settings, h.Options))));
            Assert.Equal(1, queries);
        }

        internal static T Prepare<T>(T item, long id) where T : BaseItem
        {
            // Model a fully loaded item without depending on Emby's global tagged-item repository.
            item.TagItems = Array.Empty<LinkedItemInfo>();
            _ = item.Tags;
            item.InternalId = id;
            item.Id = Guid.NewGuid();
            item.Path = "/media/item-" + id;
            return item;
        }

        internal static List<MediaStream> Streams(string language, bool subtitle)
        {
            var streams = new List<MediaStream> { new MediaStream
            {
                Type = MediaStreamType.Audio, DisplayLanguage = language, Language = language,
                Codec = "aac", Channels = 2, SampleRate = 48000, BitRate = 192000
            } };
            if (subtitle) streams.Add(new MediaStream { Type = MediaStreamType.Subtitle, DisplayLanguage = "Chinese" });
            return streams;
        }
    }
}
