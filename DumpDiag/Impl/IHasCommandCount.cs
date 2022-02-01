namespace DumpDiag.Impl
{
    internal interface IHasCommandCount
    {
        ulong TotalExecutedCommands { get; }
    }
}
