namespace DumpDiag.Impl
{
    internal readonly struct DumpDiagnoserProgress
    {
        internal double PercentCharacterArrays { get; }
        internal double PercentDelegateDetails { get; }
        internal double PercentDeterminingDelegates { get; }
        internal double PercentLoadHeap { get; }
        internal double PercentStartingTasks { get; }
        internal double PercentStrings { get; }
        internal double PercentThreadCount { get; }
        internal double PercentThreadDetails { get; }
        internal double PercentTypeDetails { get; }

        internal DumpDiagnoserProgress(double ca, double dd, double ddel, double lh, double st, double s, double tc, double td, double tyd)
        {
            PercentCharacterArrays = ca;
            PercentDelegateDetails = dd;
            PercentDeterminingDelegates = ddel;
            PercentLoadHeap = lh;
            PercentStartingTasks = st;
            PercentStrings = s;
            PercentThreadCount = tc;
            PercentThreadDetails = td;
            PercentTypeDetails = tyd;
        }

        internal DumpDiagnoserProgress WithCharacterArrays(double ca)
        => new DumpDiagnoserProgress(ca, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithDelegateDetails(double dd)
        => new DumpDiagnoserProgress(PercentCharacterArrays, dd, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithDeterminingDelegates(double ddel)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, ddel, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithLoadingHeap(double lh)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, lh, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithStartingTasks(double st)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, st, PercentStrings, PercentThreadCount, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithStrings(double s)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, s, PercentThreadCount, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithThreadCount(double tc)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, tc, PercentThreadDetails, PercentTypeDetails);

        internal DumpDiagnoserProgress WithThreadDetails(double td)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, td, PercentTypeDetails);

        internal DumpDiagnoserProgress WithTypeDetails(double tyd)
        => new DumpDiagnoserProgress(PercentCharacterArrays, PercentDelegateDetails, PercentDeterminingDelegates, PercentLoadHeap, PercentStartingTasks, PercentStrings, PercentThreadCount, PercentThreadDetails, tyd);
    }
}
