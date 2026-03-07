namespace ModelFlow.DataVirtualization.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Interface for providing grouped/hierarchical data with per-group virtualization.
    /// Parallel to <see cref="IPagedSourceProviderAsync{T}"/> but for grouped data.
    /// </summary>
    /// <typeparam name="T">The type of items within groups.</typeparam>
    internal interface IGroupedSourceProviderAsync<T> : IBaseSourceProvider
    {
        /// <summary>
        /// Gets the total number of groups.
        /// </summary>
        Task<int> GetGroupCountAsync();

        /// <summary>
        /// Gets the group structures (keys and item counts) without loading items.
        /// This enables layout calculation and virtualization decisions without data loading.
        /// The returned list should be ordered according to the current sort order.
        /// </summary>
        Task<IReadOnlyList<GroupInfo>> GetGroupStructuresAsync();

        /// <summary>
        /// Gets items within a specific group.
        /// The provider receives the page to update placeholders in place via SetItem.
        /// </summary>
        /// <param name="page">The source page containing placeholders to update.</param>
        /// <param name="groupIndex">The zero-based group index.</param>
        /// <param name="offset">Offset within the group.</param>
        /// <param name="count">Number of items to fetch.</param>
        /// <param name="signal">Optional signal callback when filter is captured.</param>
        Task<IEnumerable<T>> GetGroupItemsAsync(ISourcePage<T> page, int groupIndex, int offset, int count, Action? signal = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the total item count for a specific group.
        /// </summary>
        /// <param name="groupIndex">The zero-based group index.</param>
        Task<int> GetGroupItemCountAsync(int groupIndex);

        /// <summary>
        /// Gets a placeholder for an item in a loading state.
        /// </summary>
        /// <param name="groupIndex">The group index.</param>
        /// <param name="itemOffset">The offset within the group.</param>
        /// <param name="page">The page number within the group.</param>
        /// <param name="pageOffset">The offset within the page.</param>
        T GetItemPlaceHolder(int groupIndex, int itemOffset, int page, int pageOffset);

        /// <summary>
        /// Replaces an old item with a new item.
        /// </summary>
        void Replace(T old, T newItem);

        /// <summary>
        /// Checks if an item exists in any group.
        /// </summary>
        Task<bool> ContainsAsync(T item);

        /// <summary>
        /// Gets the index of an item as a tuple of (groupIndex, itemIndex).
        /// Returns (-1, -1) if not found.
        /// </summary>
        Task<(int groupIndex, int itemIndex)> IndexOfAsync(T item);

        /// <summary>
        /// Gets items for multiple groups in a single batch operation.
        /// Used for prefetching adjacent groups to reduce database round-trips.
        /// </summary>
        /// <param name="groupIndices">The indices of groups to fetch.</param>
        /// <param name="itemsPerGroup">Number of items to fetch per group (typically first page).</param>
        /// <returns>Dictionary mapping group index to fetched items.</returns>
        Task<IReadOnlyDictionary<int, IReadOnlyList<T>>> GetMultipleGroupItemsAsync(
            IReadOnlyList<int> groupIndices,
            int itemsPerGroup);

        /// <summary>
        /// Gets or sets the number of groups to prefetch ahead when loading a group.
        /// Default is 0 (no prefetching). Set to a positive number to enable batch prefetching.
        /// </summary>
        int GroupPrefetchCount { get; }
    }
}
