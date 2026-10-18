using EmbyIcons.Caching;
using EmbyIcons.Configuration;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EmbyIcons.Tests
{
    public class StableImageCacheTests
    {
        private sealed class Folder : IDisposable
        {
            internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "image-state-test-" + Guid.NewGuid().ToString("N"));
            public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
        }
        private static ImageCacheState State(Folder folder) => new ImageCacheState(folder.Path, Mock.Of<ILogger>());
        private static PluginOptions Options()
        {
            var result = new PluginOptions { PersistedVersion = "probe-fixed-version" };
            var settings = ImageQueryTests.RatingsOnly();
            settings.ParentalRatingIconAlignment = IconAlignment.Disabled;
            settings.ShowOverlaysForEpisodes = true;
            settings.UseCollectionLiteMode = true;
            result.Profiles.Add(new IconProfile { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Probe", Settings = settings });
            return result;
        }
        private static Mock<ILibraryManager> Inventory(params BaseItem[] rows)
        {
            var result = new Mock<ILibraryManager>(MockBehavior.Strict);
            result.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<CancellationToken>()))
                .Returns((InternalItemsQuery q, CancellationToken _) => rows.Where(r => q.ItemIds.Contains(r.InternalId)).ToArray());
            result.Setup(x => x.GetInternalItemIds(It.IsAny<InternalItemsQuery>()))
                .Throws(new InvalidOperationException("A working-set check must not enumerate the complete library."));
            return result;
        }
        public static IEnumerable<object[]> Kinds() => new[] { typeof(Movie), typeof(Series), typeof(Season), typeof(Episode),
            typeof(BoxSet), typeof(MusicAlbum), typeof(MusicArtist), typeof(Audio), typeof(Photo), typeof(Video) }.Select(t => new object[] { t });
        private static BaseItem Item(Type type, long id = 10)
        {
            var item = (BaseItem)Activator.CreateInstance(type)!;
            item.InternalId = id;
            item.Path = "/media/example";
            return item;
        }

        [Fact]
        public void ExistingRatingsPosterKeyMatchesThePreviousReleasedDll()
        {
            // Captured from the installed 5.61.1.6 DLL, not recomputed by this test.
            const string previous = "ei11_i10_0_p11111111111111111111111111111111_vprobe-fixed-version_s76772452fdee4342_d0:0";
            using var folder = new Folder(); using var state = State(folder);
            var options = Options();
            var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            Assert.Equal(previous, keys.GetKey(Item(typeof(Series)), ImageType.Primary, options.Profiles[0], true));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void ResolvingAndEvictingAProfileDoesNotChangeAnExistingImageKey(Type kind)
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(kind);
            var cold = keys.GetKey(item, ImageType.Primary, null, false);
            Assert.Equal(cold, keys.GetKey(item, ImageType.Primary, options.Profiles[0], true));
            Assert.Equal(cold, keys.GetKey(item, ImageType.Primary, null, false));
            Assert.True(keys.Supports(item, ImageType.Primary, null, false));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public async Task PersistedKeysAreIdenticalBeforeAndAfterStartupVerification(Type kind)
        {
            using var folder = new Folder();
            var item = Item(kind); var options = Options(); string before;
            using (var state = State(folder))
            {
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                before = keys.GetKey(item, ImageType.Primary, options.Profiles[0], true);
            }
            using (var state = State(folder))
            {
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                Assert.Equal(before, keys.GetKey(item, ImageType.Primary, null, false));
                state.Start(Inventory(item).Object); await state.Initialization!;
                Assert.Equal(before, keys.GetKey(item, ImageType.Primary, null, false));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OperationalSettingsAndProfileNamesCannotBustExistingImages(bool resolved)
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(typeof(Series));
            var before = keys.GetKey(item, ImageType.Primary, resolved ? options.Profiles[0] : null, resolved);
            options.MaxSeriesCacheSize++;
            options.EnableDebugLogging = true;
            options.EnableSummaryDiskCache = !options.EnableSummaryDiskCache;
            options.CacheMaintenanceIntervalHours += 1;
            options.Profiles[0].Name = "Renamed";
            keys.Configure(options);
            Assert.Equal(before, keys.GetKey(item, ImageType.Primary, resolved ? options.Profiles[0] : null, resolved));
        }

        [Fact]
        public void ChangingAnUnrelatedProfileCannotBustAnImageAfterItsProfileWasBound()
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options();
            var other = new IconProfile { Settings = ImageQueryTests.RatingsOnly() }; options.Profiles.Add(other);
            var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(typeof(Series));
            var before = keys.GetKey(item, ImageType.Primary, null, false);
            Assert.Equal(before, keys.GetKey(item, ImageType.Primary, options.Profiles[0], true));
            other.Settings.IconSize++;
            options.Profiles.Reverse();
            keys.Configure(options);
            Assert.Equal(before, keys.GetKey(item, ImageType.Primary, null, false));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ManualRefreshStillInvalidatesBothLegacyKeyShapes(bool resolved)
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(typeof(Series));
            var before = keys.GetKey(item, ImageType.Primary, resolved ? options.Profiles[0] : null, resolved);
            options.PersistedVersion = "explicit-manual-refresh"; keys.Configure(options);
            Assert.NotEqual(before, keys.GetKey(item, ImageType.Primary, resolved ? options.Profiles[0] : null, resolved));
        }

        [Fact]
        public void DisabledMetadataFieldsAndTimestampOnlySavesKeepTheKey()
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(typeof(Series)); item.CommunityRating = 8.4f;
            var before = keys.GetKey(item, ImageType.Primary, options.Profiles[0], true);
            item.CriticRating = 99; item.DateModified = DateTimeOffset.UtcNow; item.DateLastSaved = DateTimeOffset.UtcNow;
            state.Changed(item);
            Assert.Equal(before, keys.GetKey(item, ImageType.Primary, options.Profiles[0], true));
            item.CommunityRating = 9.0f; state.Changed(item);
            Assert.NotEqual(before, keys.GetKey(item, ImageType.Primary, options.Profiles[0], true));
        }

        [Fact]
        public async Task LargeLibrariesAreVerifiedThroughBoundedWorkingSetQueries()
        {
            using var folder = new Folder(); using var state = State(folder);
            var settings = ImageQueryTests.RatingsOnly();
            var items = Enumerable.Range(1, 1002).Select(id => (BaseItem)new Series { InternalId = id }).ToArray();
            foreach (var item in items) state.GetDataVersion(item, settings);
            var library = Inventory(items);
            // An underlying library with 500,001+ rows is immaterial: the forbidden
            // all-library ID query above would throw. Only these 1,002 identities are fetched.
            state.Start(library.Object); await state.Initialization!;
            var queries = library.Invocations.Where(i => i.Method.Name == nameof(ILibraryManager.GetItemList)).Select(i => (InternalItemsQuery)i.Arguments[0]).ToArray();
            Assert.Equal(3, queries.Length);
            Assert.All(queries, q => { Assert.InRange(q.ItemIds.Length, 1, 500); Assert.Equal(500, q.Limit); });
            Assert.Equal(1002, queries.Sum(q => q.ItemIds.Length));
            Assert.Equal(1002, state.TrackedCount);
        }

        [Fact]
        public async Task RenderedCollectionReusesItsVersionAcrossRestartsAndUnchangedMembershipChecks()
        {
            using var folder = new Folder(); var item = Item(typeof(BoxSet));
            var member = Item(typeof(Movie), 11); var options = Options(); string before;
            using (var state = State(folder))
            {
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                before = keys.GetKey(item, ImageType.Primary, options.Profiles[0], true);
                state.Observed(item, options.Profiles[0].Settings, new ImageCacheState.Inspection { Fingerprint = "same", Dependencies = new long[] { 11 } }, 0, true);
            }
            using (var state = State(folder))
            {
                int inspections = 0;
                state.Inspect = (_, _, _, _) => { inspections++; return Task.FromResult(new ImageCacheState.Inspection { Fingerprint = "same", Dependencies = new long[] { 11 } }); };
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                Assert.Equal(before, keys.GetKey(item, ImageType.Primary, null, false));
                state.Start(Inventory(item, member).Object); await state.Initialization!;
                Assert.Equal(1, inspections);
                Assert.Equal(before, keys.GetKey(item, ImageType.Primary, null, false));
            }
        }

        [Fact]
        public void CorruptPartitionRecoveryDoesNotInvalidateOtherPartitions()
        {
            using var folder = new Folder(); var options = Options();
            var first = Item(typeof(Series), 10); var second = Item(typeof(Series), 11);
            Assert.NotEqual(ImageCacheState.Partition("i10"), ImageCacheState.Partition("i11"));
            string a, b;
            using (var state = State(folder))
            {
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                a = keys.GetKey(first, ImageType.Primary, options.Profiles[0], true);
                b = keys.GetKey(second, ImageType.Primary, options.Profiles[0], true);
            }
            string path = Path.Combine(folder.Path, "image-state-v2", ImageCacheState.Partition("i10").ToString("x3") + ".json");
            File.WriteAllText(path, "{ invalid"); File.WriteAllText(path + ".bak", "{ invalid");
            string recovered;
            using (var state = State(folder))
            {
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                recovered = keys.GetKey(first, ImageType.Primary, options.Profiles[0], true);
                Assert.NotEqual(a, recovered);
                Assert.Equal(b, keys.GetKey(second, ImageType.Primary, options.Profiles[0], true));
            }
            using (var state = State(folder))
            {
                var keys = new EmbyIcons.Services.ImageKeyService(options, state);
                Assert.Equal(recovered, keys.GetKey(first, ImageType.Primary, options.Profiles[0], true));
                Assert.Equal(b, keys.GetKey(second, ImageType.Primary, options.Profiles[0], true));
            }
        }

        [Fact]
        public async Task ConcurrentUpdateCannotCommitAnOldInspection()
        {
            using var folder = new Folder(); using var state = State(folder);
            var parent = Item(typeof(Series)); var episode = new Episode { InternalId = 11, SeriesId = 10, ParentId = 10 };
            var settings = ImageQueryTests.RatingsOnly(); settings.AudioIconAlignment = IconAlignment.TopLeft;
            var before = state.GetDataVersion(parent, settings);
            state.Observed(parent, settings, new ImageCacheState.Inspection { Fingerprint = "english", Dependencies = new long[] { 11 } }, 0, true);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            state.Inspect = async (_, _, _, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                { entered.TrySetResult(true); await release.Task.WaitAsync(token); return new ImageCacheState.Inspection { Fingerprint = "english", Dependencies = new long[] { 11 } }; }
                return new ImageCacheState.Inspection { Fingerprint = "french", Dependencies = new long[] { 11 } };
            };
            state.Start(Inventory(parent, episode).Object);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            state.Changed(episode); release.TrySetResult(true);
            await state.VerifyPendingAsync();
            Assert.True(calls >= 2);
            Assert.NotEqual(before, state.GetDataVersion(parent, settings));
        }

        [Fact]
        public async Task DisposingAnActiveInspectionDoesNotWriteAfterTheFinalCheckpoint()
        {
            using var folder = new Folder(); var state = State(folder);
            var item = Item(typeof(BoxSet)); var settings = ImageQueryTests.RatingsOnly();
            var before = state.GetDataVersion(item, settings);
            state.Observed(item, settings, new ImageCacheState.Inspection { Fingerprint = "before" }, 0, true);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Inspect = async (_, _, _, _) => { entered.TrySetResult(true); await release.Task; return new ImageCacheState.Inspection { Fingerprint = "late" }; };
            state.Start(Inventory(item).Object); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            state.Dispose();
            var files = Directory.GetFiles(Path.Combine(folder.Path, "image-state-v2"), "*.json").ToDictionary(p => p, File.ReadAllText);
            release.TrySetResult(true); await state.Initialization!;
            Assert.All(files, p => Assert.Equal(p.Value, File.ReadAllText(p.Key)));
            using var restored = State(folder);
            Assert.Equal(before, restored.GetDataVersion(item, settings));
        }

        [Fact]
        public async Task AStaleMetadataPageCannotOverwriteANewerItemEvent()
        {
            using var folder = new Folder(); using var state = State(folder);
            var old = new Series { InternalId = 10, CommunityRating = 8.4f };
            var current = new Series { InternalId = 10, CommunityRating = 9.1f };
            var settings = ImageQueryTests.RatingsOnly();
            var before = state.GetDataVersion(old, settings);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var library = Inventory(old);
            library.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>(), It.IsAny<CancellationToken>())).Returns(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return new BaseItem[] { old };
            });
            state.Start(library.Object);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            state.Changed(current);
            var newest = state.GetDataVersion(current, settings);
            Assert.NotEqual(before, newest);
            release.Set(); await state.Initialization!;
            Assert.Equal(newest, state.GetDataVersion(current, settings));
            await state.VerifyPendingAsync();
            Assert.Equal(newest, state.GetDataVersion(current, settings));
        }

        [Fact]
        public void BindingAfterAnUnrelatedColdProfileUpdatePreservesTheEmittedKey()
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options();
            var critic = ImageQueryTests.RatingsOnly();
            critic.CommunityScoreIconAlignment = IconAlignment.Disabled;
            critic.RottenTomatoesScoreIconAlignment = IconAlignment.TopLeft;
            options.Profiles.Add(new IconProfile { Settings = critic });
            var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(typeof(Series)); item.CommunityRating = 8.4f; item.CriticRating = 10;
            keys.GetKey(item, ImageType.Primary, null, false);
            item.CriticRating = 99; state.Changed(item);
            var lastCold = keys.GetKey(item, ImageType.Primary, null, false);
            Assert.Equal(lastCold, keys.GetKey(item, ImageType.Primary, options.Profiles[0], true));
            item.CriticRating = 51; state.Changed(item);
            Assert.Equal(lastCold, keys.GetKey(item, ImageType.Primary, null, false));
        }

        [Fact]
        public void IrrelevantAggregationSettingsCannotResetAChangedRatingToItsOriginalCacheKey()
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); options.Profiles[0].Settings.ShowSeriesIconsIfAllEpisodesHaveLanguage = true;
            var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var item = Item(typeof(Series)); item.CommunityRating = 8.4f;
            var original = keys.GetKey(item, ImageType.Primary, options.Profiles[0], true);
            item.CommunityRating = 9.2f; state.Changed(item);
            var updated = keys.GetKey(item, ImageType.Primary, options.Profiles[0], true);
            Assert.NotEqual(original, updated);
            options.Profiles[0].Settings.UseSeriesLiteMode = !options.Profiles[0].Settings.UseSeriesLiteMode;
            keys.Configure(options);
            Assert.Equal(updated, keys.GetKey(item, ImageType.Primary, options.Profiles[0], true));
        }

        [Fact]
        public async Task AnEmptyArtistIsRecheckedWhenItsFirstNonPhysicalMemberArrives()
        {
            using var folder = new Folder(); using var state = State(folder);
            var artist = Item(typeof(MusicArtist)); var track = new Audio { InternalId = 11, ParentId = 1234 };
            var settings = ImageQueryTests.RatingsOnly(); settings.EnableMusicAlbumAggregation = true; settings.AudioIconAlignment = IconAlignment.TopLeft;
            var before = state.GetDataVersion(artist, settings);
            state.Observed(artist, settings, new ImageCacheState.Inspection { Fingerprint = "empty" }, 0, true);
            bool member = false;
            state.Inspect = (_, _, _, _) => Task.FromResult(new ImageCacheState.Inspection { Fingerprint = member ? "audio" : "empty", Dependencies = member ? new long[] { 11 } : Array.Empty<long>() });
            state.Start(Inventory(artist, track).Object); await state.Initialization!;
            member = true; state.Changed(track, added: true); await state.VerifyPendingAsync();
            Assert.NotEqual(before, state.GetDataVersion(artist, settings));
        }

        [Fact]
        public void InapplicableFilenameRulesDoNotInvalidateOtherMediaKinds()
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var series = Item(typeof(Series));
            var before = keys.GetKey(series, ImageType.Primary, options.Profiles[0], true);
            options.Profiles[0].Settings.FilenameBasedIcons.Add(new FilenameIconMapping { Keyword = "remux", IconName = "remux", ApplyToMovies = true,
                ApplyToSeries = false, ApplyToSeasons = false, ApplyToEpisodes = false });
            keys.Configure(options);
            Assert.Equal(before, keys.GetKey(series, ImageType.Primary, options.Profiles[0], true));
        }

        [Fact]
        public void MusicAggregationSettingsCannotInvalidateTrackImages()
        {
            using var folder = new Folder(); using var state = State(folder);
            var options = Options(); options.Profiles[0].Settings.AudioIconAlignment = IconAlignment.TopLeft;
            var keys = new EmbyIcons.Services.ImageKeyService(options, state);
            var track = Item(typeof(Audio));
            var before = keys.GetKey(track, ImageType.Primary, options.Profiles[0], true);
            options.Profiles[0].Settings.EnableMusicAlbumAggregation = !options.Profiles[0].Settings.EnableMusicAlbumAggregation;
            options.Profiles[0].Settings.UseMusicAlbumLiteMode = !options.Profiles[0].Settings.UseMusicAlbumLiteMode;
            keys.Configure(options);
            Assert.Equal(before, keys.GetKey(track, ImageType.Primary, options.Profiles[0], true));
        }

        [Fact]
        public async Task DisabledFavoritesNeverModifyTheOriginalImageTimestamp()
        {
            using var h = await ImageQueryTests.Harness.Create();
            var item = AggregationTests.Prepare(new Series(), 400);
            item.ImageInfos = new[] { new ItemImageInfo { Type = ImageType.Primary, Path = "poster.jpg" } };
            var before = item.ImageInfos[0].DateModified;
            h.UserData.Raise(x => x.UserDataSaved += null, new UserDataSaveEventArgs { Item = item, SaveReason = UserDataSaveReason.UpdateUserRating });
            Assert.Equal(before, item.ImageInfos[0].DateModified);
            h.Library.Verify(x => x.UpdateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()), Times.Never());
        }
    }
}
