using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using Moq;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace EmbyIcons.Tests
{
    public class RenderingAndRecoveryTests
    {
        private readonly ITestOutputHelper _output;
        public RenderingAndRecoveryTests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public async Task ActualPosterRenderingDrawsRatingWithoutQueryingEpisodes()
        {
            using var h = await ImageQueryTests.Harness.Create();
            var item = AggregationTests.Prepare(new Series(), 600);
            item.CommunityRating = 8.4f;
            item.OfficialRating = "PG-13";
            h.Options.OutputFormat = OutputFormat.Png;
            var enhancer = h.Plugin.Enhancer;
            using var original = new SKBitmap(300, 450);
            original.Erase(SKColors.Blue);
            using var image = SKImage.FromBitmap(original);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = encoded.ToArray();
            h.Files.Setup(x => x.GetFileStream(It.IsAny<string>(), FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true))
                .Returns(() => new MemoryStream(bytes));
            var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            h.Library.Invocations.Clear();
            h.Streams.Invocations.Clear();
            try
            {
                await enhancer.EnhanceImageAsync(item, "original.png", target, ImageType.Primary, 0, CancellationToken.None);
                using var result = SKBitmap.Decode(target);
                Assert.NotNull(result);
                Assert.Equal(300, result.Width);
                Assert.Equal(450, result.Height);
                Assert.True(result.Pixels.Count(p => p != SKColors.Blue) > 30);
                Assert.Empty(h.Library.Invocations);
                Assert.Empty(h.Streams.Invocations);
            }
            finally { if (File.Exists(target)) File.Delete(target); }
        }

        [Fact]
        public async Task UnresolvedCollectionCannotEnableAnImageTypeDisabledInItsActualProfile()
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.EnableForThumbs = false;
            var other = new IconProfile { Settings = ImageQueryTests.RatingsOnly() };
            other.Settings.EnableForThumbs = true;
            h.Options.Profiles.Add(other);
            h.Plugin.ImageKeys.Configure(h.Options);
            var collection = AggregationTests.Prepare(new BoxSet(), 650);
            h.Library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new BaseItem[] { new Movie { Path = "/media/movie" } });
            var enhancer = h.Plugin.Enhancer;
            Assert.True(enhancer.Supports(collection, ImageType.Thumb));
            collection.CommunityRating = 8.4f;
            using var bitmap = new SKBitmap(300, 450);
            bitmap.Erase(SKColors.Blue);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var original = encoded.ToArray();
            h.Files.Setup(x => x.GetFileStream(It.IsAny<string>(), FileOpenMode.Open, FileAccessMode.Read, FileShareMode.Read, true))
                .Returns(() => new MemoryStream(original));
            var target = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            try
            {
                await enhancer.EnhanceImageAsync(collection, "original.png", target, ImageType.Thumb, 0, CancellationToken.None);
                Assert.Equal(original, File.ReadAllBytes(target));
            }
            finally { if (File.Exists(target)) File.Delete(target); }
        }

        [Fact]
        public async Task LargeTechnicalMetadataListDoesNotStartQueriesOrStreamReads()
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.AudioIconAlignment = h.Settings.SubtitleIconAlignment = IconAlignment.TopLeft;
            h.Settings.VideoCodecIconAlignment = h.Settings.ResolutionIconAlignment = IconAlignment.TopRight;
            h.Options.MDBListApiKey = "offline-test-key";
            h.Settings.PopcornScoreIconAlignment = IconAlignment.TopLeft;
            h.Plugin.ImageKeys.Configure(h.Options);
            var enhancer = h.Plugin.Enhancer;
            h.Library.Invocations.Clear(); h.Streams.Invocations.Clear(); h.Files.Invocations.Clear();
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 1548; i++)
            {
                var item = new Series { InternalId = 1000 + i, Path = "/media/series-" + i };
                item.ProviderIds = new ProviderIdDictionary { ["Tmdb"] = (10000 + i).ToString() };
                Assert.True(enhancer.Supports(item, ImageType.Primary));
                var key = enhancer.GetConfigurationCacheKey(item, ImageType.Primary);
                Assert.StartsWith("ei11_", key);
                Assert.Equal(key, enhancer.GetConfigurationCacheKey(item, ImageType.Primary));
            }
            _output.WriteLine($"Offline callbacks for 1548 series: {timer.ElapsedMilliseconds} ms; no SQL, media streams or filesystem calls.");
            Assert.Empty(h.Library.Invocations); Assert.Empty(h.Streams.Invocations); Assert.Empty(h.Files.Invocations);
        }

        [Fact]
        public async Task MovingEpisodeInvalidatesItsOldAndNewParents()
        {
            var oldSeries = new Series { InternalId = 700 };
            var newSeries = new Series { InternalId = 800 };
            var episode = new Episode { InternalId = 701, ParentId = 702, SeriesId = 700 };
            using var state = ImageQueryTests.NewState();
            state.Start(ImageQueryTests.Inventory(oldSeries, newSeries, episode).Object);
            await state.Initialization!;
            var settings = new ProfileSettings();
            var beforeOld = state.GetDataVersion(oldSeries, settings);
            var beforeNew = state.GetDataVersion(newSeries, settings);
            episode.ParentId = 802; episode.SeriesId = 800;
            state.Changed(episode);
            Assert.NotEqual(beforeOld, state.GetDataVersion(oldSeries, settings));
            Assert.NotEqual(beforeNew, state.GetDataVersion(newSeries, settings));
        }

        [Fact]
        public async Task CorruptedVersionIndexCannotReuseAnOldDependencyRevision()
        {
            var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "image-input-versions.json"), "{ invalid json");
            using (var state = new ImageCacheState(folder, Mock.Of<ILogger>()))
            {
                var item = new Series { InternalId = 900 };
                var before = state.GetDataVersion(item, new ProfileSettings());
                state.Start(ImageQueryTests.Inventory(item).Object);
                await state.Initialization!;
                Assert.Equal(before, state.GetDataVersion(item, new ProfileSettings()));
            }
            Directory.Delete(folder, true);
        }

        [Fact]
        public async Task BlockedStartupVerificationDoesNotBlockImageTags()
        {
            using var h = await ImageQueryTests.Harness.Create();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var library = ImageQueryTests.Inventory();
            library.Setup(x => x.GetInternalItemIds(It.IsAny<InternalItemsQuery>())).Returns(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return Array.Empty<long>();
            });
            using var state = ImageQueryTests.NewState();
            state.Start(library.Object);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            try
            {
                var keys = new ImageKeyService(h.Options, state);
                var series = new Series { InternalId = 901 };
                for (int i = 0; i < 100; i++) Assert.StartsWith("ei11_", keys.GetKey(series, ImageType.Primary, null, false));
            }
            finally { release.Set(); await state.Initialization!; }
        }
    }
}
