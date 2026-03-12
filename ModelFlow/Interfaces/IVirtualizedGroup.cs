namespace ModelFlow.DataVirtualization.Interfaces
{
    using System.Collections.Generic;
    using DataManagement;

    /// <summary>
    /// Non-generic interface for virtualized groups.
    /// Enables UI components to access groups without knowing the item type.
    /// </summary>
    public interface IVirtualizedGroup
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
        /// </summary>
        object? HeaderData { get; }

        /// <summary>
        /// Total item count in this group.
        /// </summary>
        int ItemCount { get; }

        /// <summary>
        /// Gets the item at the specified index as a non-generic object.
        /// Returns DataItem&lt;T&gt; - accessing triggers pagination if needed.
        /// </summary>
        /// <param name="index">Zero-based index within this group.</param>
        /// <returns>The DataItem at the specified index.</returns>
        object GetItemAt(int index);

        /// <summary>
        /// Gets whether items have been accessed.
        /// </summary>
        bool IsItemsInitialized { get; }

        /// <summary>
        /// Gets whether all loaded pages are fully loaded.
        /// </summary>
        bool IsFullyLoaded { get; }

        /// <summary>
        /// Gets whether the item count has been established.
        /// </summary>
        bool HasGotCount { get; }

        /// <summary>
        /// Adjusts the item count without triggering a data fetch.
        /// </summary>
        void AdjustCount(int delta);

        /// <summary>
        /// Sets the count to an authoritative value without expanding pages.
        /// </summary>
        void SetKnownCount(int count);

        /// <summary>
        /// Checks if the specified index falls within a loaded page.
        /// </summary>
        bool IsIndexLoaded(int index);

        /// <summary>
        /// Checks if the specified index falls within a page that currently exists in memory,
        /// including placeholder pages that are still being fetched.
        /// </summary>
        bool HasIndexInMemory(int index);
    }

    /// <summary>
    /// Represents a virtualized group with its own pagination.
    /// Each group manages its items independently, allowing true per-group virtualization.
    /// Extends the non-generic interface with typed item access.
    /// </summary>
    /// <typeparam name="T">The type of items in the group.</typeparam>
    public interface IVirtualizedGroup<T> : IVirtualizedGroup where T : class
    {
        /// <summary>
        /// Virtualized access to items within this group.
        /// Items are loaded on-demand when accessed.
        /// </summary>
        IReadOnlyList<DataItem<T>> Items { get; }

        /// <summary>
        /// Gets the loaded page numbers for debugging/diagnostics.
        /// </summary>
        IReadOnlyList<int> GetLoadedPageNumbers();

        /// <summary>
        /// Gets an already-materialized item from the specified index without triggering pagination.
        /// Returns false if the slot is not currently backed by an in-memory page entry.
        /// </summary>
        bool TryGetInMemoryItem(int index, out DataItem<T> item);
    }
}
