namespace ModelFlow.DataVirtualization.DataManagement
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.ComponentModel;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Actions;
    using Interfaces;

    /// <summary>
    /// A collection of virtualized groups with per-group pagination.
    /// Groups are loaded on demand, and items within each group are also virtualized.
    /// Supports batch prefetching of adjacent groups for improved scrolling performance.
    /// </summary>
    /// <typeparam name="T">The type of items within groups.</typeparam>
    internal class VirtualizedGroupCollection<T> : IReadOnlyList<IVirtualizedGroup<T>>, INotifyCollectionChanged, INotifyPropertyChanged, IReclaimableService
        where T : class
    {
        private readonly IGroupedSourceProviderAsync<DataItem<T>> _provider;
        private readonly int _groupPageSize;
        private readonly int _itemPageSize;
        private readonly int _maxItemPagesPerGroup;
        private readonly SemaphoreSlim _structureLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _prefetchLock = new SemaphoreSlim(1, 1);
        private readonly HashSet<int> _prefetchedGroups = new HashSet<int>();
        private readonly HashSet<int> _prefetchingGroups = new HashSet<int>();

        private List<VirtualizedGroup<T>>? _groups;
        private volatile bool _isStructureLoaded;
        private volatile bool _isStructureLoading;

        /// <summary>
        /// Creates a new VirtualizedGroupCollection.
        /// </summary>
        /// <param name="provider">The grouped data source provider.</param>
        /// <param name="groupPageSize">How many groups to load at once (for future scalability).</param>
        /// <param name="itemPageSize">Page size for items within each group.</param>
        /// <param name="maxItemPagesPerGroup">Maximum item pages to cache per group.</param>
        public VirtualizedGroupCollection(
            IGroupedSourceProviderAsync<DataItem<T>> provider,
            int groupPageSize = 100,
            int itemPageSize = 20,
            int maxItemPagesPerGroup = 10)
        {
            _provider = provider;
            _groupPageSize = groupPageSize;
            _itemPageSize = itemPageSize;
            _maxItemPagesPerGroup = maxItemPagesPerGroup;

            VirtualizationManager.Instance.AddAction(new ReclaimPagesWA(this, "GroupCollection"));
        }

        /// <summary>
        /// Gets the virtualized group at the specified index.
        /// </summary>
        public IVirtualizedGroup<T> this[int index]
        {
            get
            {
                EnsureStructureLoaded();
                if (_groups == null || index < 0 || index >= _groups.Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _groups[index];
            }
        }

        /// <summary>
        /// Gets the number of groups.
        /// </summary>
        public int Count
        {
            get
            {
                EnsureStructureLoaded();
                return _groups?.Count ?? 0;
            }
        }

        /// <summary>
        /// Gets whether the group structure has been loaded.
        /// </summary>
        public bool IsStructureLoaded => _isStructureLoaded;

        /// <summary>
        /// Gets whether the group structure is currently loading.
        /// </summary>
        public bool IsStructureLoading => _isStructureLoading;

        /// <summary>
        /// Gets the group structure for layout calculations.
        /// Reads directly from the actual groups - single source of truth.
        /// Returns null if structure hasn't been loaded yet.
        /// </summary>
        public IReadOnlyList<GroupInfo>? GetLayoutStructure()
        {
            if (_groups == null) return null;
            return _groups.Select(g => new GroupInfo(g.Key, g.ItemCount, g.HeaderData)).ToList();
        }

        /// <summary>
        /// Returns this collection as a non-generic IReadOnlyList for UI binding.
        /// Since IVirtualizedGroup{T} extends IVirtualizedGroup, this is a safe cast.
        /// </summary>
        public IReadOnlyList<IVirtualizedGroup>? AsNonGeneric()
        {
            if (!_isStructureLoaded || _groups == null) return null;
            return new NonGenericGroupList<T>(_groups);
        }

        /// <summary>
        /// Wrapper to expose typed groups as non-generic for UI binding.
        /// </summary>
        private sealed class NonGenericGroupList<TItem> : IReadOnlyList<IVirtualizedGroup> where TItem : class
        {
            private readonly IReadOnlyList<VirtualizedGroup<TItem>> _groups;

            public NonGenericGroupList(IReadOnlyList<VirtualizedGroup<TItem>> groups)
            {
                _groups = groups;
            }

            public IVirtualizedGroup this[int index] => _groups[index];
            public int Count => _groups.Count;

            public IEnumerator<IVirtualizedGroup> GetEnumerator()
            {
                foreach (var group in _groups)
                    yield return group;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// Ensures the group structure is loaded (synchronously waits if needed).
        /// Uses SemaphoreSlim to avoid deadlocks with async operations.
        /// </summary>
        public void EnsureStructureLoaded()
        {
            if (_isStructureLoaded) return;

            // Offload to threadpool to avoid deadlock: LoadStructureInternalAsync uses
            // RunOnUiAsync to marshal back to UI, so blocking the UI thread here
            // with GetAwaiter().GetResult() would deadlock.
            Task.Run(() => EnsureStructureLoadedAsync()).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Loads the group structure asynchronously.
        /// Call this before binding to ensure structure is available.
        /// </summary>
        public async Task EnsureStructureLoadedAsync()
        {
            if (_isStructureLoaded) return;

            await _structureLock.WaitAsync();
            try
            {
                if (_isStructureLoaded) return;
                await LoadStructureInternalAsync();
            }
            finally
            {
                _structureLock.Release();
            }
        }

        /// <summary>
        /// Refreshes the group structure from the source.
        /// Call this after sort/filter changes.
        /// </summary>
        public async Task RefreshStructureAsync()
        {
            await _structureLock.WaitAsync();
            try
            {
                _isStructureLoaded = false;
                await LoadStructureInternalAsync();
            }
            finally
            {
                _structureLock.Release();
            }

            // Notify collection reset
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        /// <summary>
        /// Resets the collection, clearing all cached data.
        /// </summary>
        public void Reset()
        {
            _structureLock.Wait();
            try
            {
                if (_groups != null)
                {
                    foreach (var group in _groups)
                    {
                        group.Reset();
                    }
                }

                _groups = null;
                _isStructureLoaded = false;
            }
            finally
            {
                _structureLock.Release();
            }

            // Clear prefetch tracking
            _prefetchLock.Wait();
            try
            {
                _prefetchedGroups.Clear();
                _prefetchingGroups.Clear();
            }
            finally
            {
                _prefetchLock.Release();
            }

            _provider.OnReset(0);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        private async Task LoadStructureInternalAsync()
        {
            // Note: Callers must hold _structureLock before calling this method.
            // The lock serializes access, so we don't need an additional guard here.
            try
            {
                _isStructureLoading = true;

                // Fetch group structures (lightweight - just keys and counts)
                var structures = await _provider.GetGroupStructuresAsync();

                await VirtualizationManager.Instance.RunOnUiAsync(new ActionVirtualizationWrapper(() =>
                {
                    // Create or update groups
                    if (_groups == null)
                    {
                        _groups = new List<VirtualizedGroup<T>>();
                        for (var i = 0; i < structures.Count; i++)
                        {
                            _groups.Add(CreateGroup(i, structures[i]));
                        }
                    }
                    else
                    {
                        // Update existing groups where possible, recreate otherwise
                        var newGroups = new List<VirtualizedGroup<T>>();
                        for (var i = 0; i < structures.Count; i++)
                        {
                            var info = structures[i];
                            var existing = _groups.FirstOrDefault(g => g.Key == info.Key);
                            if (existing != null)
                            {
                                existing.UpdateGroupInfo(info);
                                existing.UpdateGroupIndex(i);
                                newGroups.Add(existing);
                            }
                            else
                            {
                                newGroups.Add(CreateGroup(i, info));
                            }
                        }
                        _groups = newGroups;
                    }

                    _isStructureLoaded = true;
                }));
            }
            finally
            {
                _isStructureLoading = false;
            }
        }

        private VirtualizedGroup<T> CreateGroup(int groupIndex, GroupInfo info)
        {
            return new VirtualizedGroup<T>(
                groupIndex,
                info,
                FetchGroupItems,
                GetPlaceholder,
                reclaimer: null,
                expiryComparer: null,
                pageSize: _itemPageSize,
                maxPages: _maxItemPagesPerGroup);
        }

        private async Task<IEnumerable<DataItem<T>>> FetchGroupItems(ISourcePage<DataItem<T>> page, int groupIndex, int offset, int count, Action? signal, CancellationToken cancellationToken = default)
        {
            var result = await _provider.GetGroupItemsAsync(page, groupIndex, offset, count, signal, cancellationToken);

            // Mark this group as prefetched (its first page is now loaded)
            if (offset == 0)
            {
                await _prefetchLock.WaitAsync();
                try
                {
                    _prefetchedGroups.Add(groupIndex);
                }
                finally
                {
                    _prefetchLock.Release();
                }

                // Trigger prefetch of adjacent groups (fire and forget)
                _ = PrefetchAdjacentGroupsAsync(groupIndex);
            }

            return result;
        }

        /// <summary>
        /// Prefetches the first page of items for groups adjacent to the specified group.
        /// This runs in the background to provide a smoother scrolling experience.
        /// </summary>
        private async Task PrefetchAdjacentGroupsAsync(int centerGroupIndex)
        {
            var prefetchCount = _provider.GroupPrefetchCount;
            if (prefetchCount <= 0 || _groups == null) return;

            // Determine which groups to prefetch (forward direction prioritized)
            var groupsToPrefetch = new List<int>();

            await _prefetchLock.WaitAsync();
            try
            {
                // Prefetch groups ahead (primary scroll direction)
                for (int i = 1; i <= prefetchCount; i++)
                {
                    var forwardIndex = centerGroupIndex + i;
                    if (forwardIndex < _groups.Count &&
                        !_prefetchedGroups.Contains(forwardIndex) &&
                        !_prefetchingGroups.Contains(forwardIndex))
                    {
                        groupsToPrefetch.Add(forwardIndex);
                        _prefetchingGroups.Add(forwardIndex);
                    }

                    // Also prefetch some groups behind (for scroll-back)
                    if (i <= prefetchCount / 2)
                    {
                        var backwardIndex = centerGroupIndex - i;
                        if (backwardIndex >= 0 &&
                            !_prefetchedGroups.Contains(backwardIndex) &&
                            !_prefetchingGroups.Contains(backwardIndex))
                        {
                            groupsToPrefetch.Add(backwardIndex);
                            _prefetchingGroups.Add(backwardIndex);
                        }
                    }
                }
            }
            finally
            {
                _prefetchLock.Release();
            }

            if (groupsToPrefetch.Count == 0) return;

            try
            {
                // Batch fetch all groups at once
                var prefetchedItems = await _provider.GetMultipleGroupItemsAsync(groupsToPrefetch, _itemPageSize);

                // Pre-populate each group's first page with the fetched items
                await VirtualizationManager.Instance.RunOnUiAsync(new ActionVirtualizationWrapper(() =>
                {
                    foreach (var kvp in prefetchedItems)
                    {
                        var groupIndex = kvp.Key;
                        var items = kvp.Value;
                        if (_groups != null && groupIndex < _groups.Count)
                        {
                            var group = _groups[groupIndex];
                            group.PrepopulateFirstPage(items);
                        }
                    }
                }));

                // Mark as prefetched
                await _prefetchLock.WaitAsync();
                try
                {
                    foreach (var groupIndex in groupsToPrefetch)
                    {
                        _prefetchedGroups.Add(groupIndex);
                        _prefetchingGroups.Remove(groupIndex);
                    }
                }
                finally
                {
                    _prefetchLock.Release();
                }
            }
            catch
            {
                // Prefetch failure is not critical - groups will load on demand
                await _prefetchLock.WaitAsync();
                try
                {
                    foreach (var groupIndex in groupsToPrefetch)
                    {
                        _prefetchingGroups.Remove(groupIndex);
                    }
                }
                finally
                {
                    _prefetchLock.Release();
                }
            }
        }

        private DataItem<T> GetPlaceholder(int groupIndex, int itemOffset, int page, int pageOffset)
        {
            return _provider.GetItemPlaceHolder(groupIndex, itemOffset, page, pageOffset);
        }

        #region Real-Time Collection Manipulation

        /// <summary>
        /// Inserts a new group at the specified index.
        /// </summary>
        /// <param name="groupIndex">The index to insert at.</param>
        /// <param name="info">The group info.</param>
        /// <returns>The created group, or null if insertion failed.</returns>
        internal VirtualizedGroup<T>? InsertGroupAt(int groupIndex, GroupInfo info)
        {
            VirtualizedGroup<T>? newGroup = null;

            _structureLock.Wait();
            try
            {
                if (_groups == null)
                {
                    _groups = new List<VirtualizedGroup<T>>();
                }

                if (groupIndex < 0 || groupIndex > _groups.Count)
                    return null;

                newGroup = CreateGroup(groupIndex, info);
                _groups.Insert(groupIndex, newGroup);

                // Update indices of subsequent groups
                for (int i = groupIndex + 1; i < _groups.Count; i++)
                {
                    _groups[i].UpdateGroupIndex(i);
                }
            }
            finally
            {
                _structureLock.Release();
            }

            // Notify listeners OUTSIDE the lock to prevent deadlocks
            if (newGroup != null)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add, newGroup, groupIndex));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            }

            return newGroup;
        }

        /// <summary>
        /// Inserts a new group or updates an existing group and moves it to the requested index.
        /// </summary>
        /// <param name="groupKey">The group key.</param>
        /// <param name="groupIndex">The desired index, computed against the structure excluding the existing group.</param>
        /// <param name="info">The updated group info.</param>
        /// <returns>The inserted or updated group, or null if the operation failed.</returns>
        internal VirtualizedGroup<T>? UpsertGroupAt(string groupKey, int groupIndex, GroupInfo info)
        {
            VirtualizedGroup<T>? targetGroup = null;
            int? originalIndex = null;
            int? finalIndex = null;
            bool inserted = false;

            _structureLock.Wait();
            try
            {
                _groups ??= new List<VirtualizedGroup<T>>();

                originalIndex = _groups.FindIndex(group => string.Equals(group.Key, groupKey, StringComparison.Ordinal));

                if (originalIndex < 0)
                {
                    if (groupIndex < 0 || groupIndex > _groups.Count)
                        return null;

                    targetGroup = CreateGroup(groupIndex, info);
                    _groups.Insert(groupIndex, targetGroup);
                    inserted = true;
                    finalIndex = groupIndex;

                    for (int i = groupIndex + 1; i < _groups.Count; i++)
                    {
                        _groups[i].UpdateGroupIndex(i);
                    }
                }
                else
                {
                    targetGroup = _groups[originalIndex.Value];
                    targetGroup.UpdateGroupInfo(info);

                    var boundedIndex = groupIndex;
                    if (boundedIndex < 0)
                    {
                        boundedIndex = 0;
                    }
                    else if (boundedIndex > _groups.Count - 1)
                    {
                        boundedIndex = _groups.Count - 1;
                    }
                    if (boundedIndex != originalIndex.Value)
                    {
                        _groups.RemoveAt(originalIndex.Value);
                        if (boundedIndex > _groups.Count)
                        {
                            boundedIndex = _groups.Count;
                        }

                        _groups.Insert(boundedIndex, targetGroup);
                        finalIndex = boundedIndex;

                        var startIndex = Math.Min(originalIndex.Value, boundedIndex);
                        for (int i = startIndex; i < _groups.Count; i++)
                        {
                            _groups[i].UpdateGroupIndex(i);
                        }
                    }
                    else
                    {
                        finalIndex = originalIndex.Value;
                    }
                }
            }
            finally
            {
                _structureLock.Release();
            }

            if (targetGroup == null || finalIndex == null)
            {
                return null;
            }

            if (inserted)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add, targetGroup, finalIndex.Value));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                return targetGroup;
            }

            if (originalIndex.HasValue && originalIndex.Value != finalIndex.Value)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Move, targetGroup, finalIndex.Value, originalIndex.Value));
            }

            return targetGroup;
        }

        /// <summary>
        /// Removes a group at the specified index.
        /// </summary>
        /// <param name="groupIndex">The index of the group to remove.</param>
        /// <returns>True if the group was removed; otherwise, false.</returns>
        internal bool RemoveGroupAt(int groupIndex)
        {
            VirtualizedGroup<T>? removedGroup = null;

            _structureLock.Wait();
            try
            {
                if (_groups == null || groupIndex < 0 || groupIndex >= _groups.Count)
                    return false;

                removedGroup = _groups[groupIndex];
                _groups.RemoveAt(groupIndex);

                for (int i = groupIndex; i < _groups.Count; i++)
                {
                    _groups[i].UpdateGroupIndex(i);
                }
            }
            finally
            {
                _structureLock.Release();
            }

            if (removedGroup != null)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove, removedGroup, groupIndex));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            }

            return removedGroup != null;
        }

        /// <summary>
        /// Inserts an item at a specific index within a group.
        /// Only works if the target index is in a loaded page.
        /// </summary>
        /// <param name="groupIndex">The group index.</param>
        /// <param name="itemIndex">The item index within the group.</param>
        /// <param name="item">The item to insert.</param>
        /// <returns>True if inserted, false if the index is not in a loaded page.</returns>
        internal bool InsertItemAt(int groupIndex, int itemIndex, DataItem<T> item)
        {
            if (_groups == null || groupIndex < 0 || groupIndex >= _groups.Count)
                return false;

            var group = _groups[groupIndex];
            if (!group.HasIndexInMemory(itemIndex))
                return false;

            group.InsertItemAt(itemIndex, item);
            RaiseGroupItemCountChanged(groupIndex, group);
            return true;
        }

        /// <summary>
        /// Removes an item at a specific index within a group.
        /// Only works if the target index is in a loaded page.
        /// </summary>
        /// <param name="groupIndex">The group index.</param>
        /// <param name="itemIndex">The item index within the group.</param>
        /// <returns>True if removed, false if the index is not in a loaded page.</returns>
        internal bool RemoveItemAt(int groupIndex, int itemIndex)
        {
            if (_groups == null || groupIndex < 0 || groupIndex >= _groups.Count)
                return false;

            var group = _groups[groupIndex];
            if (!group.HasIndexInMemory(itemIndex))
            {
                // Just adjust the count if not loaded
                group.AdjustCount(-1);
                RaiseGroupItemCountChanged(groupIndex, group);
                return true;
            }

            group.RemoveItemAt(itemIndex);
            RaiseGroupItemCountChanged(groupIndex, group);
            return true;
        }

        /// <summary>
        /// Appends an item to the end of a group.
        /// </summary>
        /// <param name="groupIndex">The group index.</param>
        /// <param name="item">The item to append.</param>
        internal void AppendItemToGroup(int groupIndex, DataItem<T> item)
        {
            if (_groups == null || groupIndex < 0 || groupIndex >= _groups.Count)
                return;

            var group = _groups[groupIndex];
            group.AppendItem(item);
            RaiseGroupItemCountChanged(groupIndex, group);
        }

        private void RaiseGroupItemCountChanged(int groupIndex, VirtualizedGroup<T> group)
        {
            CollectionChanged?.Invoke(this,
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Replace, group, group, groupIndex));
        }

        #endregion

        #region IReclaimableService Implementation

        /// <summary>
        /// Runs page reclamation across all groups.
        /// Called by VirtualizationManager on a periodic basis.
        /// </summary>
        public void RunClaim(string sectionContext)
        {
            if (_groups == null) return;

            // Take a snapshot to avoid collection modification during iteration
            List<VirtualizedGroup<T>> groupsSnapshot;
            _structureLock.Wait();
            try
            {
                if (_groups == null) return;
                groupsSnapshot = _groups.ToList();
            }
            finally
            {
                _structureLock.Release();
            }

            foreach (var group in groupsSnapshot)
            {
                group.RunClaim();
            }
        }

        #endregion

        #region IEnumerable

        public IEnumerator<IVirtualizedGroup<T>> GetEnumerator()
        {
            EnsureStructureLoaded();
            if (_groups == null) yield break;

            foreach (var group in _groups)
            {
                yield return group;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        #region Events

        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion
    }
}
