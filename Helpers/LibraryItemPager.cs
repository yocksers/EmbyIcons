using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyIcons.Helpers
{
    internal static class LibraryItemPager
    {
        public static IEnumerable<BaseItem> EnumerateAll(ILibraryManager libraryManager, Func<InternalItemsQuery> createQuery, int batchSize, Action<int>? reportTotal = null)
        {
            var snapshot = libraryManager.GetItemIds(createQuery());
            reportTotal?.Invoke(snapshot.Length);
            if (snapshot.Length == 0) yield break;

            var pending = new HashSet<Guid>(snapshot);
            int startIndex = 0;

            while (pending.Count > 0)
            {
                var query = createQuery();
                query.StartIndex = startIndex;
                query.Limit = batchSize;
                query.OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending), (ItemSortBy.DateCreated, SortOrder.Ascending) };

                int pageCount = 0;
                foreach (var item in libraryManager.GetItemList(query))
                {
                    pageCount++;
                    if (item != null && pending.Remove(item.Id))
                    {
                        yield return item;
                    }
                }

                if (pageCount < batchSize) break;
                startIndex += pageCount;
            }

            foreach (var id in pending.ToArray())
            {
                var item = libraryManager.GetItemById(id);
                if (item != null)
                {
                    yield return item;
                }
            }
        }
    }
}
