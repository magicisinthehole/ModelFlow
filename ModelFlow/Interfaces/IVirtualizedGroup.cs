namespace ModelFlow.DataVirtualization.Interfaces
{
    using System.Collections.Generic;
    using DataManagement;

    /// <summary>
    /// Represents a virtualized group with its own pagination.
    /// Each group manages its items independently, allowing true per-group virtualization.
    /// </summary>
    /// <typeparam name="T">The type of items in the group.</typeparam>
    public interface IVirtualizedGroup<T> where T : class
    {
        /// <summary>
        /// The zero-based index of this group in the parent collection.
        /// </summary>
        int GroupIndex { get; }

        /// <summary>
        /// The group key (e.g., artist name, year).
        /// </summary>
        string Key { get; }

        /// <summary>
        /// Optional header data for the group.
        /// Can be used to store additional metadata for group headers.
        /// </summary>
        object? HeaderData { get; }

        /// <summary>
        /// Total item count in this group.
        /// </summary>
        int ItemCount { get; }

        /// <summary>
        /// Virtualized access to items within this group.
        /// Items are loaded on-demand when accessed.
        /// </summary>
        IReadOnlyList<DataItem<T>> Items { get; }

        /// <summary>
        /// Gets whether items have been accessed (pages may be loading or loaded).
        /// </summary>
        bool IsItemsInitialized { get; }

        /// <summary>
        /// Gets whether all loaded pages in this group are fully loaded.
        /// False if any pages are still in loading state.
        /// </summary>
        bool IsFullyLoaded { get; }

        #region Real-Time Insertion Support

        /// <summary>
        /// Gets whether the item count has been established for this group.
        /// </summary>
        bool HasGotCount { get; }

        /// <summary>
        /// Adjusts the item count without triggering a data fetch.
        /// Use when items are added/removed from the underlying source.
        /// </summary>
        /// <param name="delta">The change in count (+1 for insert, -1 for remove).</param>
        void AdjustCount(int delta);

        /// <summary>
        /// Checks if the specified index falls within a currently loaded page.
        /// </summary>
        /// <param name="index">The zero-based index within this group.</param>
        /// <returns>True if the page containing this index is loaded.</returns>
        bool IsIndexLoaded(int index);

        /// <summary>
        /// Gets the loaded page numbers for debugging/diagnostics.
        /// </summary>
        IReadOnlyList<int> GetLoadedPageNumbers();

        #endregion
    }
}
