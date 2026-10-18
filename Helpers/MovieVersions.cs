using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class MovieVersions
    {
        // Provider names are namespaces. Never group arbitrary provider fields,
        // filenames, titles or catalogue numbers that may identify unrelated works.
        private static readonly string[] Providers = { "Tmdb", "Imdb", "Douban", "JavDB", "JavBus" };

        internal static IEnumerable<string> Groups(BaseItem item)
        {
            if (!string.IsNullOrEmpty(item.PresentationUniqueKey)) yield return "emby:" + item.PresentationUniqueKey;
            var provider = Provider(item);
            if (provider.HasValue) yield return provider.Value.Key + ":" + provider.Value.Value;
        }

        private static KeyValuePair<string, string>? Provider(BaseItem item)
        {
            foreach (var name in Providers)
                foreach (var pair in item.ProviderIds)
                    if (name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                        return new KeyValuePair<string, string>(name, pair.Value);
            return null;
        }

        internal static IEnumerable<InternalItemsQuery> Queries(Movie item)
        {
            // This is the same explicit relationship used by Video.GetAlternateVersionIds.
            // Reading the saved key is memory-only; executing this query belongs to rendering.
            if (!string.IsNullOrEmpty(item.PresentationUniqueKey))
                yield return new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { "Movie" },
                    PresentationUniqueKey = item.PresentationUniqueKey, Limit = 50 };
            var provider = Provider(item);
            if (provider.HasValue) yield return new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { "Movie" },
                AnyProviderIdEquals = new[] { provider.Value }, Limit = 50 };
        }
    }
}
