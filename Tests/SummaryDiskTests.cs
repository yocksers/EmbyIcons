using EmbyIcons.Caching;
using EmbyIcons.Helpers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace EmbyIcons.Tests
{
    public class SummaryDiskTests
    {
        public sealed class Payload { public string[] Languages { get; set; } = Array.Empty<string>(); }

        [Theory]
        [InlineData("5.61.1.5")]
        [InlineData("5.61.1.6")]
        public async Task SavedSummaryLoadsOnFirstLookupAndMigratesCompatibleRelease(string release)
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Options.EnableSummaryDiskCache = true;
            var id = Guid.NewGuid();
            var signature = SummaryStoreKeys.GetSignature(h.Settings, h.Options, null);
            var text = release + "|" + (h.Options.IconsFolder ?? "") + "|" + (int)h.Options.IconLoadingMode + "|" + SettingsChangeAnalyzer.DataSignature(h.Settings) + "|";
            var legacy = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
            var path = Path.Combine(h.Plugin.DataFolderPath, "summary-reuse-test.json");
            Directory.CreateDirectory(h.Plugin.DataFolderPath);
            File.WriteAllText(path, JsonSerializer.Serialize(new SummaryStoreFile<Payload> { Version = 1,
                Entries = new Dictionary<Guid, StoredSummary<Payload>> { [id] = new StoredSummary<Payload> {
                    Signature = legacy, Fingerprint = "unchanged-members", LastUsed = DateTime.UtcNow,
                    Data = new Payload { Languages = new[] { "english" } } } } }));
            var store = new SummaryDiskStore<Payload>("summary-reuse-test.json");
            try
            {
                Assert.True(store.TryGet(id, signature, "unchanged-members", out var data,
                    SummaryStoreKeys.GetCompatibleSignatures(h.Settings, h.Options, null)));
                Assert.Equal(new[] { "english" }, data!.Languages);
                Assert.False(store.TryGet(id, signature, "changed-members", out _));
                Assert.False(store.TryGet(id, "different-settings", "unchanged-members", out _));
                store.Save();
                var saved = JsonSerializer.Deserialize<SummaryStoreFile<Payload>>(File.ReadAllText(path))!;
                Assert.Equal(signature, saved.Entries[id].Signature);
                var restarted = new SummaryDiskStore<Payload>("summary-reuse-test.json");
                try { Assert.True(restarted.TryGet(id, signature, "unchanged-members", out _)); }
                finally { restarted.Dispose(); }
            }
            finally { store.Dispose(); }
        }

        [Fact]
        public async Task UnidentifiedMoviesWithTheSameTitleKeepIndependentPathsAndCacheKeys()
        {
            using var h = await ImageQueryTests.Harness.Create();
            h.Settings.FilenameBasedIcons.Add(new EmbyIcons.Configuration.FilenameIconMapping { Keyword = "remux", IconName = "remux", ApplyToMovies = true });
            h.Plugin.ImageKeys.Configure(h.Options);
            var first = AggregationTests.Prepare(new Movie { Name = "Same title" }, 850);
            var second = AggregationTests.Prepare(new Movie { Name = "Same title" }, 851);
            first.Path = "/media/remux.mkv"; second.Path = "/media/other.mkv";
            Assert.Empty(MovieVersions.Queries(first));
            Assert.Empty(MovieVersions.Queries(second));
            var service = h.Plugin.Enhancer._overlayDataService;
            Assert.Single(service.GetBaseOverlayData(first, h.Settings, h.Options).FilenameBasedIcons);
            Assert.Empty(service.GetBaseOverlayData(second, h.Settings, h.Options).FilenameBasedIcons);
            Assert.NotEqual(h.Plugin.Enhancer.GetConfigurationCacheKey(first, ImageType.Primary), h.Plugin.Enhancer.GetConfigurationCacheKey(second, ImageType.Primary));
            Assert.DoesNotContain(h.Library.Invocations, i => i.Method.Name == "GetItemList");
        }
    }
}
