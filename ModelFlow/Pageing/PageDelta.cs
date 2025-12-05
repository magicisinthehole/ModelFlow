namespace ModelFlow.DataVirtualization.Pageing
{
    /// <summary>
    /// Tracks the delta (number of items added/removed) for a specific page.
    /// Negative page numbers are valid - they represent pages created when
    /// prepending items before the original page 0.
    /// </summary>
    internal class PageDelta
    {
        public int Delta { get; set; }

        public int Page { get; set; }
    }
}