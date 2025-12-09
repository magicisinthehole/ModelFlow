namespace ModelFlow.DataVirtualization.Pageing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Actions;
    using DataManagement;
    using Interfaces;

    /// <summary>
    /// Manages pagination for a single group within a grouped data source.
    /// Each group has its own pages, allowing independent virtualization per group.
    /// Follows the same architecture as <see cref="PaginationManager{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of items in the group.</typeparam>
    internal class GroupPaginationManager<T> where T : DataItem, IDataItem
    {
        private int _groupIndex;
        private readonly Func<ISourcePage<T>, int, int, int, Action?, Task<IEnumerable<T>>> _fetchItems;
        private readonly Func<int, int, int, int, T> _getPlaceholder;
        private readonly Dictionary<int, ISourcePage<T>> _pages = new Dictionary<int, ISourcePage<T>>();
        private readonly Dictionary<int, PageDelta> _deltas = new Dictionary<int, PageDelta>();
        private readonly Dictionary<int, CancellationTokenSource> _tasks = new Dictionary<int, CancellationTokenSource>();
        private readonly IPageReclaimer<T> _reclaimer;
        protected object PageLock = new object();
        private readonly AutoResetEvent _filterCaptureSignal = new AutoResetEvent(false);

        private int _basePage;
        private int _itemCount;
        private bool _hasGotCount;

        public int PageSize { get; }
        public int MaxPages { get; }
        public int MaxDeltas { get; }
        public int StepToJumpThreshold { get; set; } = 10;
        public IPageExpiryComparer? ExpiryComparer { get; set; }

        /// <summary>
        /// Creates a new GroupPaginationManager for a specific group.
        /// </summary>
        /// <param name="groupIndex">The index of this group.</param>
        /// <param name="itemCount">Known item count for this group.</param>
        /// <param name="fetchItems">Function to fetch items: (page, groupIndex, offset, count, signal) => items.
        /// The provider receives the page to materialize placeholders in place via SetItem().</param>
        /// <param name="getPlaceholder">Function to get placeholder: (groupIndex, itemOffset, page, pageOffset) => placeholder.</param>
        /// <param name="reclaimer">Page reclaimer for memory management. Defaults to PageReclaimOnTouched.</param>
        /// <param name="expiryComparer">Optional expiry comparer for page lifecycle.</param>
        /// <param name="pageSize">Size of each page.</param>
        /// <param name="maxPages">Maximum pages to keep in memory per group.</param>
        /// <param name="maxDeltas">Maximum number of delta adjustments to track before forcing reset.</param>
        public GroupPaginationManager(
            int groupIndex,
            int itemCount,
            Func<ISourcePage<T>, int, int, int, Action?, Task<IEnumerable<T>>> fetchItems,
            Func<int, int, int, int, T> getPlaceholder,
            IPageReclaimer<T>? reclaimer = null,
            IPageExpiryComparer? expiryComparer = null,
            int pageSize = 20,
            int maxPages = 10,
            int maxDeltas = -1)
        {
            _groupIndex = groupIndex;
            _itemCount = itemCount;
            _hasGotCount = true;
            _fetchItems = fetchItems;
            _getPlaceholder = getPlaceholder;
            PageSize = pageSize;
            MaxPages = maxPages;
            MaxDeltas = maxDeltas;
            _reclaimer = reclaimer ?? new PageReclaimOnTouched<T>();
            ExpiryComparer = expiryComparer;
        }

        /// <summary>
        /// Gets the item count for this group.
        /// </summary>
        public int Count => _itemCount;

        /// <summary>
        /// Updates the item count (e.g., after structure refresh).
        /// </summary>
        public void SetItemCount(int count)
        {
            _itemCount = count;
            _hasGotCount = true;
        }

        /// <summary>
        /// Updates the group index (e.g., after a group is inserted before this one).
        /// </summary>
        public void UpdateGroupIndex(int newIndex)
        {
            _groupIndex = newIndex;
        }

        /// <summary>
        /// Gets an item at the specified index within this group.
        /// Accounts for delta adjustments from insertions/deletions.
        /// </summary>
        public T GetAt(int index)
        {
            if (index < 0 || index >= _itemCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            CalculateFromIndex(index, out var page, out var offset);

            var dataPage = SafeGetPage(page, index);
            return dataPage?.GetAt(offset) ?? _getPlaceholder(_groupIndex, index, page, offset);
        }

        /// <summary>
        /// Gets whether all loaded pages are fully loaded (not just placeholders).
        /// </summary>
        public bool IsFullyLoaded
        {
            get
            {
                lock (PageLock)
                {
                    return _pages.Values.All(p => p.PageFetchState == PageFetchStateEnum.Fetched);
                }
            }
        }

        /// <summary>
        /// Resets all pages, dropping all loaded data and deltas.
        /// </summary>
        public void Reset()
        {
            lock (PageLock)
            {
                CancelAllRequests();
                DropAllDeltasAndPages();
                _hasGotCount = false;
            }
        }

        /// <summary>
        /// Runs page reclamation to free memory.
        /// </summary>
        public void RunClaim()
        {
            lock (PageLock)
            {
                var needed = Math.Max(0, _pages.Count - MaxPages);
                if (needed == 0) return;

                var reclaimedPages = _reclaimer.ReclaimPages(_pages.Values, needed, $"Group{_groupIndex}").ToList();
                foreach (var page in reclaimedPages)
                {
                    if (page.Page == _basePage) continue; // Don't reclaim base page
                    if (_pages.ContainsKey(page.Page))
                    {
                        _pages.Remove(page.Page);
                        _reclaimer.OnPageReleased(page);
                    }
                }
            }
        }

        #region Delta Management (Index Adjustment)

        /// <summary>
        /// Calculates the actual page and offset for a logical index,
        /// accounting for delta adjustments from insertions/deletions.
        /// Mirrors PaginationManager.CalculateFromIndex.
        /// </summary>
        protected void CalculateFromIndex(int index, out int page, out int innerOffset)
        {
            // First work out the base page from the index and the offset inside that page
            var basepage = page = (index / PageSize) + _basePage;
            innerOffset = (index + (_basePage * PageSize)) - (page * PageSize);

            // We only need to do the rest if there have been modifications to the page sizes (deltas)
            if (_deltas.Count <= 0) return;

            // Get the adjustment BEFORE checking for a short page
            int adjustment;
            lock (PageLock)
            {
                adjustment = _deltas.Values
                    .Where(d => d.Page < basepage)
                    .Sum(d => d.Delta);
            }

            // Check if we are currently in a short page
            if (_deltas.ContainsKey(page))
            {
                var delta = _deltas[page].Delta;
                if (delta < 0)
                {
                    // In a short page, are we over the edge?
                    if (innerOffset >= PageSize + delta)
                    {
                        var step = innerOffset - (PageSize + delta - 1);
                        innerOffset -= step;
                        DoStepForward(ref page, ref innerOffset, step);
                    }
                }
            }

            if (adjustment == 0) return;

            if (adjustment > 0)
            {
                // Items have been added to earlier pages, so we need to step back
                DoStepBackwards(ref page, ref innerOffset, adjustment);
            }
            else
            {
                // Items have been removed from earlier pages, so we need to step forward
                DoStepForward(ref page, ref innerOffset, Math.Abs(adjustment));
            }
        }

        /// <summary>
        /// Adds or updates a delta adjustment for a page.
        /// Returns the new delta value for the page.
        /// </summary>
        public int AddOrUpdateAdjustment(int page, int offsetChange)
        {
            var ret = 0;

            lock (PageLock)
            {
                if (!_deltas.ContainsKey(page))
                {
                    if (MaxDeltas == -1 || _deltas.Count < MaxDeltas)
                    {
                        ret = offsetChange;
                        _deltas.Add(page, new PageDelta { Page = page, Delta = offsetChange });
                    }
                    else
                    {
                        // Too many deltas, force a reset
                        DropAllDeltasAndPages();
                    }
                }
                else
                {
                    var adjustment = _deltas[page];
                    adjustment.Delta += offsetChange;

                    if (adjustment.Delta == 0)
                    {
                        _deltas.Remove(page);
                    }

                    ret = adjustment.Delta;
                }
            }

            return ret;
        }

        private void DoStepBackwards(ref int page, ref int offset, int stepAmount)
        {
            var done = false;
            var ignoreSteps = -1;

            while (!done)
            {
                // Jump optimization: skip multiple pages at once when step is large
                if (stepAmount > PageSize * StepToJumpThreshold && ignoreSteps <= 0)
                {
                    var targetPage = page - stepAmount / PageSize;
                    var sourcePage = page;
                    var adj = _deltas.Values
                        .Where(a => a.Page >= targetPage && a.Page <= sourcePage)
                        .OrderBy(a => a.Page)
                        .ToArray();

                    if (!adj.Any())
                    {
                        page = targetPage;
                        stepAmount -= (sourcePage - targetPage) * PageSize;
                        if (stepAmount == 0) done = true;
                    }
                    else if (adj.Last().Page < page - 2)
                    {
                        targetPage = adj.Last().Page + 1;
                        page = targetPage;
                        stepAmount -= (sourcePage - targetPage) * PageSize;
                        if (stepAmount == 0) done = true;
                    }
                    else
                    {
                        ignoreSteps = sourcePage - adj.Last().Page;
                    }
                }

                if (done) continue;

                if (offset - stepAmount < 0)
                {
                    stepAmount -= (offset + 1);
                    page--;
                    var items = PageSize;
                    if (_deltas.ContainsKey(page))
                    {
                        items += _deltas[page].Delta;
                    }
                    offset = items - 1;
                }
                else
                {
                    offset -= stepAmount;
                    done = true;
                }

                ignoreSteps--;
            }
        }

        private void DoStepForward(ref int page, ref int offset, int stepAmount)
        {
            var done = false;
            var ignoreSteps = -1;

            while (!done)
            {
                // Jump optimization: skip multiple pages at once when step is large
                if (stepAmount > PageSize * StepToJumpThreshold && ignoreSteps <= 0)
                {
                    var targetPage = page + stepAmount / PageSize;
                    var sourcePage = page;
                    var adj = _deltas.Values
                        .Where(a => a.Page <= targetPage && a.Page >= sourcePage)
                        .OrderBy(a => a.Page)
                        .ToArray();

                    if (!adj.Any())
                    {
                        page = targetPage;
                        stepAmount -= (targetPage - sourcePage) * PageSize;
                        if (stepAmount == 0) done = true;
                    }
                    else if (adj.Last().Page > page - 2)
                    {
                        targetPage = adj.Last().Page - 1;
                        page = targetPage;
                        stepAmount -= (targetPage - sourcePage) * PageSize;
                        if (stepAmount == 0) done = true;
                    }
                    else
                    {
                        ignoreSteps = adj.Last().Page - sourcePage;
                    }
                }

                if (done) continue;

                var items = PageSize;
                if (_deltas.ContainsKey(page))
                {
                    items += _deltas[page].Delta;
                }

                if (items <= offset + stepAmount)
                {
                    stepAmount -= items - offset;
                    offset = 0;
                    page++;
                }
                else
                {
                    offset += stepAmount;
                    done = true;
                }

                ignoreSteps--;
            }
        }

        private void DropAllDeltasAndPages()
        {
            _deltas.Clear();
            _pages.Clear();
            _basePage = 0;
            CancelAllRequests();
        }

        #endregion

        #region Real-Time Insertion Support

        /// <summary>
        /// Gets whether the count has been established for this group.
        /// </summary>
        public bool HasGotCount => _hasGotCount;

        /// <summary>
        /// Adjusts the item count without triggering a data fetch.
        /// Use when items are added/removed from the underlying source.
        /// </summary>
        /// <param name="delta">The change in count (+1 for insert, -1 for remove).</param>
        public void AdjustCount(int delta)
        {
            lock (PageLock)
            {
                if (!_hasGotCount) return;
                _itemCount = Math.Max(0, _itemCount + delta);
            }
        }

        /// <summary>
        /// Checks if the specified index falls within a currently loaded page.
        /// </summary>
        /// <param name="index">The zero-based index within this group.</param>
        /// <returns>True if the page containing this index is loaded.</returns>
        public bool IsIndexLoaded(int index)
        {
            lock (PageLock)
            {
                if (!_hasGotCount || index < 0 || index >= _itemCount)
                    return false;

                CalculateFromIndex(index, out var page, out _);
                return _pages.ContainsKey(page);
            }
        }

        /// <summary>
        /// Gets the loaded page numbers for debugging/diagnostics.
        /// </summary>
        public IReadOnlyList<int> GetLoadedPageNumbers()
        {
            lock (PageLock)
            {
                return _pages.Keys.ToList();
            }
        }

        /// <summary>
        /// Inserts an item at a specific index within a loaded page.
        /// Properly tracks deltas for accurate future index calculations.
        /// </summary>
        /// <param name="index">The index to insert at.</param>
        /// <param name="item">The item to insert.</param>
        public void InsertAt(int index, T item)
        {
            lock (PageLock)
            {
                CalculateFromIndex(index, out var page, out var offset);

                if (_pages.TryGetValue(page, out var dataPage))
                {
                    dataPage.InsertAt(offset, item, DateTime.Now, ExpiryComparer);

                    // Track the delta adjustment
                    var adj = AddOrUpdateAdjustment(page, 1);

                    // Handle short page growth
                    if (dataPage.ItemsPerPage < PageSize)
                    {
                        dataPage.ItemsPerPage++;
                    }

                    // Handle page overflow - create new base page if needed
                    if (page == _basePage && adj == PageSize * 2)
                    {
                        HandlePageOverflow(page, dataPage, index);
                    }

                    _itemCount++;
                }
            }
        }

        /// <summary>
        /// Appends an item to the end of this group.
        /// Properly tracks deltas if the last page is loaded.
        /// For empty groups, creates page 0 to store the item.
        /// </summary>
        /// <param name="item">The item to append.</param>
        public void Append(T item)
        {
            lock (PageLock)
            {
                var index = _itemCount;
                CalculateFromIndex(index, out var page, out _);

                if (_pages.TryGetValue(page, out var dataPage))
                {
                    var shortPage = dataPage.ItemsPerPage < PageSize;
                    dataPage.Append(item, DateTime.Now, ExpiryComparer);

                    if (shortPage)
                    {
                        dataPage.ItemsPerPage++;
                    }
                    else
                    {
                        AddOrUpdateAdjustment(page, 1);
                    }
                }
                else if (_itemCount == 0 && page == 0)
                {
                    // Empty group - create page 0 to store the first item
                    var newPage = _reclaimer.MakePage(0, 1);
                    _pages.Add(0, newPage);
                    newPage.Append(item, DateTime.Now, ExpiryComparer);
                    _hasGotCount = true;
                }

                _itemCount++;
            }
        }

        /// <summary>
        /// Removes an item at a specific index within a loaded page.
        /// Properly tracks deltas for accurate future index calculations.
        /// </summary>
        /// <param name="index">The index to remove at.</param>
        public void RemoveAt(int index)
        {
            lock (PageLock)
            {
                CalculateFromIndex(index, out var page, out var offset);

                if (_pages.TryGetValue(page, out var dataPage))
                {
                    dataPage.RemoveAt(offset, DateTime.Now, ExpiryComparer);

                    // Track the delta adjustment (negative for removal)
                    AddOrUpdateAdjustment(page, -1);

                    // Keep ItemsPerPage in sync with actual item count
                    if (dataPage.ItemsPerPage > 0)
                    {
                        dataPage.ItemsPerPage--;
                    }

                    _itemCount = Math.Max(0, _itemCount - 1);
                }
            }
        }

        private void HandlePageOverflow(int page, ISourcePage<T> dataPage, int index)
        {
            // When a page has doubled in size, split items to a new base page
            if (IsPageWired(page))
            {
                ISourcePage<T> newDataPage;
                if (IsPageWired(page - 1))
                {
                    newDataPage = SafeGetPage(page - 1, index);
                }
                else
                {
                    newDataPage = _reclaimer.MakePage(page - 1, PageSize);
                    _pages.Add(page - 1, newDataPage);
                }

                // Move items to new page
                for (var loop = 0; loop < PageSize; loop++)
                {
                    var item = dataPage.GetAt(0);
                    dataPage.RemoveAt(0, null, null);
                    newDataPage.Append(item, null, null);
                }
            }

            AddOrUpdateAdjustment(page, -PageSize);
            _basePage--;
        }

        private bool IsPageWired(int page)
        {
            lock (PageLock)
            {
                return _pages.ContainsKey(page);
            }
        }

        /// <summary>
        /// Pre-populates the first page (page 0) with already-fetched items.
        /// Used by batch prefetching to fill groups before they're accessed.
        /// If the first page is already loaded, this is a no-op.
        /// </summary>
        /// <param name="items">The items to populate the first page with.</param>
        public void PrepopulateFirstPage(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0) return;

            lock (PageLock)
            {
                // Don't prepopulate if page 0 already exists
                if (_pages.ContainsKey(0)) return;

                // Create page 0 with the prefetched items
                var pageSize = Math.Min(items.Count, PageSize);
                var newPage = _reclaimer.MakePage(0, pageSize);
                _pages.Add(0, newPage);

                // Fill with the prefetched items (not placeholders)
                for (var i = 0; i < pageSize; i++)
                {
                    newPage.Append(items[i], DateTime.Now, ExpiryComparer);
                }

                // Mark as fetched immediately (no async fetch needed)
                newPage.WiredDateTime = DateTime.Now;
                newPage.PageFetchState = PageFetchStateEnum.Fetched;
            }
        }

        #endregion

        #region Page Management

        private ISourcePage<T>? SafeGetPage(int pageNum, int itemIndex)
        {
            lock (PageLock)
            {
                if (_pages.TryGetValue(pageNum, out var existingPage))
                {
                    _reclaimer.OnPageTouched(existingPage);
                    return existingPage;
                }

                // Create new page with placeholders
                var pageOffset = CalculatePageOffset(pageNum);
                var pageSize = Math.Min(PageSize, _itemCount - pageOffset);
                if (pageSize <= 0) return null;

                // Account for delta adjustments
                if (_deltas.ContainsKey(pageNum))
                {
                    pageSize += _deltas[pageNum].Delta;
                }

                var newPage = _reclaimer.MakePage(pageNum, pageSize);
                _pages.Add(pageNum, newPage);

                // Fill with placeholders
                for (var i = 0; i < pageSize; i++)
                {
                    var placeholder = _getPlaceholder(_groupIndex, pageOffset + i, pageNum, i);
                    newPage.Append(placeholder, null, ExpiryComparer);
                }

                // Start async fetch with filter capture signal
                var cts = StartPageRequest(pageNum);
                _filterCaptureSignal.Reset();
                _ = Task.Run(async () => await FetchPageAsync(newPage, pageOffset, pageSize, () => _filterCaptureSignal.Set(), cts), cts.Token);
                _filterCaptureSignal.WaitOne(); // Wait for filter capture before returning

                return newPage;
            }
        }

        private int CalculatePageOffset(int pageNum)
        {
            // Calculate the offset in the underlying data for this page,
            // accounting for deltas on earlier pages
            var offset = (pageNum - _basePage) * PageSize;
            lock (PageLock)
            {
                offset += _deltas.Values
                    .Where(d => d.Page < pageNum)
                    .Sum(d => d.Delta);
            }
            return offset;
        }

        private async Task FetchPageAsync(ISourcePage<T> page, int offset, int count, Action signal, CancellationTokenSource cts)
        {
            if (cts.IsCancellationRequested) return;

            try
            {
                // Pass the page to the provider - the provider will materialize items in place
                // via SetItem() on the existing placeholders. The signal is invoked once the
                // filter is captured so we can return with placeholders immediately.
                await _fetchItems(page, _groupIndex, offset, count, signal);

                if (cts.IsCancellationRequested) return;

                // Record when the page was loaded (used for expiry comparison)
                page.WiredDateTime = DateTime.Now;

                // Mark page as fetched - the provider already materialized items via SetItem()
                await VirtualizationManager.Instance.RunOnUiAsync(new ActionVirtualizationWrapper(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    page.PageFetchState = PageFetchStateEnum.Fetched;
                }));
            }
            finally
            {
                RemovePageRequest(page.Page);
            }
        }

        private CancellationTokenSource StartPageRequest(int page)
        {
            var cts = new CancellationTokenSource();
            CancelPageRequest(page);

            lock (PageLock)
            {
                _tasks[page] = cts;
            }

            return cts;
        }

        private void CancelPageRequest(int page)
        {
            lock (PageLock)
            {
                if (_tasks.TryGetValue(page, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    _tasks.Remove(page);
                }
            }
        }

        private void RemovePageRequest(int page)
        {
            lock (PageLock)
            {
                _tasks.Remove(page);
            }
        }

        private void CancelAllRequests()
        {
            lock (PageLock)
            {
                foreach (var cts in _tasks.Values)
                {
                    try { cts.Cancel(); } catch { }
                }
                _tasks.Clear();
            }
        }

        #endregion
    }
}
