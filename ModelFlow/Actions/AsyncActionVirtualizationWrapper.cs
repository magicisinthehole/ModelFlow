namespace ModelFlow.DataVirtualization.Actions
{
    using System;
    using System.Threading.Tasks;
    using Interfaces;

    /// <summary>
    /// Preserves the Task from async lambdas so callers can await completion,
    /// unlike ActionVirtualizationWrapper whose Action signature makes async lambdas fire-and-forget.
    /// </summary>
    internal class AsyncActionVirtualizationWrapper : IAsyncVirtualizationAction
    {
        private readonly Func<Task> _asyncAction;

        public AsyncActionVirtualizationWrapper(Func<Task> asyncAction)
        {
            _asyncAction = asyncAction ?? throw new ArgumentNullException(nameof(asyncAction));
        }

        public VirtualActionThreadModelEnum ThreadModel => VirtualActionThreadModelEnum.UseUIThread;

        public Func<Task> DoActionAsync => _asyncAction;

        public void DoAction()
        {
            throw new InvalidOperationException(
                "AsyncActionVirtualizationWrapper must be used via RunOnUiAsync, not AddAction. " +
                "Calling DoAction() synchronously would deadlock on a single-threaded UI dispatcher.");
        }
    }
}
