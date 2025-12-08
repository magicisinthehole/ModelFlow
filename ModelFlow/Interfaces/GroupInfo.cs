namespace ModelFlow.DataVirtualization.Interfaces
{
    /// <summary>
    /// Lightweight structure describing a group without loading its items.
    /// This enables layout calculation and virtualization decisions without data loading.
    /// </summary>
    public readonly struct GroupInfo
    {
        /// <summary>
        /// The group key (e.g., artist name, year).
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Number of items in this group.
        /// </summary>
        public int ItemCount { get; }

        /// <summary>
        /// Optional header data for the group.
        /// </summary>
        public object? HeaderData { get; }

        /// <summary>
        /// Creates a new GroupInfo.
        /// </summary>
        public GroupInfo(string key, int itemCount, object? headerData = null)
        {
            Key = key;
            ItemCount = itemCount;
            HeaderData = headerData;
        }
    }
}
