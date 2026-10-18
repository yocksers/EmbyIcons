using EmbyIcons.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EmbyIcons.Helpers
{
    internal static class ImageDependencies
    {
        // Called after real rendering input has been requested, or by the background
        // verifier. Never called by Supports/GetConfigurationCacheKey.
        internal static long[] Read(ILibraryManager library, BaseItem item, ProfileSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if ((item is Series || item is Season) && OverlayRequirements.VideoChildren(item, settings))
                return library.GetInternalItemIds(new InternalItemsQuery { Parent = item, Recursive = true, IncludeItemTypes = new[] { "Episode" },
                    Limit = settings.UseSeriesLiteMode ? 1 : (int?)null,
                    OrderBy = settings.UseSeriesLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>() })
                    .Concat(library.GetInternalItemIds(new InternalItemsQuery { Parent = item, Recursive = true, IncludeItemTypes = new[] { "Season" } })).Distinct().ToArray();
            if ((item is MusicAlbum || item is MusicArtist) && settings.EnableMusicAlbumAggregation && OverlayRequirements.MusicChildren(item, settings))
                return library.GetInternalItemIds(new InternalItemsQuery { Parent = item, Recursive = true, IncludeItemTypes = new[] { "Audio" },
                    Limit = settings.UseMusicAlbumLiteMode ? 1 : (int?)null,
                    OrderBy = settings.UseMusicAlbumLiteMode ? new[] { (ItemSortBy.SortName, SortOrder.Ascending) } : Array.Empty<(string, SortOrder)>() });
            if (item is Movie && OverlayRequirements.MoviePaths(settings))
            {
                return MovieVersions.Queries((Movie)item).SelectMany(library.GetInternalItemIds).Distinct().Take(50).ToArray();
            }
            if (item is BoxSet)
            {
                var members = library.GetInternalItemIds(new InternalItemsQuery { CollectionIds = new[] { item.InternalId },
                    Recursive = true, IncludeItemTypes = new[] { "Movie", "Series", "Episode" } });
                if (!OverlayRequirements.VideoChildren(item, settings)) return members;
                var series = library.GetInternalItemIds(new InternalItemsQuery { CollectionIds = new[] { item.InternalId }, IncludeItemTypes = new[] { "Series" } });
                var result = new HashSet<long>(members);
                for (int offset = 0; offset < series.Length; offset += 500)
                {
                    token.ThrowIfCancellationRequested();
                    result.UnionWith(library.GetInternalItemIds(new InternalItemsQuery { AncestorIds = series.Skip(offset).Take(500).ToArray(),
                        Recursive = true, IncludeItemTypes = new[] { "Episode" } }));
                }
                return result.ToArray();
            }
            return Array.Empty<long>();
        }
    }
}
