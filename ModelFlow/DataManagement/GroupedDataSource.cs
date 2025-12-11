namespace ModelFlow.DataVirtualization.DataManagement
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Actions;
    using Interfaces;

    /// <summary>
    /// Non-generic base class for GroupedDataSource providing static callback support.
    /// </summary>
    public abstract class GroupedDataSource : INotifyPropertyChanged
    {
        /// <summary>
        /// Default number of groups to prefetch ahead when loading group items.
        /// </summary>
        public const int DefaultGroupPrefetchCount = 5;

        /// <summary>
        /// Static callbacks for monitoring CRUD operations across all GroupedDataSource instances.
        /// </summary>
        public static IDataSourceCallbacks? DataSourceCallbacks;

        private bool _isInitialized;
        private bool _isActive;

        /// <summary>
        /// Gets or sets whether the data source has been initialized.
        /// </summary>
        public bool IsInitialized
        {
            get => _isInitialized;
            protected internal set
            {
                if (_isInitialized == value) return;
                _isInitialized = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets whether the data source is currently loading.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            protected internal set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets the data collection for binding.
        /// </summary>
        public abstract IEnumerable DataCollection { get; }

        /// <summary>
        /// Gets the virtualized groups with non-generic access.
        /// UI components can use this to access groups without knowing the item type.
        /// Returns null if structure hasn't been loaded yet.
        /// </summary>
        public abstract IReadOnlyList<IVirtualizedGroup>? Groups { get; }

        /// <summary>
        /// Gets the cached group structure for layout calculations.
        /// Returns null if structure hasn't been loaded yet.
        /// </summary>
        public abstract IReadOnlyList<GroupInfo>? LayoutStructure { get; }

        /// <summary>
        /// Gets whether the structure has been loaded.
        /// </summary>
        public abstract bool IsStructureLoaded { get; }

        /// <summary>
        /// Ensures the group structure is loaded.
        /// </summary>
        public abstract Task EnsureStructureLoadedAsync();

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? FilterQueryCleared;

        /// <summary>
        /// Fired when the groups collection changes (groups added/removed).
        /// UI should subscribe to this to react to dynamic group changes.
        /// </summary>
        public event System.Collections.Specialized.NotifyCollectionChangedEventHandler? GroupsCollectionChanged;

        protected void RaiseFilterQueryCleared()
        {
            FilterQueryCleared?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected void RaiseGroupsCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            GroupsCollectionChanged?.Invoke(this, e);
        }
    }

    /// <summary>
    /// Abstract base class for grouped/hierarchical data sources with per-group virtualization.
    /// Parallel to <see cref="DataSource{TViewModel, TModel}"/> but for grouped data.
    /// </summary>
    /// <typeparam name="TViewModel">The view model type for items.</typeparam>
    /// <typeparam name="TModel">The model type from the data layer.</typeparam>
    public abstract class GroupedDataSource<TViewModel, TModel> : GroupedDataSource, IGroupedSourceProviderAsync<DataItem<TViewModel>>
        where TViewModel : class
    {
        private readonly Func<TModel, TViewModel> _selector;
        private readonly VirtualizedGroupCollection<TViewModel> _collection;
        private readonly bool _autoSyncEnabled;
        private readonly int _groupPrefetchCount;
        private readonly int _itemPageSize;
        private Func<IQueryable<TModel>, IQueryable<TModel>>? _filterQuery;
        private int _operationCount;

        /// <summary>
        /// Creates a new GroupedDataSource.
        /// </summary>
        /// <param name="selector">Function to convert model to view model.</param>
        /// <param name="itemPageSize">Page size for items within each group.</param>
        /// <param name="maxItemPagesPerGroup">Maximum item pages to cache per group.</param>
        /// <param name="autoSync">Whether to automatically sync changes back to the database for IAutoSynchronize ViewModels.</param>
        /// <param name="groupPrefetchCount">Number of groups to prefetch ahead. Set to 0 to disable prefetching.</param>
        protected GroupedDataSource(
            Func<TModel, TViewModel> selector,
            int itemPageSize = 20,
            int maxItemPagesPerGroup = 10,
            bool autoSync = true,
            int groupPrefetchCount = DefaultGroupPrefetchCount)
        {
            _autoSyncEnabled = autoSync;
            _selector = selector;
            _groupPrefetchCount = groupPrefetchCount;
            _itemPageSize = itemPageSize;
            _collection = new VirtualizedGroupCollection<TViewModel>(this, itemPageSize: itemPageSize, maxItemPagesPerGroup: maxItemPagesPerGroup);

            // Forward collection change events to the base class event for UI binding
            _collection.CollectionChanged += (sender, e) => RaiseGroupsCollectionChanged(e);

            SortDescriptionList = new SortDescriptionList();
            SortDescriptionList.CollectionChanged += OnSortDescriptionListChanged;
        }

        private void OnSortDescriptionListChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            Invalidate();
        }

        #region Public Properties

        /// <summary>
        /// Gets the virtualized group collection with typed item access.
        /// </summary>
        public IReadOnlyList<IVirtualizedGroup<TViewModel>> TypedGroups => _collection;

        /// <summary>
        /// Gets the virtualized groups with non-generic access for UI binding.
        /// </summary>
        public override IReadOnlyList<IVirtualizedGroup>? Groups => _collection.AsNonGeneric();

        /// <summary>
        /// Gets the data collection for binding.
        /// </summary>
        public override IEnumerable DataCollection => _collection;

        /// <summary>
        /// Sort descriptions for ordering groups and items.
        /// </summary>
        public SortDescriptionList SortDescriptionList { get; }

        /// <summary>
        /// Gets the cached group structure for layout calculations.
        /// </summary>
        public override IReadOnlyList<GroupInfo>? LayoutStructure => GetLayoutStructure();

        /// <summary>
        /// Gets the cached group structure for layout calculations.
        /// Returns null if structure hasn't been loaded yet.
        /// </summary>
        public IReadOnlyList<GroupInfo>? GetLayoutStructure() => _collection.GetLayoutStructure();

        /// <summary>
        /// Gets whether the structure has been loaded.
        /// </summary>
        public override bool IsStructureLoaded => _collection.IsStructureLoaded;

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets a filter query on the data source.
        /// </summary>
        public void SetFilterQuery(Func<IQueryable<TModel>, IQueryable<TModel>>? filterQuery, bool invalidate = true)
        {
            if (_filterQuery != filterQuery)
            {
                _filterQuery = filterQuery;
                if (invalidate) Invalidate();

                if (filterQuery == null)
                {
                    RaiseFilterQueryCleared();
                }
            }
        }

        /// <summary>
        /// Invalidates the data source, forcing a refresh.
        /// </summary>
        public void Invalidate()
        {
            _collection.Reset();
        }

        /// <summary>
        /// Ensures the group structure is loaded.
        /// </summary>
        public override Task EnsureStructureLoadedAsync() => _collection.EnsureStructureLoadedAsync();

        /// <summary>
        /// Refreshes the group structure without clearing all data.
        /// </summary>
        public Task RefreshStructureAsync() => _collection.RefreshStructureAsync();

        /// <summary>
        /// Replaces all sort descriptions atomically with a single invalidation.
        /// </summary>
        /// <param name="descriptions">The new sort descriptions.</param>
        public void SetSortDescriptions(IEnumerable<SortDescription> descriptions)
        {
            SortDescriptionList.CollectionChanged -= OnSortDescriptionListChanged;

            try
            {
                SortDescriptionList.Clear();
                foreach (var desc in descriptions.Reverse())
                {
                    SortDescriptionList.Add(desc);
                }
            }
            finally
            {
                SortDescriptionList.CollectionChanged += OnSortDescriptionListChanged;
                Invalidate();
            }
        }

        /// <summary>
        /// Ensures the datasource is initialised before performing operations.
        /// </summary>
        public async Task EnsureInitializedAsync()
        {
            if (!IsInitialized)
            {
                await EnsureStructureLoadedAsync();

                while (!IsInitialized)
                {
                    await Task.Delay(10);
                }
            }
        }

        #endregion

        #region Abstract Methods - Must Implement

        /// <summary>
        /// Gets the group structures (keys and item counts) without loading items.
        /// This should perform a lightweight GROUP BY COUNT query.
        /// </summary>
        /// <param name="filterQuery">The filter query to apply.</param>
        protected abstract Task<IReadOnlyList<GroupInfo>> GetGroupStructuresAsync(Func<IQueryable<TModel>, IQueryable<TModel>> filterQuery);

        /// <summary>
        /// Gets items within a specific group.
        /// </summary>
        /// <param name="groupKey">The group key.</param>
        /// <param name="offset">Offset within the group.</param>
        /// <param name="count">Number of items to fetch.</param>
        /// <param name="filterSortQuery">Filter and sort query.</param>
        protected abstract Task<IEnumerable<TModel>> GetGroupItemsAsync(
            string groupKey,
            int offset,
            int count,
            Func<IQueryable<TModel>, IQueryable<TModel>> filterSortQuery);

        /// <summary>
        /// Gets a placeholder view model for loading state.
        /// </summary>
        /// <param name="groupIndex">The group index.</param>
        /// <param name="itemOffset">The offset within the group.</param>
        /// <param name="page">The page number within the group.</param>
        /// <param name="pageOffset">The offset within the page.</param>
        protected abstract TViewModel GetPlaceHolder(int groupIndex, int itemOffset, int page, int pageOffset);

        /// <summary>
        /// Gets the model for a given view model.
        /// Override to enable search/lookup functionality.
        /// </summary>
        /// <param name="viewModel">The view model.</param>
        /// <returns>The model, or null if not available.</returns>
        protected virtual TModel? GetModelForViewModel(TViewModel viewModel)
        {
            return default;
        }

        /// <summary>
        /// Checks if two models are equal (e.g., same database ID).
        /// Override to enable search/lookup functionality.
        /// </summary>
        protected virtual bool ModelsEqual(TModel a, TModel b)
        {
            return EqualityComparer<TModel>.Default.Equals(a, b);
        }

        /// <summary>
        /// Gets the group key for an item. Used for CRUD operations to determine group membership.
        /// Override to enable CreateAsync functionality.
        /// </summary>
        /// <param name="viewModel">The view model.</param>
        /// <returns>The group key, or null if unknown.</returns>
        protected virtual string? GetGroupKeyForItem(TViewModel viewModel)
        {
            return null;
        }

        /// <summary>
        /// Gets items for multiple groups in a single batch operation.
        /// Override to enable batch prefetching for improved scrolling performance.
        /// The default implementation falls back to sequential single-group fetches.
        /// </summary>
        /// <param name="groupKeys">The group keys to fetch items for.</param>
        /// <param name="itemsPerGroup">Number of items to fetch per group (typically first page).</param>
        /// <param name="filterSortQuery">Filter and sort query.</param>
        /// <returns>Dictionary mapping group key to fetched items.</returns>
        protected virtual async Task<IReadOnlyDictionary<string, IReadOnlyList<TModel>>> GetMultipleGroupItemsAsync(
            IReadOnlyList<string> groupKeys,
            int itemsPerGroup,
            Func<IQueryable<TModel>, IQueryable<TModel>> filterSortQuery)
        {
            // Default implementation: sequential fetches (override for batch optimization)
            var results = new Dictionary<string, IReadOnlyList<TModel>>();
            foreach (var groupKey in groupKeys)
            {
                var items = await GetGroupItemsAsync(groupKey, 0, itemsPerGroup, filterSortQuery);
                results[groupKey] = items.ToList();
            }
            return results;
        }

        #endregion

        #region CRUD Operations - Abstract Methods

        /// <summary>
        /// Creates an entry in the datasource to store the ViewModel.
        /// Override to enable CreateAsync functionality.
        /// </summary>
        /// <param name="item">The viewmodel.</param>
        /// <returns>True if the operation was a success or false if it failed.</returns>
        protected virtual Task<bool> DoCreateAsync(TViewModel item)
        {
            throw new NotImplementedException("Override DoCreateAsync to enable create operations.");
        }

        /// <summary>
        /// Updates an entry in the datasource.
        /// Override to enable UpdateAsync functionality.
        /// </summary>
        /// <param name="viewModel">The viewmodel.</param>
        /// <returns>True if the operation was a success or false if it failed.</returns>
        protected virtual Task<bool> DoUpdateAsync(TViewModel viewModel)
        {
            throw new NotImplementedException("Override DoUpdateAsync to enable update operations.");
        }

        /// <summary>
        /// Deletes an entry in the datasource.
        /// Override to enable DeleteAsync functionality.
        /// </summary>
        /// <param name="item">The viewmodel.</param>
        /// <returns>True if the operation was a success or false if it failed.</returns>
        protected virtual Task<bool> DoDeleteAsync(TViewModel item)
        {
            throw new NotImplementedException("Override DoDeleteAsync to enable delete operations.");
        }

        #endregion

        #region CRUD Operations - Public API

        /// <summary>
        /// Creates the viewmodel in the datasource.
        /// </summary>
        /// <param name="viewModel">The viewmodel to create.</param>
        /// <returns>A tuple containing success status, group index, item index, and the created DataItem.</returns>
        public async Task<(bool success, int groupIndex, int itemIndex, DataItem<TViewModel>? item)> CreateAsync(TViewModel viewModel)
        {
            await EnsureInitializedAsync();

            SetFilterQuery(null, true);

            try
            {
                if (DataSourceCallbacks is { })
                {
                    if (!await DataSourceCallbacks.OnBeforeCreateOperation(viewModel))
                    {
                        return (false, -1, -1, null);
                    }
                }

                bool isSuccess = await DoCreateAsync(viewModel);
                int groupIndex = -1;
                int itemIndex = -1;
                DataItem<TViewModel>? item = null;

                if (isSuccess)
                {
                    // Refresh structure to pick up potential new group or count changes
                    await RefreshStructureAsync();

                    // Find where the item should appear
                    (groupIndex, itemIndex) = await IndexOfAsync(viewModel);

                    item = DataItem.Create(viewModel);
                    await InitializeItemAsync(viewModel);

                    // If we found a position and it's in a loaded page, insert it
                    if (groupIndex >= 0 && itemIndex >= 0)
                    {
                        var group = GetGroupByIndex(groupIndex);
                        if (group != null && group.IsIndexLoaded(itemIndex))
                        {
                            _collection.InsertItemAt(groupIndex, itemIndex, item);
                        }
                    }
                }

                if (DataSourceCallbacks is { })
                {
                    await DataSourceCallbacks.OnCreateOperationCompleted(viewModel, isSuccess);
                }

                return (isSuccess, groupIndex, itemIndex, item);
            }
            catch (Exception e)
            {
                if (DataSourceCallbacks is { })
                {
                    await DataSourceCallbacks.OnCreateException(viewModel, e);
                }
            }

            return (false, -1, -1, null);
        }

        /// <summary>
        /// Updates the viewmodel in the datasource.
        /// </summary>
        /// <param name="viewModel">The viewmodel to update.</param>
        /// <returns>True if the update was successful.</returns>
        public async Task<bool> UpdateAsync(TViewModel viewModel)
        {
            try
            {
                if (DataSourceCallbacks is { })
                {
                    if (!await DataSourceCallbacks.OnBeforeUpdateOperation(viewModel))
                    {
                        return false;
                    }
                }

                bool isSuccess = await DoUpdateAsync(viewModel);

                if (DataSourceCallbacks is { })
                {
                    await DataSourceCallbacks.OnUpdateOperationCompleted(viewModel, isSuccess);
                }

                return isSuccess;
            }
            catch (Exception e)
            {
                if (DataSourceCallbacks is { })
                {
                    await DataSourceCallbacks.OnUpdateException(viewModel, e);
                }
            }

            return false;
        }

        /// <summary>
        /// Deletes an item from the datasource.
        /// </summary>
        /// <param name="dataItem">The DataItem wrapper to delete.</param>
        /// <returns>True if the delete was successful.</returns>
        public async Task<bool> DeleteAsync(DataItem<TViewModel> dataItem)
        {
            try
            {
                if (DataSourceCallbacks is { })
                {
                    if (!await DataSourceCallbacks.OnBeforeDeleteOperation(dataItem))
                    {
                        return false;
                    }
                }

                // Find the item's position
                var (groupIndex, itemIndex) = await IndexOfAsync(dataItem.Item);

                bool isSuccess = await DoDeleteAsync(dataItem.Item);

                if (isSuccess && groupIndex >= 0 && itemIndex >= 0)
                {
                    // Remove from collection if in a loaded page
                    var group = GetGroupByIndex(groupIndex);
                    if (group != null && group.IsIndexLoaded(itemIndex))
                    {
                        _collection.RemoveItemAt(groupIndex, itemIndex);
                    }
                    else
                    {
                        // Just adjust the count
                        group?.AdjustCount(-1);
                    }
                }

                if (DataSourceCallbacks is { })
                {
                    await DataSourceCallbacks.OnDeleteOperationCompleted(dataItem, isSuccess);
                }

                return isSuccess;
            }
            catch (Exception e)
            {
                if (DataSourceCallbacks is { })
                {
                    await DataSourceCallbacks.OnDeleteException(dataItem, e);
                }
            }

            return false;
        }

        #endregion

        #region Search/Lookup Support

        /// <summary>
        /// Determines if the data source contains an item.
        /// Override GetModelForViewModel and ModelsEqual for full implementation.
        /// </summary>
        /// <param name="item">The item to check.</param>
        /// <returns>True if found, false otherwise.</returns>
        protected virtual Task<bool> ContainsAsync(TViewModel item)
        {
            // Default implementation - search loaded pages only
            foreach (var group in _collection)
            {
                foreach (var dataItem in group.Items)
                {
                    if (!dataItem.IsLoading && EqualityComparer<TViewModel>.Default.Equals(dataItem.Item, item))
                    {
                        return Task.FromResult(true);
                    }
                }
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Gets the index of an item as (groupIndex, itemIndex).
        /// Override GetModelForViewModel and ModelsEqual for full implementation.
        /// </summary>
        /// <param name="item">The item to find.</param>
        /// <returns>Tuple of (groupIndex, itemIndex) or (-1, -1) if not found.</returns>
        protected virtual Task<(int groupIndex, int itemIndex)> IndexOfAsync(TViewModel item)
        {
            // Default implementation - search loaded pages only
            for (var g = 0; g < _collection.Count; g++)
            {
                var group = _collection[g];
                for (var i = 0; i < group.Items.Count; i++)
                {
                    var dataItem = group.Items[i];
                    if (!dataItem.IsLoading && EqualityComparer<TViewModel>.Default.Equals(dataItem.Item, item))
                    {
                        return Task.FromResult((g, i));
                    }
                }
            }
            return Task.FromResult((-1, -1));
        }

        /// <summary>
        /// Calculates the sorted insertion index for an item within a specific group.
        /// Uses binary search - may make DB calls to compare items.
        /// </summary>
        /// <param name="groupKey">The group key to search within.</param>
        /// <param name="item">The item to find insertion index for.</param>
        /// <returns>The index where the item should be inserted to maintain sort order within the group.</returns>
        public async Task<(int groupIndex, int itemIndex)> GetSortedInsertionIndexAsync(TViewModel item)
        {
            await EnsureInitializedAsync();

            var model = GetModelForViewModel(item);
            if (model is null)
            {
                return (-1, -1);
            }

            // Determine which group this item belongs to
            var groupKey = GetGroupKeyForItem(item);
            if (groupKey == null)
            {
                return (-1, -1);
            }

            // Find the group
            var structure = GetLayoutStructure();
            if (structure == null)
            {
                return (-1, -1);
            }

            int groupIndex = -1;
            int groupItemCount = 0;
            for (int i = 0; i < structure.Count; i++)
            {
                if (structure[i].Key == groupKey)
                {
                    groupIndex = i;
                    groupItemCount = structure[i].ItemCount;
                    break;
                }
            }

            if (groupIndex == -1)
            {
                // Group doesn't exist yet - would need to create it
                return (-1, 0);
            }

            if (groupItemCount == 0)
            {
                return (groupIndex, 0);
            }

            // Binary search within the group
            int start = 0;
            int end = groupItemCount;

            while (start < end)
            {
                int mid = start + ((end - start) / 2);

                var sampleItems = await GetGroupItemsAsync(groupKey, mid, 1, BuildFilterSortQuery);
                var sample = sampleItems.FirstOrDefault();

                if (sample == null)
                {
                    return (groupIndex, groupItemCount);
                }

                // Use sort query to determine order
                var compareItems = new[] { model, sample };
                var sorted = BuildSortQuery(compareItems.AsQueryable()).ToList();

                if (ModelsEqual(sorted[0], model))
                {
                    // Our item sorts before the sample
                    end = mid;
                }
                else
                {
                    // Our item sorts after (or equal to) the sample
                    start = mid + 1;
                }
            }

            return (groupIndex, start);
        }

        #endregion

        #region Real-Time Insertion Support

        /// <summary>
        /// Gets all currently loaded (non-placeholder) items across all groups.
        /// </summary>
        public IEnumerable<TViewModel> GetLoadedItems()
        {
            foreach (var group in _collection)
            {
                foreach (var dataItem in group.Items)
                {
                    if (!dataItem.IsLoading)
                    {
                        yield return dataItem.Item;
                    }
                }
            }
        }

        /// <summary>
        /// Gets a group by key, or null if not found.
        /// </summary>
        /// <param name="groupKey">The group key to find.</param>
        /// <returns>The group, or null.</returns>
        public IVirtualizedGroup<TViewModel>? GetGroupByKey(string groupKey)
        {
            return _collection.FirstOrDefault(g => g.Key == groupKey);
        }

        /// <summary>
        /// Gets a group by index.
        /// </summary>
        /// <param name="index">The group index.</param>
        /// <returns>The group.</returns>
        public IVirtualizedGroup<TViewModel>? GetGroupByIndex(int index)
        {
            if (index < 0 || index >= _collection.Count)
                return null;
            return _collection[index];
        }

        /// <summary>
        /// Updates a loaded item in place without invalidating the data source.
        /// Use this for property updates (rating, play count, etc.) that don't affect sort order.
        /// </summary>
        /// <param name="predicate">A function to find the item to update.</param>
        /// <param name="updatedItem">The updated item data.</param>
        /// <returns>True if the item was found and updated, false otherwise.</returns>
        public bool UpdateItemInPlace(Func<TViewModel, bool> predicate, TViewModel updatedItem)
        {
            foreach (var group in _collection)
            {
                foreach (var dataItem in group.Items)
                {
                    if (!dataItem.IsLoading && dataItem.Item != null && predicate(dataItem.Item))
                    {
                        dataItem.UpdateItem(updatedItem);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Finds the first loaded item matching the predicate and calls an action on it.
        /// Use this for in-place mutations (like rating updates) where the existing item instance
        /// must be modified rather than replaced, to preserve UI bindings.
        /// </summary>
        /// <param name="predicate">A function to find the item.</param>
        /// <param name="action">An action to perform on the found item.</param>
        /// <returns>True if the item was found and the action was called, false otherwise.</returns>
        public bool MutateItem(Func<TViewModel, bool> predicate, Action<TViewModel> action)
        {
            foreach (var group in _collection)
            {
                foreach (var dataItem in group.Items)
                {
                    if (!dataItem.IsLoading && dataItem.Item != null && predicate(dataItem.Item))
                    {
                        action(dataItem.Item);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Appends an item to the end of a specific group without invalidating.
        /// Use this when adding items that should appear at the end of a group.
        /// </summary>
        /// <param name="groupKey">The group key to append to.</param>
        /// <param name="item">The item to append.</param>
        /// <returns>The created DataItem wrapper, or null if the group wasn't found.</returns>
        public DataItem<TViewModel>? AppendItemToGroup(string groupKey, TViewModel item)
        {
            var group = GetGroupByKey(groupKey);
            if (group == null) return null;

            var dataItem = DataItem.Create(item);
            _collection.AppendItemToGroup(group.GroupIndex, dataItem);
            return dataItem;
        }

        /// <summary>
        /// Inserts a new group at the specified index.
        /// </summary>
        /// <param name="groupIndex">The index to insert at.</param>
        /// <param name="groupKey">The group key.</param>
        /// <param name="itemCount">Initial item count for the group.</param>
        /// <param name="headerData">Optional header data for the group.</param>
        /// <returns>The created group, or null if insertion failed.</returns>
        public IVirtualizedGroup<TViewModel>? InsertGroup(int groupIndex, string groupKey, int itemCount = 0, object? headerData = null)
        {
            var info = new GroupInfo(groupKey, itemCount, headerData);
            return _collection.InsertGroupAt(groupIndex, info);
        }

        /// <summary>
        /// Gets the sorted insertion index for a new group based on the group key.
        /// Uses binary search on the existing groups.
        /// </summary>
        /// <param name="groupKey">The group key to find insertion position for.</param>
        /// <returns>The index where the group should be inserted.</returns>
        public int GetGroupInsertionIndex(string groupKey)
        {
            var structure = _collection.GetLayoutStructure();
            if (structure == null || structure.Count == 0)
                return 0;

            // Binary search for the correct position
            int left = 0;
            int right = structure.Count - 1;

            while (left <= right)
            {
                int mid = (left + right) / 2;
                int cmp = string.Compare(structure[mid].Key, groupKey, StringComparison.OrdinalIgnoreCase);

                if (cmp < 0)
                    left = mid + 1;
                else if (cmp > 0)
                    right = mid - 1;
                else
                    return mid; // Exact match (shouldn't happen for new group)
            }

            return left;
        }

        /// <summary>
        /// Inserts an item at a specific index within a group without invalidating.
        /// Only works if the target index is in a loaded page.
        /// </summary>
        /// <param name="groupKey">The group key to insert into.</param>
        /// <param name="index">The index within the group.</param>
        /// <param name="item">The item to insert.</param>
        /// <returns>The created DataItem wrapper, or null if the group/index wasn't valid.</returns>
        public DataItem<TViewModel>? InsertItemInGroup(string groupKey, int index, TViewModel item)
        {
            var group = GetGroupByKey(groupKey);
            if (group == null) return null;

            var dataItem = DataItem.Create(item);
            if (_collection.InsertItemAt(group.GroupIndex, index, dataItem))
            {
                return dataItem;
            }
            return null;
        }

        /// <summary>
        /// Removes the first item matching the predicate from any group without invalidating.
        /// </summary>
        /// <param name="predicate">A function to find the item to remove.</param>
        /// <returns>True if an item was found and removed, false otherwise.</returns>
        public bool RemoveItemFromGroups(Func<TViewModel, bool> predicate)
        {
            for (int g = 0; g < _collection.Count; g++)
            {
                var group = _collection[g];
                for (int i = 0; i < group.Items.Count; i++)
                {
                    var dataItem = group.Items[i];
                    if (!dataItem.IsLoading && predicate(dataItem.Item))
                    {
                        return _collection.RemoveItemAt(g, i);
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Adjusts the known count for a specific group without invalidating or fetching from DB.
        /// Use this when an item is added to the underlying data source
        /// but doesn't need to appear in memory immediately (page not loaded).
        /// </summary>
        /// <param name="groupKey">The group key.</param>
        /// <param name="delta">The change in count (+1 for insert, -1 for remove).</param>
        public void AdjustGroupCount(string groupKey, int delta)
        {
            var group = GetGroupByKey(groupKey);
            group?.AdjustCount(delta);
        }

        /// <summary>
        /// Checks if a specific index within a group falls within a currently loaded page.
        /// Use this to decide whether to insert in-memory or just adjust count.
        /// </summary>
        /// <param name="groupKey">The group key.</param>
        /// <param name="index">The index within the group.</param>
        /// <returns>True if the page containing this index is currently loaded.</returns>
        public bool IsGroupIndexLoaded(string groupKey, int index)
        {
            var group = GetGroupByKey(groupKey);
            return group?.IsIndexLoaded(index) ?? false;
        }

        #endregion

        #region Operation Tracking

        private void StartOperation()
        {
            var operations = Interlocked.Increment(ref _operationCount);
            if (operations > 0)
            {
                IsActive = true;
            }
        }

        private void EndOperation()
        {
            var operations = Interlocked.Decrement(ref _operationCount);
            if (operations <= 0)
            {
                IsActive = false;
            }
        }

        #endregion

        #region IGroupedSourceProviderAsync Implementation

        bool ISynchronized.IsSynchronized => false;
        object ISynchronized.SyncRoot { get; } = new object();

        void IBaseSourceProvider.OnReset(int count)
        {
            // Reset handled by collection
        }

        async Task<int> IGroupedSourceProviderAsync<DataItem<TViewModel>>.GetGroupCountAsync()
        {
            var structures = await GetGroupStructuresAsync(BuildFilterQuery);
            return structures.Count;
        }

        async Task<IReadOnlyList<GroupInfo>> IGroupedSourceProviderAsync<DataItem<TViewModel>>.GetGroupStructuresAsync()
        {
            StartOperation();
            try
            {
                var result = await GetGroupStructuresAsync(BuildFilterQuery);
                IsInitialized = true;
                return result;
            }
            finally
            {
                EndOperation();
            }
        }

        async Task<IEnumerable<DataItem<TViewModel>>> IGroupedSourceProviderAsync<DataItem<TViewModel>>.GetGroupItemsAsync(
            ISourcePage<DataItem<TViewModel>> page, int groupIndex, int offset, int count, Action? signal)
        {
            StartOperation();
            try
            {
                // Wait for structure to be loaded to avoid race condition
                await _collection.EnsureStructureLoadedAsync();

                var structure = _collection.GetLayoutStructure();
                if (structure == null || groupIndex >= structure.Count)
                    return Enumerable.Empty<DataItem<TViewModel>>();

                var groupKey = structure[groupIndex].Key;

                // Capture filter before signaling
                var filter = _filterQuery;
                signal?.Invoke();

                var models = (await GetGroupItemsAsync(groupKey, offset, count, x => BuildFilterSortQuery(x, filter))).ToList();

                if (models.Count != count)
                {
                    throw new Exception(
                        "The number of items returned from the data source is different than expected. " +
                        "This has caused an inconsistent state. Check the GroupedDataSource implementation. " +
                        this.GetType().FullName);
                }

                var results = new List<DataItem<TViewModel>>();

                var completionSource = new TaskCompletionSource<bool>();

                // Materialize on UI thread to update placeholders in place
                await VirtualizationManager.Instance.RunOnUiAsync(new ActionVirtualizationWrapper(async () =>
                {
                    for (int i = 0; i < models.Count; i++)
                    {
                        results.Add(await Materialize(page, i, models[i]));
                    }
                    completionSource.SetResult(true);
                }));

                await completionSource.Task;

                return results;
            }
            finally
            {
                EndOperation();
            }
        }

        /// <summary>
        /// Materializes a model into the existing placeholder DataItem at the given page index.
        /// This updates the placeholder in place, preserving UI bindings.
        /// </summary>
        private async Task<DataItem<TViewModel>> Materialize(ISourcePage<DataItem<TViewModel>> page, int pageIndex, TModel model)
        {
            var placeholder = page.GetAt(pageIndex);
            var viewModel = _selector(model);
            await InitializeItemAsync(viewModel);
            placeholder.SetItem(viewModel);
            OnMaterializedInternal(placeholder);
            return placeholder;
        }

        private void OnMaterializedInternal(DataItem<TViewModel> item)
        {
            if (_autoSyncEnabled && item.Item is IAutoSynchronize { IsManaged: false })
            {
                this.AutoManage(item);
            }

            OnMaterialized(item);
        }

        /// <summary>
        /// Called when a ViewModel is materialized. It is called AFTER any async initialization has occurred.
        /// This can be used to subscribe to model changes. After this is called, Database synchronization will be active.
        /// </summary>
        /// <param name="item">The DataItem wrapper containing the materialized ViewModel.</param>
        protected virtual void OnMaterialized(DataItem<TViewModel> item)
        {
        }

        /// <summary>
        /// Applies filter and sort queries with a specific filter.
        /// </summary>
        private IQueryable<TModel> BuildFilterSortQuery(IQueryable<TModel> queryable, Func<IQueryable<TModel>, IQueryable<TModel>>? filter)
        {
            queryable = filter?.Invoke(queryable) ?? queryable;

            foreach (var sort in SortDescriptionList)
            {
                if (sort.Direction != null)
                {
                    queryable = AddSorting(queryable, sort.Direction.Value, sort.PropertyName);
                }
            }

            return queryable;
        }

        Task<int> IGroupedSourceProviderAsync<DataItem<TViewModel>>.GetGroupItemCountAsync(int groupIndex)
        {
            var structure = _collection.GetLayoutStructure();
            if (structure == null || groupIndex >= structure.Count)
                return Task.FromResult(0);

            return Task.FromResult(structure[groupIndex].ItemCount);
        }

        DataItem<TViewModel> IGroupedSourceProviderAsync<DataItem<TViewModel>>.GetItemPlaceHolder(int groupIndex, int itemOffset, int page, int pageOffset)
        {
            return DataItem.Create(GetPlaceHolder(groupIndex, itemOffset, page, pageOffset), true);
        }

        void IGroupedSourceProviderAsync<DataItem<TViewModel>>.Replace(DataItem<TViewModel> old, DataItem<TViewModel> newItem)
        {
            // Item replacement handled by DataItem.SetItem
        }

        Task<bool> IGroupedSourceProviderAsync<DataItem<TViewModel>>.ContainsAsync(DataItem<TViewModel> item)
        {
            return ContainsAsync(item.Item);
        }

        Task<(int groupIndex, int itemIndex)> IGroupedSourceProviderAsync<DataItem<TViewModel>>.IndexOfAsync(DataItem<TViewModel> item)
        {
            return IndexOfAsync(item.Item);
        }

        int IGroupedSourceProviderAsync<DataItem<TViewModel>>.GroupPrefetchCount => _groupPrefetchCount;

        async Task<IReadOnlyDictionary<int, IReadOnlyList<DataItem<TViewModel>>>> IGroupedSourceProviderAsync<DataItem<TViewModel>>.GetMultipleGroupItemsAsync(
            IReadOnlyList<int> groupIndices,
            int itemsPerGroup)
        {
            StartOperation();
            try
            {
                await _collection.EnsureStructureLoadedAsync();

                var structure = _collection.GetLayoutStructure();
                if (structure == null)
                    return new Dictionary<int, IReadOnlyList<DataItem<TViewModel>>>();

                // Map group indices to group keys
                var groupKeys = new List<string>();
                var indexToKeyMap = new Dictionary<int, string>();
                foreach (var groupIndex in groupIndices)
                {
                    if (groupIndex >= 0 && groupIndex < structure.Count)
                    {
                        var key = structure[groupIndex].Key;
                        groupKeys.Add(key);
                        indexToKeyMap[groupIndex] = key;
                    }
                }

                if (groupKeys.Count == 0)
                    return new Dictionary<int, IReadOnlyList<DataItem<TViewModel>>>();

                // Capture filter
                var filter = _filterQuery;

                // Batch fetch from data source
                var modelsByKey = await GetMultipleGroupItemsAsync(
                    groupKeys,
                    itemsPerGroup,
                    x => BuildFilterSortQuery(x, filter));

                // Convert models to DataItems
                var results = new Dictionary<int, IReadOnlyList<DataItem<TViewModel>>>();
                foreach (var kvp in indexToKeyMap)
                {
                    var groupIndex = kvp.Key;
                    var groupKey = kvp.Value;
                    if (modelsByKey.TryGetValue(groupKey, out var models))
                    {
                        var dataItems = new List<DataItem<TViewModel>>();
                        foreach (var model in models)
                        {
                            var viewModel = _selector(model);
                            await InitializeItemAsync(viewModel);
                            var dataItem = DataItem.Create(viewModel);
                            OnMaterializedInternal(dataItem);
                            dataItems.Add(dataItem);
                        }
                        results[groupIndex] = dataItems;
                    }
                }

                return results;
            }
            finally
            {
                EndOperation();
            }
        }

        #endregion

        #region Protected Helpers

        /// <summary>
        /// Applies the filter query.
        /// </summary>
        protected IQueryable<TModel> BuildFilterQuery(IQueryable<TModel> queryable)
        {
            return _filterQuery?.Invoke(queryable) ?? queryable;
        }

        /// <summary>
        /// Applies sort queries only.
        /// </summary>
        protected IQueryable<TModel> BuildSortQuery(IQueryable<TModel> queryable)
        {
            foreach (var sort in SortDescriptionList)
            {
                if (sort.Direction != null)
                {
                    queryable = AddSorting(queryable, sort.Direction.Value, sort.PropertyName);
                }
            }

            return queryable;
        }

        /// <summary>
        /// Applies filter and sort queries.
        /// </summary>
        protected IQueryable<TModel> BuildFilterSortQuery(IQueryable<TModel> queryable)
        {
            queryable = BuildFilterQuery(queryable);

            foreach (var sort in SortDescriptionList)
            {
                if (sort.Direction != null)
                {
                    queryable = AddSorting(queryable, sort.Direction.Value, sort.PropertyName);
                }
            }

            return queryable;
        }

        private IQueryable<TModel> AddSorting(IQueryable<TModel> query, ListSortDirection sortDirection, string propertyName)
        {
            var param = Expression.Parameter(typeof(TModel));
            Expression prop = param;

            foreach (var member in propertyName.Split('.'))
            {
                prop = Expression.PropertyOrField(prop, member);
            }

            var sortLambda = Expression.Lambda(prop, param);
            var isAlreadyOrdered = query.Expression.Type == typeof(IOrderedQueryable<TModel>);

            Expression<Func<IOrderedQueryable<TModel>>>? sortMethod = null;

            if (sortDirection == ListSortDirection.Ascending)
            {
                sortMethod = isAlreadyOrdered
                    ? (Expression<Func<IOrderedQueryable<TModel>>>)(() => ((IOrderedQueryable<TModel>)query).ThenBy<TModel, object?>(k => null))
                    : () => query.OrderBy<TModel, object?>(k => null);
            }
            else if (sortDirection == ListSortDirection.Descending)
            {
                sortMethod = isAlreadyOrdered
                    ? (Expression<Func<IOrderedQueryable<TModel>>>)(() => ((IOrderedQueryable<TModel>)query).ThenByDescending<TModel, object?>(k => null))
                    : () => query.OrderByDescending<TModel, object?>(k => null);
            }

            if (!(sortMethod?.Body is MethodCallExpression methodCallExpression))
                return query;

            var method = methodCallExpression.Method.GetGenericMethodDefinition();
            var genericSortMethod = method.MakeGenericMethod(typeof(TModel), prop.Type);

            return (IOrderedQueryable<TModel>)genericSortMethod.Invoke(query, new object[] { query, sortLambda })!;
        }

        /// <summary>
        /// Initializes an item if it implements INeedsInitializationAsync.
        /// </summary>
        protected async Task InitializeItemAsync(TViewModel viewModel)
        {
            if (viewModel is INeedsInitializationAsync toInitialize)
            {
                await toInitialize.InitializeAsync();
            }
        }

        #endregion
    }

    /// <summary>
    /// Simplified GroupedDataSource where TViewModel and TModel are the same type.
    /// </summary>
    /// <typeparam name="TViewModel">The view model/model type.</typeparam>
    public abstract class GroupedDataSource<TViewModel> : GroupedDataSource<TViewModel, TViewModel>
        where TViewModel : class
    {
        protected GroupedDataSource(
            int itemPageSize = 20,
            int maxItemPagesPerGroup = 10,
            int groupPrefetchCount = DefaultGroupPrefetchCount)
            : base(x => x, itemPageSize, maxItemPagesPerGroup, autoSync: true, groupPrefetchCount: groupPrefetchCount)
        {
        }

        protected override TViewModel? GetModelForViewModel(TViewModel viewModel)
        {
            return viewModel;
        }
    }
}
