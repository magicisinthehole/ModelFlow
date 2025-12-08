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

        private List<VirtualizedGroup<T>>? _groups;
        private IReadOnlyList<GroupInfo>? _cachedStructure;
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

            // Register with VirtualizationManager for automatic memory reclamation
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
        /// Gets the cached group structure for layout calculations.
        /// Returns null if structure hasn't been loaded yet.
        /// </summary>
        public IReadOnlyList<GroupInfo>? GetLayoutStructure() => _cachedStructure;

        /// <summary>
        /// Ensures the group structure is loaded (synchronously waits if needed).
        /// Uses SemaphoreSlim to avoid deadlocks with async operations.
        /// </summary>
        public void EnsureStructureLoaded()
        {
            if (_isStructureLoaded) return;

            // Use async version and wait - SemaphoreSlim handles this correctly
            EnsureStructureLoadedAsync().GetAwaiter().GetResult();
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
                _cachedStructure = null;
                _isStructureLoaded = false;
            }
            finally
            {
                _structureLock.Release();
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
                    _cachedStructure = structures;

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

        private async Task<IEnumerable<DataItem<T>>> FetchGroupItems(ISourcePage<DataItem<T>> page, int groupIndex, int offset, int count, Action? signal)
        {
            return await _provider.GetGroupItemsAsync(page, groupIndex, offset, count, signal);
        }

        private DataItem<T> GetPlaceholder(int groupIndex, int itemOffset, int page, int pageOffset)
        {
            return _provider.GetItemPlaceHolder(groupIndex, itemOffset, page, pageOffset);
        }

        #region Real-Time Collection Manipulation

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
            if (!group.IsIndexLoaded(itemIndex))
                return false;

            group.InsertItemAt(itemIndex, item);
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
            if (!group.IsIndexLoaded(itemIndex))
            {
                // Just adjust the count if not loaded
                group.AdjustCount(-1);
                return true;
            }

            group.RemoveItemAt(itemIndex);
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
