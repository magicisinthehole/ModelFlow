namespace ModelFlow.DataVirtualization.DataManagement
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Interfaces;
    using Pageing;

    /// <summary>
    /// Implementation of a virtualized group with its own pagination.
    /// Each group manages its items independently for true per-group virtualization.
    /// </summary>
    /// <typeparam name="T">The type of items in the group.</typeparam>
    public class VirtualizedGroup<T> : IVirtualizedGroup<T> where T : class
    {
        private readonly GroupPaginationManager<DataItem<T>> _paginationManager;
        private readonly VirtualizedGroupItemsList _itemsList;
        private GroupInfo _groupInfo;

        /// <summary>
        /// Creates a new VirtualizedGroup.
        /// </summary>
        /// <param name="groupIndex">The index of this group.</param>
        /// <param name="groupInfo">The group information (key, item count).</param>
        /// <param name="fetchItems">Function to fetch items for this group.
        /// Receives the page to materialize placeholders in place via SetItem().</param>
        /// <param name="getPlaceholder">Function to get placeholder items: (groupIndex, itemOffset, page, pageOffset) => placeholder.</param>
        /// <param name="reclaimer">Page reclaimer for memory management.</param>
        /// <param name="expiryComparer">Optional expiry comparer for page lifecycle.</param>
        /// <param name="pageSize">Page size for this group's pagination.</param>
        /// <param name="maxPages">Maximum pages to cache per group.</param>
        internal VirtualizedGroup(
            int groupIndex,
            GroupInfo groupInfo,
            Func<ISourcePage<DataItem<T>>, int, int, int, Action?, Task<IEnumerable<DataItem<T>>>> fetchItems,
            Func<int, int, int, int, DataItem<T>> getPlaceholder,
            IPageReclaimer<DataItem<T>>? reclaimer = null,
            IPageExpiryComparer? expiryComparer = null,
            int pageSize = 20,
            int maxPages = 10)
        {
            GroupIndex = groupIndex;
            _groupInfo = groupInfo;
            _paginationManager = new GroupPaginationManager<DataItem<T>>(
                groupIndex,
                groupInfo.ItemCount,
                fetchItems,
                getPlaceholder,
                reclaimer,
                expiryComparer,
                pageSize,
                maxPages);
            _itemsList = new VirtualizedGroupItemsList(this);
        }

        /// <inheritdoc />
        public int GroupIndex { get; private set; }

        /// <inheritdoc />
        public string Key => _groupInfo.Key;

        /// <inheritdoc />
        public object? HeaderData => _groupInfo.HeaderData;

        /// <inheritdoc />
        public int ItemCount => _paginationManager.Count;

        /// <inheritdoc />
        public IReadOnlyList<DataItem<T>> Items => _itemsList;

        /// <inheritdoc />
        public object GetItemAt(int index) => Items[index];

        /// <inheritdoc />
        public bool IsItemsInitialized => true; // Items list is always available

        /// <inheritdoc />
        public bool IsFullyLoaded => _paginationManager.IsFullyLoaded;

        /// <summary>
        /// Updates the group info (e.g., after structure refresh).
        /// </summary>
        internal void UpdateGroupInfo(GroupInfo newInfo)
        {
            _groupInfo = newInfo;
            _paginationManager.SetItemCount(newInfo.ItemCount);
        }

        /// <summary>
        /// Updates the group index (e.g., after a group is inserted before this one).
        /// </summary>
        internal void UpdateGroupIndex(int newIndex)
        {
            GroupIndex = newIndex;
            _paginationManager.UpdateGroupIndex(newIndex);
        }

        /// <summary>
        /// Resets all loaded pages.
        /// </summary>
        internal void Reset()
        {
            _paginationManager.Reset();
        }

        /// <summary>
        /// Runs page reclamation.
        /// </summary>
        internal void RunClaim()
        {
            _paginationManager.RunClaim();
        }

        /// <summary>
        /// Pre-populates the first page (page 0) with already-fetched items.
        /// Used by batch prefetching to fill groups before they're accessed.
        /// If the first page is already loaded, this is a no-op.
        /// </summary>
        /// <param name="items">The items to populate the first page with.</param>
        internal void PrepopulateFirstPage(IReadOnlyList<DataItem<T>> items)
        {
            _paginationManager.PrepopulateFirstPage(items);
        }

        #region Real-Time Insertion Support

        /// <inheritdoc />
        public bool HasGotCount => _paginationManager.HasGotCount;

        /// <inheritdoc />
        public void AdjustCount(int delta)
        {
            _paginationManager.AdjustCount(delta);
        }

        /// <inheritdoc />
        public bool IsIndexLoaded(int index)
        {
            return _paginationManager.IsIndexLoaded(index);
        }

        /// <inheritdoc />
        public IReadOnlyList<int> GetLoadedPageNumbers()
        {
            return _paginationManager.GetLoadedPageNumbers();
        }

        /// <summary>
        /// Appends an item to the end of this group.
        /// </summary>
        /// <param name="item">The item to append.</param>
        internal void AppendItem(DataItem<T> item)
        {
            _paginationManager.Append(item);
        }

        /// <summary>
        /// Inserts an item at a specific index.
        /// Only call if IsIndexLoaded returns true.
        /// </summary>
        /// <param name="index">The index to insert at.</param>
        /// <param name="item">The item to insert.</param>
        internal void InsertItemAt(int index, DataItem<T> item)
        {
            _paginationManager.InsertAt(index, item);
        }

        /// <summary>
        /// Removes an item at a specific index.
        /// Only call if IsIndexLoaded returns true.
        /// </summary>
        /// <param name="index">The index to remove at.</param>
        internal void RemoveItemAt(int index)
        {
            _paginationManager.RemoveAt(index);
        }

        #endregion

        /// <summary>
        /// Internal list wrapper that provides IReadOnlyList access to virtualized items.
        /// </summary>
        private class VirtualizedGroupItemsList : IReadOnlyList<DataItem<T>>
        {
            private readonly VirtualizedGroup<T> _group;

            public VirtualizedGroupItemsList(VirtualizedGroup<T> group)
            {
                _group = group;
            }

            public DataItem<T> this[int index] => _group._paginationManager.GetAt(index);

            public int Count => _group.ItemCount;

            public IEnumerator<DataItem<T>> GetEnumerator()
            {
                for (var i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
