namespace ModelFlow.DataVirtualization.Pageing
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Actions;
    using DataManagement;
    using Interfaces;

    internal class PaginationManager<T> : IItemSourceProvider<T>, INotifyImmediately, IEditableProvider<T>,
        IEditableProviderIndexBased<T>, IEditableProviderItemBased<T>, IReclaimableService,
        IAsyncResetProvider, IProviderPreReset, INotifyCountChanged, INotifyCollectionChanged,
        ICollection where T : DataItem, IDataItem
    {
        private readonly Func<VirtualizingObservableCollection<T>> _getVoc;
        private readonly Dictionary<int, PageDelta> _deltas = new Dictionary<int, PageDelta>();
        private readonly Dictionary<int, ISourcePage<T>> _pages = new Dictionary<int, ISourcePage<T>>();
        private readonly IPageReclaimer<T> _reclaimer;

        private readonly Dictionary<int, CancellationTokenSource> _tasks =
            new Dictionary<int, CancellationTokenSource>();

        protected object PageLock = new object();
        private int _basePage;
        private bool _hasGotCount;

        private int _localCount;
        private int _pageSize = 100;
        
        public PaginationManager(
            IPagedSourceProviderAsync<T> provider,
            Func<VirtualizingObservableCollection<T>> getVoc,
            IPageReclaimer<T> reclaimer = null,
            IPageExpiryComparer expiryComparer = null,
            int pageSize = 100,
            int maxPages = 100,
            int maxDeltas = -1,
            int maxDistance = -1,
            string sectionContext = "")
        {
            _getVoc = getVoc;
            PageSize = pageSize;
            MaxPages = maxPages;
            MaxDeltas = maxDeltas;
            MaxDistance = maxDistance;

            ProviderAsync = provider;

            _reclaimer = reclaimer ?? new PageReclaimOnTouched<T>();

            ExpiryComparer = expiryComparer;

            VirtualizationManager.Instance.AddAction(new ReclaimPagesWA(this, sectionContext));
        }


        /// <summary>
        ///     Initializes a new instance of the <see cref="PaginationManager{T}" /> class.
        /// </summary>
        /// <param name="provider">The provider.</param>
        /// <param name="reclaimer">The reclaimer.</param>
        /// <param name="expiryComparer">The expiry comparer.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <param name="maxPages">The maximum pages.</param>
        /// <param name="maxDeltas">The maximum deltas.</param>
        /// <param name="maxDistance">The maximum distance.</param>
        /// <param name="sectionContext">The section context.</param>
        public PaginationManager(
            IPagedSourceProvider<T> provider,
            IPageReclaimer<T> reclaimer = null,
            IPageExpiryComparer expiryComparer = null,
            int pageSize = 100,
            int maxPages = 100,
            int maxDeltas = -1,
            int maxDistance = -1,
            string sectionContext = "")
        {
            PageSize = pageSize;
            MaxPages = maxPages;
            MaxDeltas = maxDeltas;
            MaxDistance = maxDistance;
            if (provider is IPagedSourceProviderAsync<T> async)
            {
                ProviderAsync = async;
            }
            else
            {
                Provider = provider;
            }

            _reclaimer = reclaimer ?? new PageReclaimOnTouched<T>();

            ExpiryComparer = expiryComparer;

            VirtualizationManager.Instance.AddAction(new ReclaimPagesWA(this, sectionContext));
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="PaginationManager{T}" /> class.
        /// </summary>
        /// <param name="provider">The provider.</param>
        /// <param name="reclaimer">The reclaimer.</param>
        /// <param name="expiryComparer">The expiry comparer.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <param name="maxPages">The maximum pages.</param>
        /// <param name="maxDeltas">The maximum deltas.</param>
        /// <param name="maxDistance">The maximum distance.</param>
        /// <param name="sectionContext">The section context.</param>
        public PaginationManager(
            IPagedSourceObservableProvider<T> provider,
            IPageReclaimer<T> reclaimer = null,
            IPageExpiryComparer expiryComparer = null,
            int pageSize = 100,
            int maxPages = 100,
            int maxDeltas = -1,
            int maxDistance = -1,
            string sectionContext = "") : this(provider as IPagedSourceProvider<T>, reclaimer, expiryComparer, pageSize,
            maxPages, maxDeltas, maxDistance, sectionContext)
        {
            provider.CollectionChanged += OnProviderCollectionChanged;
        }

        public IPageExpiryComparer ExpiryComparer { get; set; }

        /// <summary>
        ///     Gets or sets the maximum deltas.
        /// </summary>
        /// <value>
        ///     The maximum deltas.
        /// </value>
        public int MaxDeltas { get; set; } = -1;

        /// <summary>
        ///     Gets or sets the maximum distance.
        /// </summary>
        /// <value>
        ///     The maximum distance.
        /// </value>
        public int MaxDistance { get; set; } = -1;

        /// <summary>
        ///     Gets or sets the maximum pages.
        /// </summary>
        /// <value>
        ///     The maximum pages.
        /// </value>
        public int MaxPages { get; set; } = 100;

        /// <summary>
        ///     Gets or sets the size of the page.
        /// </summary>
        /// <value>
        ///     The size of the page.
        /// </value>
        public int PageSize
        {
            get => _pageSize;
            set
            {
                DropAllDeltasAndPages();
                _pageSize = value;
            }
        }

        /// <summary>
        ///     Gets or sets the provider.
        /// </summary>
        /// <value>
        ///     The provider.
        /// </value>
        public IPagedSourceProvider<T> Provider { get; set; }

        /// <summary>
        ///     Gets or sets the provider asynchronous.
        /// </summary>
        /// <value>
        ///     The provider asynchronous.
        /// </value>
        public IPagedSourceProviderAsync<T> ProviderAsync { get; set; }

        public int StepToJumpThreshold { get; set; } = 10;

        private int LocalCount
        {
            get => _localCount;
            set => _localCount = value;
        }

        public async Task<int> GetCountAsync()
        {
            _hasGotCount = true;
            if (!IsAsync)
            {
                return Provider.Count;
            }

            return await ProviderAsync.GetCountAsync();
        }


        /// <summary>
        ///     Resets the specified count.
        /// </summary>
        /// <param name="count">The count.</param>
        public void OnReset(int count)
        {
            CancelAllRequests();

            lock (PageLock)
            {
                DropAllDeltasAndPages();
            }

            if (count < 0)
            {
                _hasGotCount = false;
                LocalCount = 0;
            }
            else
            {
                //TODO <-lock (this.SyncRoot)
                lock (SyncRoot)
                {
                    LocalCount = count;
                    _hasGotCount = true;
                }
            }
#if DEBUG
            Serilog.Log.Debug($"[PM.OnReset] id={GetHashCode():x8} count={count} _localCount={_localCount}");
#endif

            if (!IsAsync)
            {
                Provider.OnReset(count);
            }
            else
            {
                ProviderAsync.OnReset(count);
            }

            if (count >= -1)
            {
                RaiseCountChanged(true, count);
            }
        }


        /// <summary>
        ///     Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        ///     An <see cref="T:System.Collections.IEnumerator" /> object that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator GetEnumerator()
        {
            throw new NotImplementedException();
        }

        public bool Contains(T item)
        {
            // Attempt to get the item from the pages, else call  the provider to get it..
            lock (PageLock)
            {
                foreach (var p in _pages)
                {
                    var o = p.Value.IndexOf(item);
                    if (o >= 0)
                    {
                        return true;
                    }
                }
            }

            return !IsAsync
                ? Provider.Contains(item)
                : ProviderAsync.ContainsAsync(item).GetAwaiter().GetResult();
        }

        public T GetAt(int index, object voc)
        {
            return GetAt(index, voc, 10);
        }

        /// <summary>
        ///     Gets the count.
        /// </summary>
        /// <value>
        ///     The count.
        /// </value>
        public int GetCount(bool asyncOk)
        {
            if (_hasGotCount) return LocalCount;

            //TODO<-lock(this.SyncRoot)
            lock (this)
            {
                if (!IsAsync)
                {
                    LocalCount = Provider.Count;
                    _hasGotCount = true;
                }
                else
                {
                    LocalCount = ProviderAsync.GetCountAsync().GetAwaiter().GetResult();
#if DEBUG
                    Serilog.Log.Debug($"[PM.GetCount] id={GetHashCode():x8} sync fetch: _localCount={_localCount}");
#endif
                    _hasGotCount = true;
                }
            }
            
            return LocalCount;
        }

        /// <summary>
        ///     Gets the Index of item.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>the index of the item, or -1 if not found</returns>
        public int IndexOf(T item)
        {
            // Attempt to get the item from the pages, else call  the provider to get it..
            lock (PageLock)
            {
                foreach (var p in _pages)
                {
                    var o = p.Value.IndexOf(item);
                    if (o >= 0)
                    {
                        return o + ((p.Key - _basePage) * PageSize) + (from d in _deltas.Values
                                   where d.Page < p.Key
                                   select d.Delta).Sum();
                    }
                }
            }

            if (!IsAsync)
            {
                return Provider.IndexOf(item);
            }
            else
            {
                var result = Task.Run(async () => await ProviderAsync.IndexOfAsync(item)).GetAwaiter().GetResult();

                return result;
            }
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        /// <summary>
        ///     Occurs when [count changed].
        /// </summary>
        public event OnCountChanged CountChanged;

        public bool IsNotifyImmediately
        {
            get => Provider is INotifyImmediately iNotifyImmediatelyProvider &&
                   iNotifyImmediatelyProvider.IsNotifyImmediately;
            set
            {
                if (Provider is INotifyImmediately iNotifyImmediatelyProvider)
                {
                    iNotifyImmediatelyProvider.IsNotifyImmediately = value;
                }
            }
        }


        public void OnBeforeReset()
        {
            if (!IsAsync)
            {
                (Provider as IProviderPreReset)?.OnBeforeReset();
            }
            else
            {
                (ProviderAsync as IProviderPreReset)?.OnBeforeReset();
            }
        }

        public void RunClaim(string sectionContext = "")
        {
            if (_reclaimer == null) return;
            lock (PageLock)
            {
                var needed = Math.Max(0, _pages.Count - MaxPages);
                if (needed == 0) return;
                var reclaimedPages = _reclaimer.ReclaimPages(_pages.Values, needed, sectionContext).ToList();

                foreach (var reclaimedPage in reclaimedPages)
                {
                    if (reclaimedPage.Page == _basePage) continue;
                    lock (_pages)
                    {
                        if (!_pages.ContainsKey(reclaimedPage.Page)) continue;
                        _pages.Remove(reclaimedPage.Page);
                        _reclaimer.OnPageReleased(reclaimedPage);
                    }
                }
            }
        }

        /// <summary>
        ///     Adds the or update adjustment.
        /// </summary>
        /// <param name="page">The page.</param>
        /// <param name="offsetChange">The offset change.</param>
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
                        _deltas.Add(page, new PageDelta {Page = page, Delta = offsetChange});
                    }
                    else
                    {
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

        /// <summary>
        ///     Gets at.
        /// </summary>
        /// <param name="index">The index.</param>
        /// <param name="voc">The voc.</param>
        /// <returns></returns>
        public T GetAt(int index, object voc, int nullTryCount = 10)
        {
            var ret = default(T);

            CalculateFromIndex(index, out var page, out var offset);

            var datapage = SafeGetPage(page, voc, index);

            if (datapage != null)
            {
                ret = datapage.GetAt(offset);
            }

            if (ret == null)
            {
                if (datapage != null && IsAsync && ProviderAsync != null)
                {
                    var placeholder = ProviderAsync.GetPlaceHolder(index, page, offset);
                    if (placeholder != null)
                    {
                        if (offset < datapage.ItemsCount)
                        {
                            datapage.ReplaceAt(offset, placeholder, null, null);
                        }
                        else
                        {
                            datapage.InsertAt(offset, placeholder, null, null);
                            // Keep ItemsPerPage in sync so DoRealPageGet requests enough items
                            if (datapage.ItemsPerPage < datapage.ItemsCount)
                                datapage.ItemsPerPage = datapage.ItemsCount;
                        }

                        datapage.PageFetchState = PageFetchStateEnum.Placeholders;
                        ret = placeholder;

                        if (voc != null)
                        {
                            lock (PageLock)
                            {
                                if (!_tasks.ContainsKey(page))
                                {
                                    var pageOffset = CalculatePageOffset(page);
                                    QueuePageFetch(datapage, voc, page, pageOffset, datapage.ItemsPerPage);
                                }
                            }
                        }
                    }
                }

                // Inconsistency detected - notify reset collection
                if (ret == null && nullTryCount <= 0)
                {
                    OnProviderCollectionChanged(Provider,
                        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                }
                return ret;
            }

            // Prefetch adjacent page when near the edge of the current page
            if (voc != null && IsAsync)
            {
                PrefetchAdjacentPages(page, offset, voc);
            }

            return ret;
        }

        /// <summary>
        /// Proactively fetches the next/previous page when the access offset is
        /// within the outer half of the current page, so data is ready before
        /// the user scrolls past the boundary.
        /// </summary>
        private void PrefetchAdjacentPages(int currentPage, int offset, object voc)
        {
            var threshold = Math.Max(1, PageSize / 2);
            var totalCount = GetCount(false);
            if (totalCount <= 0) return;
            CalculateFromIndex(totalCount - 1, out var maxPage, out _);

            // In the second half of this page → prefetch next
            if (offset >= PageSize - threshold)
            {
                var nextPage = currentPage + 1;
                if (nextPage <= maxPage)
                    EnsurePageLoading(nextPage, voc);
            }

            // In the first half of this page → prefetch previous
            if (offset < threshold)
            {
                var prevPage = currentPage - 1;
                if (prevPage >= _basePage)
                    EnsurePageLoading(prevPage, voc);
            }
        }

        /// <summary>
        /// Starts an async page fetch if the page is not already loaded or in-flight.
        /// </summary>
        private void EnsurePageLoading(int page, object voc)
        {
            lock (PageLock)
            {
                if (_tasks.ContainsKey(page))
                    return;

                if (_pages.TryGetValue(page, out var existingPage))
                {
                    if (voc != null && existingPage.PageFetchState == PageFetchStateEnum.Placeholders)
                    {
                        var existingPageOffset = CalculatePageOffset(page);
                        QueuePageFetch(existingPage, voc, page, existingPageOffset, existingPage.ItemsPerPage);
                    }

                    return;
                }

                var newPage = CreateNewPage(page, out var pageSize, out var pageOffset);
                if (pageSize <= 0) return;

                for (var i = 0; i < pageSize; i++)
                {
                    var placeHolder = ProviderAsync.GetPlaceHolder(pageOffset + i, newPage.Page, i);
                    newPage.Append(placeHolder, null, ExpiryComparer);
                }

                QueuePageFetch(newPage, voc, page, pageOffset, pageSize);
            }
        }

        private void QueuePageFetch(ISourcePage<T> page, object voc, int pageNumber, int pageOffset, int requestedCount)
        {
            var cts = StartPageRequest(pageNumber);
            Task.Run(async () =>
            {
                await DoRealPageGet(voc, page, pageOffset, requestedCount, cts);
            }, cts.Token).ConfigureAwait(false);
        }


        protected void CalculateFromIndex(int index, out int page, out int inneroffset)
        {
            // First work out the base page from the index and the offset inside that page
            var basepage = page = (index / PageSize) + _basePage;
            inneroffset = (index + (_basePage * PageSize)) - (page * PageSize);

            // We only need to do the rest if there have been modifications to the page sizes on pages (deltas)
            if (_deltas.Count <= 0) return;

            // Get the adjustment BEFORE checking for a short page, because we are going to adjust for that first..
            var adjustment = 0;

            lock (PageLock)
            {
                // First, get the total adjustments for any pages BEFORE the current page..
                adjustment = (from d in _deltas.Values
                    where d.Page < basepage
                    select d.Delta).Sum();
            }

            // Now check to see if we are currently in a short page - in which case we need to adjust for that
            if (_deltas.ContainsKey(page))
            {
                var delta = _deltas[page].Delta;

                if (delta < 0)
                {
                    // In a short page, are we over the edge ?
                    if (inneroffset >= PageSize + delta)
                    {
                        var step = inneroffset - (PageSize + delta - 1);
                        inneroffset -= step;
                        DoStepForward(ref page, ref inneroffset, step);
                    }
                }
            }

            // If we do have adjustments...
            if (adjustment == 0) return;

            if (adjustment > 0)
            {
                // items have been added to earlier pages, so we need to step back
                DoStepBackwards(ref page, ref inneroffset, adjustment);
            }
            else
            {
                // items have been removed from earlier pages, so we need to step forward
                DoStepForward(ref page, ref inneroffset, Math.Abs(adjustment));
            }
        }

        protected void CancelAllRequests()
        {
            lock (PageLock)
            {
                var cancellationTokenSources = _tasks.Values.ToList();
                foreach (var cancellationTokenSource in cancellationTokenSources)
                {
                    try
                    {
                        cancellationTokenSource.Cancel(false);
                    }
                    catch (Exception)
                    {
                        // Cancellation may throw if already cancelled - ignore
                    }
                }

                _tasks.Clear();
            }
        }


        protected void CancelPageRequest(int page)
        {
            lock (PageLock)
            {
                if (!_tasks.ContainsKey(page))
                {
                    return;
                }

                try
                {
                    _tasks[page].Cancel();
                }
                catch (Exception)
                {
                    // Cancellation may throw if already cancelled - ignore
                }

                try
                {
                    _tasks.Remove(page);
                }
                catch (Exception)
                {
                    // Removal may fail if not present - ignore
                }
            }
        }

        /// <summary>
        /// Cancels in-flight page requests for pages not adjacent to the target page.
        /// Also removes those pages from the page dictionary so they don't consume
        /// maxPages budget with stale placeholder data.
        /// Called when a new on-demand page is created during fast scrolling.
        /// </summary>
        private void CancelDistantRequests(int targetPage)
        {
            // Already inside PageLock from SafeGetPage
            // Snapshot keys to avoid modifying collection during iteration
            var buffer = new int[_tasks.Count];
            var count = 0;
            foreach (var p in _tasks.Keys)
            {
                if (p != int.MinValue && Math.Abs(p - targetPage) > 1)
                    buffer[count++] = p;
            }

            for (var i = 0; i < count; i++)
            {
                var page = buffer[i];
                var cts = _tasks[page];
                cts.Cancel();
                cts.Dispose();
                _tasks.Remove(page);

                if (_pages.TryGetValue(page, out var stalePageObj) && page != _basePage)
                {
                    _reclaimer.OnPageReleased(stalePageObj);
                    _pages.Remove(page);
                }
            }
        }

        /// Removes an item from the page state at the given global index.
        /// Caller must hold PageLock.
        private void RemoveFromPageState(int removedIndex)
        {
            CalculateFromIndex(removedIndex, out var page, out var offset);

            if (IsPageWired(page))
            {
                var dataPage = SafeGetPage(page, null, removedIndex);
                if (offset < dataPage.ItemsCount)
                {
                    dataPage.RemoveAt(offset, null, null);
                }

                if (dataPage.ItemsPerPage > 0)
                {
                    dataPage.ItemsPerPage--;
                }
            }

            // AddOrUpdateAdjustment re-enters PageLock (safe — Monitor is re-entrant)
            AddOrUpdateAdjustment(page, -1);

            if (page == _basePage)
            {
                var items = PageSize;
                if (_deltas.ContainsKey(page))
                {
                    items += _deltas[page].Delta;
                }

                if (items == 0)
                {
                    _deltas.Remove(page);
                    _basePage++;
                }
            }
        }

        /// <summary>
        ///     Drops all deltas and pages.
        /// </summary>
        protected void DropAllDeltasAndPages()
        {
            lock (PageLock)
            {
                _deltas.Clear();
                _pages.Clear();
                _basePage = 0;
                CancelAllRequests();
            }
        }


        /// <summary>
        ///     Gets the provider as editable.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="System.NotSupportedException"></exception>
        protected IEditableProvider<T> GetProviderAsEditable()
        {
            if (Provider != null)
            {
                return Provider as IEditableProvider<T>;
            }

            return ProviderAsync as IEditableProvider<T>;
        }


        /// <summary>
        ///     Raises the count changed.
        /// </summary>
        /// <param name="count">The count.</param>
        protected void RaiseCountChanged(bool needsReset, int count)
        {
            //TODO<-this._hasGotCount = false;
            var evnt = CountChanged;
            evnt?.Invoke(this, new CountChangedEventArgs
            {
                NeedsReset = needsReset,
                Count = count
            });
        }

        protected void RemovePageRequest(int page)
        {
            lock (PageLock)
            {
                if (!_tasks.ContainsKey(page)) return;
                try
                {
                    _tasks.Remove(page);
                }
                catch (Exception)
                {
                    // Removal may fail if not present - ignore
                }
            }
        }

        protected CancellationTokenSource StartPageRequest(int page)
        {
            var cts = new CancellationTokenSource();

            CancelPageRequest(page);

            lock (PageLock)
            {
                if (!_tasks.ContainsKey(page))
                {
                    _tasks.Add(page, cts);
                }
                else
                {
                    _tasks[page] = cts;
                }
            }

            return cts;
        }


        private void DoStepBackwards(ref int page, ref int offset, int stepAmount)
        {
            var done = false;
            var ignoreSteps = -1;
            //TODO <-lock (this.PageLock)
            //{
            while (!done)
            {
                if (stepAmount > PageSize * StepToJumpThreshold && ignoreSteps <= 0)
                {
                    var targetPage = page - stepAmount / PageSize;
                    var sourcePage = page;
                    var adj = (from a in _deltas.Values
                        where a.Page >= targetPage && a.Page <= sourcePage
                        orderby a.Page
                        select a).ToArray();
                    if (!adj.Any())
                    {
                        page = targetPage;
                        stepAmount -= (sourcePage - targetPage) * PageSize;

                        if (stepAmount == 0)
                        {
                            done = true;
                        }
                    }
                    else if (adj.Last().Page < page - 2)
                    {
                        targetPage = adj.Last().Page + 1;
                        page = targetPage;
                        stepAmount -= (sourcePage - targetPage) * PageSize;

                        if (stepAmount == 0)
                        {
                            done = true;
                        }
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

            // }
        }

        private void DoStepForward(ref int page, ref int offset, int stepAmount)
        {
            var done = false;
            var ignoreSteps = -1;
            //TODO <-lock (this.PageLock)
            //{
            while (!done)
            {
                if (stepAmount > PageSize * StepToJumpThreshold && ignoreSteps <= 0)
                {
                    var targetPage = page + stepAmount / PageSize;
                    var sourcePage = page;
                    var adj = (from a in _deltas.Values
                        where a.Page <= targetPage && a.Page >= sourcePage
                        orderby a.Page
                        select a).ToArray();
                    if (!adj.Any())
                    {
                        page = targetPage;
                        stepAmount -= (targetPage - sourcePage) * PageSize;

                        if (stepAmount == 0)
                        {
                            done = true;
                        }
                    }
                    else if (adj.Last().Page > page - 2)
                    {
                        targetPage = adj.Last().Page - 1;
                        page = targetPage;
                        stepAmount -= (targetPage - sourcePage) * PageSize;

                        if (stepAmount == 0)
                        {
                            done = true;
                        }
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
                    stepAmount -= (items) - offset;
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

            //}
        }

        /// <summary>
        ///     Fills the page.
        /// </summary>
        /// <param name="newPage">The new page.</param>
        /// <param name="pageOffset">The page offset.</param>
        private void FillPage(ISourcePage<T> newPage, int pageOffset)
        {;
            var data = new PagedSourceItemsPacket<T>(Provider.GetItemsAt(pageOffset, newPage.ItemsPerPage));
            newPage.WiredDateTime = data.LoadedAt;
            foreach (var o in data.Items)
            {
                newPage.Append(o, null, ExpiryComparer);
            }

            newPage.PageFetchState = PageFetchStateEnum.Fetched;
        }



        private void OnProviderCollectionChanged(object sender,
            NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs)
        {
            //lock(this._addLock)
            //{
            switch (notifyCollectionChangedEventArgs.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (var item in notifyCollectionChangedEventArgs.NewItems)
                    {
                        if (!(item is T newItem)) continue;

                        OnAppend(newItem, DateTime.Now, true, true);
                    }

                    CollectionChanged?.Invoke(sender,
                        notifyCollectionChangedEventArgs); // check if this.OnAppend does not raise collection change as well
                    //this.RaiseCountChanged(true, this._localCount);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    if (_hasGotCount && notifyCollectionChangedEventArgs.OldStartingIndex >= 0)
                    {
                        var removedIndex = notifyCollectionChangedEventArgs.OldStartingIndex;
                        lock (PageLock)
                        {
                            RemoveFromPageState(removedIndex);
                        }

                        Interlocked.Decrement(ref _localCount);
                    }
                    else
                    {
                        lock (PageLock)
                        {
                            _hasGotCount = false;
                            CancelAllRequests();
                            DropAllDeltasAndPages();
                        }
                    }

                    CollectionChanged?.Invoke(sender, notifyCollectionChangedEventArgs);
                    break;

                case NotifyCollectionChangedAction.Reset:
                    lock (PageLock)
                    {
                        _hasGotCount = false;
                        CancelAllRequests();
                        DropAllDeltasAndPages();
                    }

                    CollectionChanged?.Invoke(sender, notifyCollectionChangedEventArgs);
                    break;

                case NotifyCollectionChangedAction.Replace: //TODO
                case NotifyCollectionChangedAction.Move: //TODO
                default:
                    break;
            }

            //}
        }


        /// <summary>
        ///     Called when [append].
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <param name="isAlreadyInSourceCollection"></param>
        /// <param name="createPageIfNotExist"></param>
        /// <returns></returns>
        public int OnAppend(T item, object timestamp, bool isAlreadyInSourceCollection = false,
            bool createPageIfNotExist = false)
        {
            var index = LocalCount;

            if (!_hasGotCount)
            {
                lock (SyncRoot)
                {
                    EnsureCount();
                    if (isAlreadyInSourceCollection)
                    {
                        Interlocked.Decrement(ref _localCount);
                    }
                }
            }

            CalculateFromIndex(index, out var page, out _);

            if (IsPageWired(page))
            {
                var shortpage = false;
                var dataPage = SafeGetPage(page, null, index);
                if (dataPage.ItemsPerPage < PageSize)
                {
                    shortpage = true;
                }

                dataPage.Append(item, timestamp, ExpiryComparer);

                if (shortpage)
                {
                    dataPage.ItemsPerPage++;
                }
                else
                {
                    AddOrUpdateAdjustment(page, 1);
                }
            }
            else if (createPageIfNotExist)
            {
                var dataPage = CreateNewPage(page, out _, out _);
                dataPage.Append(item, timestamp, ExpiryComparer);
            }

            Interlocked.Increment(ref _localCount);
#if DEBUG
            Serilog.Log.Debug($"[PM.OnAppend] id={GetHashCode():x8} _localCount={_localCount}");
#endif

            var edit = GetProviderAsEditable();
            if (edit != null && !isAlreadyInSourceCollection)
            {
                //==>edit.OnInsert(index, item, timestamp);
                //TODO<-edit.OnAppend(item, timestamp);
                edit.OnAppend(item, timestamp);
            }
            else if (!isAlreadyInSourceCollection)
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index);
                CollectionChanged?.Invoke(this, args);
            }

            return index;
        }

        /// <summary>
        ///     Gets the page, if use placeholders is false - then gets page sync else async.
        /// </summary>
        /// <param name="page">The page.</param>
        /// <param name="voc">The voc.</param>
        /// <param name="index">The index that this page refers to (effectively the pageoffset.</param>
        /// <returns></returns>
        protected ISourcePage<T> SafeGetPage(int page, object voc, int index)
        {
            ISourcePage<T> ret = null;

            lock (PageLock)
            {
                if (_pages.ContainsKey(page))
                {
                    ret = _pages[page];
                    _reclaimer.OnPageTouched(ret);
                }
                else
                {
                    CancelDistantRequests(page);

                    var newPage = CreateNewPage(page, out var pageSize, out var pageOffset);

                    if (!IsAsync)
                    {
                        FillPage(newPage, pageOffset);

                        ret = newPage;
                    }
                    else
                    {
                        if (voc != null)
                        {
                            for (var loop = 0; loop < pageSize; loop++)
                            {
                                var placeHolder = ProviderAsync.GetPlaceHolder(pageOffset + loop, newPage.Page, loop);
                                newPage.Append(placeHolder, null, ExpiryComparer);
                            }

                            ret = newPage;
                            QueuePageFetch(newPage, voc, newPage.Page, pageOffset, newPage.ItemsPerPage);
                        }
                        else
                        {
                            ret = newPage;
                        }
                    }
                }
            }

            return ret;
        }

        private ISourcePage<T> CreateNewPage(int page, out int pageSize, out int pageOffset)
        {
            pageOffset = (page - _basePage) * PageSize + (from d in _deltas.Values
                             where d.Page < page
                             select d.Delta).Sum();
            pageSize = Math.Min(this.PageSize, this.GetCount(false) - pageOffset);
            if (_deltas.ContainsKey(page))
                pageSize += _deltas[page].Delta;
#if DEBUG
            Serilog.Log.Debug($"[PM.CreateNewPage] id={GetHashCode():x8} page={page} _localCount={_localCount} pageOffset={pageOffset} pageSize={pageSize}");
#endif

            var newPage = _reclaimer.MakePage(page, pageSize);
            _pages.Add(page, newPage);
            return newPage;
        }

        private int CalculatePageOffset(int page)
        {
            return (page - _basePage) * PageSize + (from d in _deltas.Values
                where d.Page < page
                select d.Delta).Sum();
        }

        private bool EnsureInsertCapacity(ISourcePage<T> page, int pageNumber, int pageOffset, int offset)
        {
            if (!IsAsync || ProviderAsync == null || offset < page.ItemsCount)
            {
                return false;
            }

            var requiredSize = offset + 1;
            if (page.ItemsPerPage < PageSize)
            {
                requiredSize = Math.Min(PageSize, Math.Max(requiredSize, page.ItemsPerPage + 1));
            }
            else
            {
                requiredSize = Math.Min(PageSize, requiredSize);
            }

            if (page.ItemsPerPage < requiredSize)
            {
                page.ItemsPerPage = requiredSize;
            }

            var addedPlaceholders = false;
            for (var i = page.ItemsCount; i < offset; i++)
            {
                var placeholder = ProviderAsync.GetPlaceHolder(pageOffset + i, pageNumber, i);
                page.Append(placeholder, null, ExpiryComparer);
                addedPlaceholders = true;
            }

            if (addedPlaceholders)
            {
                page.PageFetchState = PageFetchStateEnum.Placeholders;
            }

            return addedPlaceholders;
        }

        private async Task DoRealPageGet(object voc, ISourcePage<T> page, int pageOffset, int requestedCount,
            CancellationTokenSource cts)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var loadedAt = DateTime.Now;
                // Read current page size at execution time — concurrent inserts may have
                // grown the page since the fetch was queued. The items are already in the
                // DB (inserts commit before OnInsert), so the query returns all of them.
                var actualCount = page.ItemsPerPage;
                var items = (await ProviderAsync.GetItemsAtAsync(page, pageOffset, actualCount, null, cts.Token)).ToList();

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                lock (PageLock)
                {
                    if (!_pages.TryGetValue(page.Page, out var currentPage) || !ReferenceEquals(currentPage, page))
                    {
                        RemovePageRequest(page.Page);
                        return;
                    }

                    page.WiredDateTime = loadedAt;

                    if (page.ItemsCount > items.Count)
                    {
                        if (items.Count < actualCount)
                        {
                            // DB returned fewer items than requested — this is the
                            // true end of data. Trim excess placeholders.
                            while (page.ItemsCount > items.Count)
                                page.RemoveAt(page.ItemsCount - 1, null, null);
                            if (page.ItemsPerPage > items.Count)
                                page.ItemsPerPage = items.Count;
                            page.PageFetchState = PageFetchStateEnum.Fetched;
                            RemovePageRequest(page.Page);
                        }
                        else
                        {
                            // DB had enough data but page grew from concurrent
                            // inserts during fetch — re-fetch to cover them.
                            RemovePageRequest(page.Page);
                            var newPageOffset = CalculatePageOffset(page.Page);
                            QueuePageFetch(page, voc, page.Page, newPageOffset, page.ItemsPerPage);
                        }
                    }
                    else
                    {
                        page.PageFetchState = PageFetchStateEnum.Fetched;
                        RemovePageRequest(page.Page);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                RemovePageRequest(page.Page);
            }
            catch (Exception)
            {
                // Remove the failed page so the next access creates a fresh one and retries.
                lock (PageLock)
                {
                    if (_pages.TryGetValue(page.Page, out var currentPage) && ReferenceEquals(currentPage, page))
                    {
                        _pages.Remove(page.Page);
                        _reclaimer.OnPageReleased(page);
                    }
                }

                RemovePageRequest(page.Page);
            }
        }

        protected bool IsPageWired(int page)
        {
            var wired = false;

            lock (PageLock)
            {
                if (_pages.ContainsKey(page))
                {
                    wired = true;
                }
            }

            return wired;
        }

        public int OnAppend(T item, object timestamp) => OnAppend(item, timestamp, false, false);

        public void OnInsert(int index, T item, object timestamp)
        {
            if (!_hasGotCount)
                EnsureCount();

            CalculateFromIndex(index, out var page, out var offset);

            if (IsPageWired(page))
            {
                var dataPage = SafeGetPage(page, null, index);

                var pageOffset = CalculatePageOffset(page);

                if (IsAsync && ProviderAsync != null && dataPage.ItemsCount == 0 && dataPage.ItemsPerPage > 0)
                {
                    for (var i = 0; i < dataPage.ItemsPerPage; i++)
                    {
                        var placeholder = ProviderAsync.GetPlaceHolder(pageOffset + i, page, i);
                        dataPage.Append(placeholder, null, ExpiryComparer);
                    }
                    dataPage.PageFetchState = PageFetchStateEnum.Placeholders;
                }

                dataPage.InsertAt(offset, item, timestamp, ExpiryComparer);

                if (dataPage.ItemsPerPage < dataPage.ItemsCount)
                {
                    dataPage.ItemsPerPage = dataPage.ItemsCount;
                }

                if (dataPage.PageFetchState == PageFetchStateEnum.Placeholders && !_tasks.ContainsKey(page))
                {
                    QueuePageFetch(dataPage, _getVoc(), page, pageOffset, dataPage.ItemsPerPage);
                }

                var adj = AddOrUpdateAdjustment(page, 1);

                if (page == _basePage && adj == PageSize * 2)
                {
                    lock (PageLock)
                    {
                        if (IsPageWired(page))
                        {
                            ISourcePage<T> newdataPage = null;
                            if (IsPageWired(page - 1))
                            {
                                newdataPage = SafeGetPage(page - 1, null, index);
                            }
                            else
                            {
                                newdataPage = _reclaimer.MakePage(page - 1, PageSize);
                                _pages.Add(page - 1, newdataPage);
                            }

                            for (var loop = 0; loop < PageSize; loop++)
                            {
                                var i = dataPage.GetAt(0);

                                dataPage.RemoveAt(0, null, null);
                                newdataPage.Append(i, null, null);
                            }
                        }

                        AddOrUpdateAdjustment(page, -PageSize);

                        _basePage--;
                    }
                }
            }
            else
            {
                // Page not loaded — record the delta so CalculateFromIndex stays
                // correct when this page is eventually fetched. Don't create the
                // page or queue a fetch; the data lives in the provider.
                AddOrUpdateAdjustment(page, 1);
            }

            Interlocked.Increment(ref _localCount);

            var edit = GetProviderAsEditable();
            if (edit != null)
            {
                edit.OnInsert(index, item, timestamp);
            }
            else
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index);
                CollectionChanged?.Invoke(this, args);
            }

#if DEBUG
            Serilog.Log.Debug($"[PM.OnInsert] id={GetHashCode():x8} index={index} _localCount={_localCount} wired={IsPageWired(page)}");
#endif
        }

        public void OnReplace(int index, T oldItem, T newItem, object timestamp)
        {
            CalculateFromIndex(index, out var page, out var offset);

            if (IsPageWired(page))
            {
                var dataPage = SafeGetPage(page, null, index);
                dataPage.ReplaceAt(offset, newItem, timestamp, ExpiryComparer);
            }
            else
            {
                oldItem = new PagedSourceItemsPacket<T>(Provider.GetItemsAt(index, 1)).Items.FirstOrDefault();
            }

            if (Provider is IEditableProvider<T> editableProvider)
            {
                editableProvider.OnReplace(index, oldItem, newItem, timestamp);
            }
            else
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newItem, oldItem,
                    index);
                CollectionChanged?.Invoke(this, args);
            }
        }

        private void EnsureCount()
        {
            GetCount(false);
        }

        protected bool IsAsync => ProviderAsync != null ? true : false;

        /// <inheritdoc />
        /// <summary>
        ///     Copies the elements of the <see cref="T:System.Collections.ICollection" /> to an <see cref="T:System.Array" />,
        ///     starting at a particular <see cref="T:System.Array" /> index.
        /// </summary>
        /// <param name="array">
        ///     The one-dimensional <see cref="T:System.Array" /> that is the destination of the elements copied
        ///     from <see cref="T:System.Collections.ICollection" />. The <see cref="T:System.Array" /> must have zero-based
        ///     indexing.
        /// </param>
        /// <param name="index">The zero-based index in <paramref name="array" /> at which copying begins. </param>
        /// <exception cref="T:System.ArgumentNullException"><paramref name="array" /> is null. </exception>
        /// <exception cref="T:System.ArgumentOutOfRangeException"><paramref name="index" /> is less than zero. </exception>
        /// <exception cref="T:System.ArgumentException">
        ///     <paramref name="array" /> is multidimensional.-or- The number of elements
        ///     in the source <see cref="T:System.Collections.ICollection" /> is greater than the available space from
        ///     <paramref name="index" /> to the end of the destination <paramref name="array" />.-or-The type of the source
        ///     <see cref="T:System.Collections.ICollection" /> cannot be cast automatically to the type of the destination
        ///     <paramref name="array" />.
        /// </exception>
        public void CopyTo(Array array, int index)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        /// <summary>
        ///     Gets the number of elements contained in the <see cref="T:System.Collections.ICollection" />.
        /// </summary>
        /// <returns>
        ///     The number of elements contained in the <see cref="T:System.Collections.ICollection" />.
        /// </returns>
        public int Count => GetCount(false);

        /// <inheritdoc cref="" />
        /// <summary>
        ///     Gets an object that can be used to synchronize access to the <see cref="T:System.Collections.ICollection" />.
        /// </summary>
        /// <returns>
        ///     An object that can be used to synchronize access to the <see cref="T:System.Collections.ICollection" />.
        /// </returns>
        public object SyncRoot => Provider?.SyncRoot ?? ProviderAsync.SyncRoot;
        //public object SyncRoot => this.PageLock ?? this.ProviderAsync.SyncRoot;

        /// <summary>
        ///     Gets a value indicating whether access to the <see cref="T:System.Collections.ICollection" /> is synchronized
        ///     (thread safe).
        /// </summary>
        /// <returns>
        ///     true if access to the <see cref="T:System.Collections.ICollection" /> is synchronized (thread safe); otherwise,
        ///     false.
        /// </returns>
        public bool IsSynchronized => Provider.IsSynchronized;

        public T OnRemove(int index, object timestamp)
        {
            if (!_hasGotCount)
            {
                EnsureCount();
            }

            var item = GetAt(index, Provider);

            lock (PageLock)
            {
                RemoveFromPageState(index);
            }

            Interlocked.Decrement(ref _localCount);

            if (Provider is IEditableProviderIndexBased<T> editableProvider)
            {
                item = editableProvider.OnRemove(index, timestamp);
            }
            else
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index);
                CollectionChanged?.Invoke(this, args);
            }

            return item;
        }

        public T OnReplace(int index, T newItem, object timestamp)
        {
            T? oldItem;

            CalculateFromIndex(index, out var page, out var offset);

            if (IsPageWired(page))
            {
                var dataPage = SafeGetPage(page, null, index);
                oldItem = dataPage.ReplaceAt(offset, newItem, timestamp, ExpiryComparer);
            }
            else
            {
                oldItem = new PagedSourceItemsPacket<T>(Provider.GetItemsAt(index, 1)).Items.FirstOrDefault();
            }

            if (Provider is IEditableProviderIndexBased<T> editableProvider)
            {
                editableProvider.OnReplace(index, newItem, timestamp);
            }
            else
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newItem, oldItem,
                    index);
                CollectionChanged?.Invoke(this, args);
            }

            return oldItem;
        }

        public int OnRemove(T item, object timestamp)
        {
            if (!_hasGotCount)
            {
                EnsureCount();
            }
            int index = IndexOf(item);
            CalculateFromIndex(index, out var page, out var pageIndex);

            var dataPage = SafeGetPage(page, null, index);
            dataPage.RemoveAt(pageIndex, DateTime.Now, ExpiryComparer);

            // Keep ItemsPerPage in sync with actual item count
            if (dataPage.ItemsPerPage > 0)
            {
                dataPage.ItemsPerPage--;
            }

            AddOrUpdateAdjustment(page, -1);

            if (page == _basePage)
            {
                var items = PageSize;
                if (_deltas.ContainsKey(page))
                {
                    items += _deltas[page].Delta;
                }

                if (items == 0)
                {
                    _deltas.Remove(page);
                    _basePage++;
                }
            }

            Interlocked.Decrement(ref _localCount);

            if (Provider is IEditableProviderItemBased<T> editableProvider)
            {
                editableProvider.OnRemove(item, timestamp);
            }
            else
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index);
                CollectionChanged?.Invoke(this, args);
            }

            return pageIndex;
        }

        public int OnReplace(T oldItem, T newItem, object timestamp)
        {
            var index = Provider.IndexOf(oldItem);

            CalculateFromIndex(index, out var page, out var offset);

            if (IsPageWired(page))
            {
                var dataPage = SafeGetPage(page, null, index);
                dataPage.ReplaceAt(offset, newItem, timestamp, ExpiryComparer);
            }


            if (Provider is IEditableProviderItemBased<T> editableProvider)
            {
                editableProvider.OnReplace(oldItem, newItem, timestamp);
            }
            else
            {
                var args = new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newItem, oldItem,
                    index);
                CollectionChanged?.Invoke(this, args);
            }

            return index;
        }

        #region Memory-Efficient Real-Time Insertion Support

        /// <summary>
        /// Adjusts the local count without triggering a data fetch.
        /// Safe to call from any thread.
        /// </summary>
        /// <param name="delta">The change in count (+1 for insert, -1 for remove).</param>
        /// <remarks>
        /// If count hasn't been fetched yet, this is a no-op.
        /// The item will be included when count is fetched from the source.
        /// </remarks>
        public void AdjustCount(int delta)
        {
            int newCount;
            bool wasEmpty;
            lock (SyncRoot)
            {
                if (!_hasGotCount)
                {
                    return;
                }

                wasEmpty = _localCount == 0;
                newCount = Math.Max(0, Interlocked.Add(ref _localCount, delta));
                _localCount = newCount;
            }

            TruncatePagesForCount(newCount);

            // Notify outside lock to prevent deadlocks
            RaiseCountChanged(needsReset: wasEmpty && newCount > 0, newCount);
        }

        /// <summary>
        /// Sets the count to an authoritative value without expanding or refetching pages.
        /// Pages beyond the new count are removed; pages within it are left as-is.
        /// Use this instead of AdjustCount when the caller has the true count from the DB.
        /// </summary>
        public void SetKnownCount(int count)
        {
            int newCount;
            bool wasEmpty;
            lock (SyncRoot)
            {
                if (!_hasGotCount)
                {
                    return;
                }

                wasEmpty = _localCount == 0;
                newCount = Math.Max(0, count);
#if DEBUG
                Serilog.Log.Debug($"[PM.SetKnownCount] id={GetHashCode():x8} {_localCount} -> {newCount}");
#endif
                _localCount = newCount;
            }

            // Authoritative count supersedes accumulated deltas — clear them
            // so TruncatePagesForCount doesn't double-count local modifications.
            lock (PageLock)
            {
                _deltas.Clear();
            }

            TruncatePagesForCount(newCount);

            // When transitioning from empty to populated, raise Reset so the UI
            // framework knows to start requesting items. No pages exist to preserve.
            RaiseCountChanged(needsReset: wasEmpty && newCount > 0, newCount);
        }

        private void TruncatePagesForCount(int newCount)
        {
            if (!IsAsync || ProviderAsync == null)
            {
                return;
            }

            lock (PageLock)
            {
                if (_pages.Count == 0)
                {
                    return;
                }

                var pagesToRemove = new List<int>();
                foreach (var pageNumber in _pages.Keys.OrderBy(k => k).ToList())
                {
                    var page = _pages[pageNumber];
                    var pageOffset = CalculatePageOffset(pageNumber);
                    var expectedSize = GetExpectedPageSize(pageNumber, newCount, pageOffset);

                    if (expectedSize <= 0)
                    {
                        pagesToRemove.Add(pageNumber);
                        continue;
                    }

                    if (page.ItemsPerPage > expectedSize)
                    {
                        ShrinkPage(page, expectedSize);
                    }
                }

                foreach (var pageNumber in pagesToRemove)
                {
                    CancelPageRequest(pageNumber);
                    if (_pages.TryGetValue(pageNumber, out var page))
                    {
                        _pages.Remove(pageNumber);
                        _reclaimer.OnPageReleased(page);
                    }
                }
            }
        }

        private int GetExpectedPageSize(int pageNumber, int totalCount, int pageOffset)
        {
            if (pageOffset >= totalCount)
            {
                return 0;
            }

            var pageSize = Math.Min(PageSize, totalCount - pageOffset);
            if (_deltas.TryGetValue(pageNumber, out var delta))
            {
                pageSize += delta.Delta;
            }

            return Math.Max(0, pageSize);
        }


        private void ShrinkPage(ISourcePage<T> page, int expectedSize)
        {
            while (page.ItemsCount > expectedSize)
            {
                page.RemoveAt(page.ItemsCount - 1, null, ExpiryComparer);
            }

            page.ItemsPerPage = expectedSize;
            if (page.PageFetchState != PageFetchStateEnum.Placeholders)
            {
                page.PageFetchState = PageFetchStateEnum.Fetched;
            }
        }

        /// <summary>
        /// Checks if the specified index falls within a currently loaded page.
        /// </summary>
        /// <param name="index">The zero-based index to check.</param>
        /// <returns>True if the page containing this index is loaded in memory.</returns>
        public bool IsIndexLoaded(int index)
        {
            lock (PageLock)
            {
                if (!_hasGotCount || index < 0 || index >= _localCount)
                {
                    return false;
                }

                CalculateFromIndex(index, out var page, out _);
                return _pages.TryGetValue(page, out var sourcePage) &&
                       sourcePage.PageFetchState == PageFetchStateEnum.Fetched;
            }
        }

        public bool HasIndexInMemory(int index)
        {
            lock (PageLock)
            {
                if (!_hasGotCount || index < 0 || index >= _localCount)
                {
                    return false;
                }

                CalculateFromIndex(index, out var page, out _);
                return _pages.ContainsKey(page);
            }
        }

        public bool TryGetInMemoryAt(int index, out T item)
        {
            lock (PageLock)
            {
                item = default;

                if (!_hasGotCount || index < 0 || index >= _localCount)
                {
                    return false;
                }

                CalculateFromIndex(index, out var page, out var offset);
                if (!_pages.TryGetValue(page, out var sourcePage))
                {
                    return false;
                }

                if (offset < 0 || offset >= sourcePage.ItemsCount)
                {
                    return false;
                }

                item = sourcePage.PeekAt(offset);
                return true;
            }
        }

        /// <summary>
        /// Gets the page number for a given index.
        /// </summary>
        /// <param name="index">The index to convert.</param>
        /// <returns>The page number, or -1 if index is out of range.</returns>
        public int GetPageForIndex(int index)
        {
            lock (PageLock)
            {
                if (!_hasGotCount || index < 0 || index >= _localCount)
                {
                    return -1;
                }

                CalculateFromIndex(index, out var page, out _);
                return page;
            }
        }

        /// <summary>
        /// Gets information about which pages are currently loaded.
        /// Useful for debugging and understanding memory usage.
        /// </summary>
        public IReadOnlyList<int> GetLoadedPageNumbers()
        {
            lock (PageLock)
            {
                return _pages.Keys.ToList();
            }
        }

        /// <summary>
        /// Gets whether the count has been fetched from the source.
        /// </summary>
        public bool HasGotCount => _hasGotCount;

        #endregion
    }
}
