namespace DumpDiag.Impl
{
    internal readonly struct DumpDiagnoserProgress
    {
        internal ulong TotalCommandsExecuted { get; }

        internal double PercentCharacterArrays { get; }
        internal double PercentDelegateDetails { get; }
        internal double PercentDeterminingDelegates { get; }
        internal double PercentLoadHeap { get; }
        internal double PercentStartingTasks { get; }
        internal double PercentStrings { get; }
        internal double PercentThreadCount { get; }
        internal double PercentThreadDetails { get; }
        internal double PercentTypeDetails { get; }
        internal double PercentAsyncDetails { get; }
        internal double PercentAnalyzingPins { get; }
        internal double PercentHeapAssignments { get; }

        internal DumpDiagnoserProgress(ulong commands, double ca, double dd, double ddel, double lh, double st, double s, double tc, double td, double tyd, double ad, double ap, double ha)
        {
            TotalCommandsExecuted = commands;
            PercentCharacterArrays = ca;
            PercentDelegateDetails = dd;
            PercentDeterminingDelegates = ddel;
            PercentLoadHeap = lh;
            PercentStartingTasks = st;
            PercentStrings = s;
            PercentThreadCount = tc;
            PercentThreadDetails = td;
            PercentTypeDetails = tyd;
            PercentAsyncDetails = ad;
            PercentAnalyzingPins = ap;
            PercentHeapAssignments = ha;
        }

        internal DumpDiagnoserProgress WithTotalExecutedCommands(ulong commands)
        => new DumpDiagnoserProgress(commands, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithCharacterArrays(double ca)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, ca, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithDelegateDetails(double dd)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, dd, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithDeterminingDelegates(double ddel)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, ddel, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithLoadingHeap(double lh)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, lh, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithStartingTasks(double st)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, st, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithStrings(double s)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, s, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithThreadCount(double tc)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, tc, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithThreadDetails(double td)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, td, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithTypeDetails(double tyd)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, tyd, PercentAsyncDetails, PercentAnalyzingPins, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithAsyncDetails(double ad)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, ad, PercentAnalyzingPins, PercentHeapAssignments);
    
        internal DumpDiagnoserProgress WithAnalysingPins(double ap)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, ap, PercentHeapAssignments);

        internal DumpDiagnoserProgress WithHeapAssignments(double ha)
        => new DumpDiagnoserProgress(TotalCommandsExecuted, PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails, PercentAsyncDetails, PercentAnalyzingPins, ha);
    }
}
