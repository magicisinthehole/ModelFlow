namespace ModelFlow.DataVirtualization.Interfaces
{
    using System;

    internal interface IRepeatingVirtualizationAction
    {
        bool IsDueToRun();
        bool KeepInActionsList();
        TimeSpan GetTimeUntilDue();
    }
}