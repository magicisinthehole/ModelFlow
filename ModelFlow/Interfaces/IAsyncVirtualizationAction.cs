namespace ModelFlow.DataVirtualization.Interfaces
{
    using System;
    using System.Threading.Tasks;
    using Actions;

    internal interface IAsyncVirtualizationAction : IVirtualizationAction
    {
        Func<Task> DoActionAsync { get; }
    }
}
