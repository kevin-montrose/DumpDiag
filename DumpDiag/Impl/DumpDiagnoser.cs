using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading;

namespace DumpDiag.Impl
{
    internal enum ProgressKind : byte
    {
        StartingTasks,
        LoadingHeap,
        TypeDetails,
        ThreadCount,
        ThreadDetails,
        CharacterArrays,
        DeterminingDelegates,
        DelegateDetails,
        Strings,
        AsyncDetails,
        AnalyzingPins,
        HeapAssignments
    }

    internal sealed class DumpDiagnoser<TAnalyzer> : IAsyncDisposable
        where TAnalyzer: class, IAnalyzer
    {
        private readonly int numProcs;
        private readonly LeaseTracker<TAnalyzer> procs;
        private readonly IProgress<DumpDiagnoserProgress>? progress;
        private readonly int[] progressMax;
        private readonly int[] progressCurrent;
        private readonly Timer progressTimer;
        private long lastProgressCallback;

        private int threadCount;
        internal ImmutableDictionary<TypeDetails, ImmutableHashSet<long>>? typeDetails;
        private ImmutableDictionary<long, TypeDetails>? methodTableToTypeDetails;
        private TypeDetails stringTypeDetails;
        private TypeDetails charArrayTypeDetails;

        private StringDetails stringDetails;
        private ImmutableList<HeapEntry>? liveHeapEntries;
        private ImmutableList<HeapEntry>? deadHeapEntries;

        private ImmutableArray<ImmutableList<HeapEntry>> liveHeapEntriesByProc;
        private ImmutableArray<ImmutableList<HeapEntry>> deadHeapEntriesByProc;
        private ImmutableList<AsyncStateMachineDetails>? asyncDetails;

        private ImmutableList<HeapDetails>? heapClassifications;
        private HeapFragmentation? heapFragmentation;

        internal DumpDiagnoser(int numProcs, IProgress<DumpDiagnoserProgress>? progress)
        {
            this.numProcs = numProcs;
            procs = new LeaseTracker<TAnalyzer>();
            this.progress = progress;

            progressMax = new int[Enum.GetValues(typeof(ProgressKind)).Length];
            progressCurrent = new int[progressMax.Length];

            progressTimer = new Timer(RegularProgressCallback, null, Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(1));
        }

        private void RegularProgressCallback(object? _)
        {
            // try and make sure we always report progress at least once a minute
            var lastProgress = Volatile.Read(ref lastProgressCallback);
            var delta = Stopwatch.GetTimestamp() - lastProgress;

            if (delta >= 60 * Stopwatch.Frequency)
            {
                ReportProgress();
            }
        }

        private void SetProgressLimit(ProgressKind kind, int limit)
        {
            Debug.Assert(progressMax[(int)kind] == 0);

            lock (progressMax)
            {
                progressMax[(int)kind] = limit;
            }
        }

        internal void MadeProgress(ProgressKind kind, int by)
        {
            Debug.Assert(progressMax[(int)kind] > 0);
            Debug.Assert(by > 0);

            lock (progressCurrent)
            {
                progressCurrent[(int)kind] += by;
            }

            Debug.Assert(progressCurrent[(int)kind] <= progressMax[(int)kind]);

            ReportProgress();
        }

        private void ReportProgress()
        {
            var prog = default(DumpDiagnoserProgress);

            lock (progressMax)
            {
                lock (progressCurrent)
                {
                    Volatile.Write(ref lastProgressCallback, Stopwatch.GetTimestamp());

                    prog = prog.WithTotalExecutedCommands(procs.TotalExecutedCommands);

                    prog = prog.WithCharacterArrays(CalcPercent(progressCurrent, progressMax, ProgressKind.CharacterArrays));
                    prog = prog.WithDelegateDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.DelegateDetails));
                    prog = prog.WithDeterminingDelegates(CalcPercent(progressCurrent, progressMax, ProgressKind.DeterminingDelegates));
                    prog = prog.WithLoadingHeap(CalcPercent(progressCurrent, progressMax, ProgressKind.LoadingHeap));
                    prog = prog.WithStartingTasks(CalcPercent(progressCurrent, progressMax, ProgressKind.StartingTasks));
                    prog = prog.WithStrings(CalcPercent(progressCurrent, progressMax, ProgressKind.Strings));
                    prog = prog.WithThreadCount(CalcPercent(progressCurrent, progressMax, ProgressKind.ThreadCount));
                    prog = prog.WithThreadDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.ThreadDetails));
                    prog = prog.WithTypeDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.TypeDetails));
                    prog = prog.WithAsyncDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.AsyncDetails));
                    prog = prog.WithAnalysingPins(CalcPercent(progressCurrent, progressMax, ProgressKind.AnalyzingPins));
                    prog = prog.WithHeapAssignments(CalcPercent(progressCurrent, progressMax, ProgressKind.HeapAssignments));

                    progress?.Report(prog);
                }
            }

            static double CalcPercent(int[] num, int[] denom, ProgressKind e)
            {
                var i = (int)e;

                double n = num[i];
                double d = denom[i];

                if (d == 0)
                {
                    return 0.0;
                }

                return Math.Round(n / d * 100.0, 1);
            }
        }

        internal void SplitHeapByProcs()
        {
            Debug.Assert(liveHeapEntriesByProc == null);
            Debug.Assert(deadHeapEntriesByProc == null);

            if (liveHeapEntries == null || deadHeapEntries == null)
            {
                throw new Exception("Heap entries not loaded, this shouldn't be possible");
            }

            liveHeapEntriesByProc =
                liveHeapEntries
                    .Select((he, ix) => (he, ix))
                    .GroupBy(t => t.ix % numProcs)
                    .Select(g => g.Select(x => x.he).ToImmutableList())
                    .ToImmutableArray();

            // practically this will never happen, but for correctness sake in the extreme edges...
            while (liveHeapEntriesByProc.Length < numProcs)
            {
                liveHeapEntriesByProc = liveHeapEntriesByProc.Add(ImmutableList<HeapEntry>.Empty);
            }

            deadHeapEntriesByProc =
                deadHeapEntries
                    .Select((he, ix) => (he, ix))
                    .GroupBy(t => t.ix % numProcs)
                    .Select(g => g.Select(x => x.he).ToImmutableList())
                    .ToImmutableArray();

            // practically this will never happen, but for correctness sake in the extreme edges...
            while (deadHeapEntriesByProc.Length < numProcs)
            {
                deadHeapEntriesByProc = liveHeapEntriesByProc.Add(ImmutableList<HeapEntry>.Empty);
            }
        }

        internal async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadStringCountsAsync()
        {
            if (liveHeapEntries == null || deadHeapEntries == null)
            {
                throw new Exception("Heap entries not loaded, this shouldn't be possible");
            }

            SetProgressLimit(ProgressKind.Strings, liveHeapEntries.Count + deadHeapEntries.Count);

            // load string counts in parallel
            var stringCountTasks = new ValueTask<ImmutableDictionary<string, ReferenceStats>>[numProcs];
            for (var i = 0; i < stringCountTasks.Length; i++)
            {
                var live = liveHeapEntriesByProc[i];
                var dead = deadHeapEntriesByProc[i];
                var partialTask =
                    procs.RunWithLeasedAsync(
                        proc => LoadStringCountsInnerAsync(this, proc, stringDetails, live, dead)
                    );
                stringCountTasks[i] = partialTask;
            }
            var res = await stringCountTasks.WhenAll().ConfigureAwait(false);

            var stringCounts = MergeReferenceStats(res);

            return stringCounts;
        }

        private static ImmutableDictionary<string, ReferenceStats> MergeReferenceStats(ImmutableArray<ImmutableDictionary<string, ReferenceStats>> res)
        {
            var stringCounts =
                res
                    .SelectMany(x => x)
                    .GroupBy(x => x.Key)
                    .ToImmutableDictionary(
                        static g => g.Key,
                        static g =>
                            g.Select(static x => x.Value).Aggregate(
                                new ReferenceStats(0, 0, 0, 0),
                                static (cur, next) => new ReferenceStats(cur.Live + next.Live, cur.LiveBytes + next.LiveBytes, cur.Dead + next.Dead, cur.DeadBytes + next.DeadBytes)
                            )
                    );

            return stringCounts;
        }

        private static async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadStringCountsInnerAsync(
            DumpDiagnoser<TAnalyzer> self,
            TAnalyzer proc,
            StringDetails stringDetails,
            IEnumerable<HeapEntry> liveHeap,
            IEnumerable<HeapEntry> deadHeap
        )
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ReferenceStats>();

            // queue up all the tasks for reading live heap strings...
            var pendingHandled = 0;
            foreach (var entry in liveHeap)
            {
                pendingHandled++;
                if (pendingHandled == 1024)
                {
                    self.MadeProgress(ProgressKind.Strings, pendingHandled);
                    pendingHandled = 0;
                }

                if (stringDetails.MethodTable != entry.MethodTable)
                {
                    continue;
                }

                // try and read this string whole thing in one call...
                string str;

                var peaked = await proc.PeakStringAsync(stringDetails, entry).ConfigureAwait(false);
                if(peaked.ReadFullString)
                {
                    str = peaked.PeakedString;
                }
                else
                {
                    // ... but if we don't get it all, make a second call for the rest
                    var startAddr = entry.Address + stringDetails.FirstCharOffset + peaked.PeakedString.Length * sizeof(char);
                    var remainingLen = peaked.ActualLength - peaked.PeakedString.Length;
                    var rest = await proc.LoadCharsAsync(startAddr, remainingLen).ConfigureAwait(false);

                    str = peaked.PeakedString + rest;
                }
                
                if (!builder.TryGetValue(str, out var curStats))
                {
                    curStats = new ReferenceStats(0, 0, 0, 0);
                }

                var updatedStats = new ReferenceStats(curStats.Live + 1, curStats.LiveBytes + entry.SizeBytes, curStats.Dead, curStats.DeadBytes);
                builder[str] = updatedStats;
            }

            // queue up all the tasks for reading dead heap strings...
            foreach (var entry in deadHeap)
            {
                pendingHandled++;
                if (pendingHandled == 1024)
                {
                    self.MadeProgress(ProgressKind.Strings, pendingHandled);
                    pendingHandled = 0;
                }

                if (stringDetails.MethodTable != entry.MethodTable)
                {
                    continue;
                }

                // try and read this string whole thing in one call...
                string str;

                var peaked = await proc.PeakStringAsync(stringDetails, entry).ConfigureAwait(false);
                if (peaked.ReadFullString)
                {
                    str = peaked.PeakedString;
                }
                else
                {
                    // ... but if we don't get it all, make a second call for the rest
                    var startAddr = entry.Address + stringDetails.FirstCharOffset + peaked.PeakedString.Length * sizeof(char);
                    var remainingLen = peaked.ActualLength - peaked.PeakedString.Length;
                    var rest = await proc.LoadCharsAsync(startAddr, remainingLen).ConfigureAwait(false);

                    str = peaked.PeakedString + rest;
                }

                if (!builder.TryGetValue(str, out var curStats))
                {
                    curStats = new ReferenceStats(0, 0, 0, 0);
                }

                var updatedStats = new ReferenceStats(curStats.Live, curStats.LiveBytes, curStats.Dead + 1, curStats.DeadBytes + entry.SizeBytes);
                builder[str] = updatedStats;
            }

            if (pendingHandled != 0)
            {
                self.MadeProgress(ProgressKind.Strings, pendingHandled);
            }

            return builder.ToImmutable();
        }

        private static ImmutableList<(string TypeName, ImmutableHashSet<long> MethodTables)> GetCandidateDelegateTypes(ImmutableDictionary<TypeDetails, ImmutableHashSet<long>> typeDetails)
        {
            var candidateDelegateTypes =
               typeDetails
                   .Where(kv => IsProbablyDelegate(kv.Key.TypeName))
                   .Select(kv => (TypeName: kv.Key.TypeName, MethodTables: kv.Value))
                   .ToImmutableList();

            return candidateDelegateTypes;

            // guesses based on the type name, just to bring the number of candidates down to something reasonable
            static bool IsProbablyDelegate(string typeName)
            {
                if (IsArray(typeName))
                {
                    return false;
                }

                if (typeName.StartsWith("System.Action") || typeName.StartsWith("System.Func"))
                {
                    return true;
                }

                // todo: all built-in delegates?

                var endOfType = typeName.IndexOf('`');
                if (endOfType == -1)
                {
                    endOfType = typeName.Length;
                }

                var typeWithoutGenerics = typeName.AsSpan()[0..endOfType];

                // by convention only 
                return
                    typeWithoutGenerics.EndsWith("Delegate", StringComparison.Ordinal) ||
                    typeWithoutGenerics.EndsWith("Handler", StringComparison.Ordinal) ||
                    typeWithoutGenerics.EndsWith("Callback", StringComparison.Ordinal);

                // arrays end with [,,,,,,] (with one , for each additional dimension; zero , for one dimension arrays)
                static bool IsArray(string name)
                {
                    if (!name.EndsWith("]"))
                    {
                        return false;
                    }

                    var ix = name.Length - 2;
                    while (ix >= 0)
                    {
                        var c = name[ix];
                        if (c == ',')
                        {
                            ix--;
                            continue;
                        }

                        if (c == '[')
                        {
                            return true;
                        }

                        break;
                    }

                    return false;
                }
            }
        }

        internal async ValueTask<ImmutableHashSet<long>> DetermineActualDelegateTypesAsync()
        {
            if (typeDetails == null)
            {
                throw new Exception("Type details not loaded, this shouldn't be possible");
            }

            var candidateDelegateTypes = GetCandidateDelegateTypes(typeDetails);

            SetProgressLimit(ProgressKind.DeterminingDelegates, candidateDelegateTypes.Count);

            var byProc =
                candidateDelegateTypes
                    .Select((cdt, ix) => (cdt, ix))
                    .GroupBy(g => g.ix % numProcs)
                    .Select(g => g.Select(x => x.cdt).ToImmutableList())
                    .ToImmutableList();

            var actualDelegateTasks = new ValueTask<ImmutableHashSet<long>>[byProc.Count];
            for (var i = 0; i < actualDelegateTasks.Length; i++)
            {
                var forProc = byProc[i];
                var partialTask =
                    procs.RunWithLeasedAsync(
                        proc => DetermineActualDelegateTypesInnerAsync(this, proc, forProc)
                    );
                actualDelegateTasks[i] = partialTask;
            }

            var res = await actualDelegateTasks.WhenAll().ConfigureAwait(false);

            var ret = res.Aggregate(ImmutableHashSet<long>.Empty, static (cur, next) => cur.Union(next));

            return ret;
        }

        private static async ValueTask<ImmutableHashSet<long>> DetermineActualDelegateTypesInnerAsync(
            DumpDiagnoser<TAnalyzer> self,
            TAnalyzer proc,
            IEnumerable<(string TypeName, ImmutableHashSet<long> MethodTables)> candidateDelegateTypes
        )
        {
            var builder = ImmutableHashSet.CreateBuilder<long>();

            var pending = 0;

            foreach (var del in candidateDelegateTypes)
            {
                pending++;
                if (pending == 32)
                {
                    self.MadeProgress(ProgressKind.DeterminingDelegates, pending);
                    pending = 0;
                }

                foreach (var mt in del.MethodTables)
                {
                    var eeFromMT = await proc.LoadEEClassAsync(mt).ConfigureAwait(false);

                    var curEE = eeFromMT;
                    var isDelegate = false;
                    while (curEE != 0)
                    {
                        var curClass = await proc.LoadEEClassDetailsAsync(curEE).ConfigureAwait(false);

                        if (curClass.ClassName.Equals(typeof(Delegate).FullName))
                        {
                            isDelegate = true;
                            break;
                        }

                        curEE = curClass.ParentEEClass;
                    }

                    if (isDelegate)
                    {
                        builder.Add(mt);
                    }
                }
            }

            if (pending != 0)
            {
                self.MadeProgress(ProgressKind.DeterminingDelegates, pending);
            }

            return builder.ToImmutable();
        }

        internal async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadDelegateCountsAsync()
        {
            if (liveHeapEntries == null || deadHeapEntries == null)
            {
                throw new Exception("Heap entries not loaded, this shouldn't be possible");
            }

            var actualDelegates = await DetermineActualDelegateTypesAsync().ConfigureAwait(false);

            SetProgressLimit(ProgressKind.DelegateDetails, liveHeapEntries.Count + deadHeapEntries.Count);

            var delegateCountTasks = new ValueTask<ImmutableDictionary<string, ReferenceStats>>[numProcs];
            for (var i = 0; i < delegateCountTasks.Length; i++)
            {
                var live = liveHeapEntriesByProc[i];
                var dead = deadHeapEntriesByProc[i];
                var partialTask =
                    procs.RunWithLeasedAsync(
                        proc => LoadDelegateCountsInnerAsync(this, proc, actualDelegates, live, dead)
                    );
                delegateCountTasks[i] = partialTask;
            }

            var res = await delegateCountTasks.WhenAll().ConfigureAwait(false);

            var delCounts = MergeReferenceStats(res);

            return delCounts;
        }

        private static async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadDelegateCountsInnerAsync(
            DumpDiagnoser<TAnalyzer> self,
            TAnalyzer proc,
            ImmutableHashSet<long> delegateMethodTables,
            IEnumerable<HeapEntry> liveHeap,
            IEnumerable<HeapEntry> deadHeap
        )
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ReferenceStats>();

            // count up stats for live delegate references...
            var pending = 0;
            foreach (var entry in liveHeap)
            {
                pending++;
                if (pending == 1024)
                {
                    self.MadeProgress(ProgressKind.DelegateDetails, pending);
                    pending = 0;
                }

                if (!delegateMethodTables.Contains(entry.MethodTable))
                {
                    continue;
                }

                var del = await proc.LoadDelegateDetailsAsync(entry).ConfigureAwait(false);

                foreach (var detail in del.MethodDetails)
                {
                    if (!builder.TryGetValue(detail.BackingMethodName, out var curStats))
                    {
                        curStats = new ReferenceStats(0, 0, 0, 0);
                    }

                    var updatesStats = new ReferenceStats(curStats.Live + 1, curStats.LiveBytes + entry.SizeBytes, curStats.Dead, curStats.DeadBytes);

                    builder[detail.BackingMethodName] = updatesStats;
                }
            }

            // count up stats for dead delegate references...
            foreach (var entry in deadHeap)
            {
                pending++;
                if (pending == 1024)
                {
                    self.MadeProgress(ProgressKind.DelegateDetails, pending);
                    pending = 0;
                }

                if (!delegateMethodTables.Contains(entry.MethodTable))
                {
                    continue;
                }

                var del = await proc.LoadDelegateDetailsAsync(entry).ConfigureAwait(false);

                foreach (var detail in del.MethodDetails)
                {
                    if (!builder.TryGetValue(detail.BackingMethodName, out var curStats))
                    {
                        curStats = new ReferenceStats(0, 0, 0, 0);
                    }

                    var updatesStats = new ReferenceStats(curStats.Live, curStats.LiveBytes, curStats.Dead + 1, curStats.DeadBytes + entry.SizeBytes);

                    builder[detail.BackingMethodName] = updatesStats;
                }
            }

            if (pending != 0)
            {
                self.MadeProgress(ProgressKind.DelegateDetails, pending);
            }

            return builder.ToImmutable();
        }

        internal async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadCharacterArrayCountsAsync()
        {
            if (liveHeapEntries == null || deadHeapEntries == null)
            {
                throw new Exception("Heap entries not loaded, this shouldn't be possible");
            }

            if (typeDetails == null)
            {
                throw new Exception("Type details not loaded, this shouldn't be possible");
            }

            SetProgressLimit(ProgressKind.CharacterArrays, liveHeapEntries.Count + deadHeapEntries.Count);

            var charCountTasks = new ValueTask<ImmutableDictionary<string, ReferenceStats>>[numProcs];

            var charType = typeDetails[charArrayTypeDetails].Single();
            for (var i = 0; i < charCountTasks.Length; i++)
            {
                var live = liveHeapEntriesByProc[i];
                var dead = deadHeapEntriesByProc[i];
                var partialTask =
                    procs.RunWithLeasedAsync(
                        proc => LoadCharacterArrayCountsInnerAsync(this, proc, charType, live, dead)
                    );
                charCountTasks[i] = partialTask;
            }
            var res = await charCountTasks.WhenAll().ConfigureAwait(false);

            var charCounts = MergeReferenceStats(res);

            return charCounts;
        }

        private static async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadCharacterArrayCountsInnerAsync(
            DumpDiagnoser<TAnalyzer> self,
            TAnalyzer proc,
            long charArrayTypeMethodTable,
            IEnumerable<HeapEntry> liveHeap,
            IEnumerable<HeapEntry> deadHeap
        )
        {
            var builder = ImmutableDictionary.CreateBuilder<string, ReferenceStats>();

            // queue up all the tasks for reading live heap char[]s...
            var curHandled = 0;
            foreach (var entry in liveHeap)
            {
                curHandled++;
                if (curHandled == 1024)
                {
                    self.MadeProgress(ProgressKind.CharacterArrays, curHandled);
                    curHandled = 0;
                }

                if (charArrayTypeMethodTable != entry.MethodTable)
                {
                    continue;
                }

                var details = await proc.LoadArrayDetailsAsync(entry).ConfigureAwait(false);
                var str = details.FirstElementAddress == null ? "" : (await proc.LoadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false));

                if (!builder.TryGetValue(str, out var curStats))
                {
                    curStats = new ReferenceStats(0, 0, 0, 0);
                }

                var updatesStats = new ReferenceStats(curStats.Live + 1, curStats.LiveBytes + entry.SizeBytes, curStats.Dead, curStats.DeadBytes);

                builder[str] = updatesStats;
            }

            // queue up all the tasks for reading dead heap char[]s...
            foreach (var entry in deadHeap)
            {
                curHandled++;
                if (curHandled == 1024)
                {
                    self.MadeProgress(ProgressKind.CharacterArrays, curHandled);
                    curHandled = 0;
                }

                if (charArrayTypeMethodTable != entry.MethodTable)
                {
                    continue;
                }

                var details = await proc.LoadArrayDetailsAsync(entry).ConfigureAwait(false);
                var str = details.FirstElementAddress == null ? "" : (await proc.LoadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false));

                if (!builder.TryGetValue(str, out var curStats))
                {
                    curStats = new ReferenceStats(0, 0, 0, 0);
                }

                var updatesStats = new ReferenceStats(curStats.Live, curStats.LiveBytes, curStats.Dead + 1, curStats.DeadBytes + entry.SizeBytes);

                builder[str] = updatesStats;
            }

            if (curHandled != 0)
            {
                self.MadeProgress(ProgressKind.CharacterArrays, curHandled);
            }

            return builder.ToImmutable();
        }

        internal async ValueTask<ThreadAnalysis> LoadThreadDetailsAsync()
        {
            SetProgressLimit(ProgressKind.ThreadDetails, threadCount);

            var threadIxByProc =
                    Enumerable.Range(0, threadCount)
                        .GroupBy(t => t % numProcs)
                        .Select(t => t.Select(x => x).ToImmutableArray())
                        .ToImmutableArray();

            var threadTasks = new ValueTask<ImmutableList<(int ThreadIndex, ImmutableList<AnalyzerStackFrame> StackTrace)>>[threadIxByProc.Length];
            for (var i = 0; i < threadTasks.Length; i++)
            {
                var threadIndexes = threadIxByProc[i];
                var partialTask =
                    procs.RunWithLeasedAsync(
                        proc => LoadThreadDetailsInnerAsync(this, proc, threadIndexes)
                    );
                threadTasks[i] = partialTask;
            }

            var res = await threadTasks.WhenAll().ConfigureAwait(false);

            var callSites =
                res
                    .SelectMany(
                        static x => x.SelectMany(
                            static y => y.StackTrace.Select(
                                static z => z.CallSite
                            )
                        )
                    )
                    .GroupBy(static x => x)
                    .ToImmutableDictionary(
                        static g => g.Key,
                        static g => g.Count()
                    );

            var traces =
                res
                    .SelectMany(static x => x)
                    .OrderBy(static t => t.ThreadIndex)
                    .Select(static t => t.StackTrace)
                    .ToImmutableList();

            return
                new ThreadAnalysis(
                    traces,
                    callSites
                );
        }

        private static async ValueTask<ImmutableList<(int ThreadIndex, ImmutableList<AnalyzerStackFrame> StackTrace)>> LoadThreadDetailsInnerAsync(
            DumpDiagnoser<TAnalyzer> self,
            TAnalyzer proc,
            IEnumerable<int> threadIndexes
        )
        {
            var builder = ImmutableList.CreateBuilder<(int ThreadIndex, ImmutableList<AnalyzerStackFrame> StackTrace)>();

            foreach (var threadIndex in threadIndexes)
            {
                self.MadeProgress(ProgressKind.ThreadDetails, 1);

                var stack = await proc.LoadStackTraceForThreadAsync(threadIndex).ConfigureAwait(false);

                if (stack.Count == 0)
                {
                    continue;
                }

                builder.Add((threadIndex, stack));
            }

            return builder.ToImmutable();
        }

        internal async ValueTask<ImmutableList<AsyncMachineBreakdown>> GetAsyncMachineBreakdownsAsync()
        {
            if (typeDetails == null)
            {
                throw new Exception("Type details not loaded, this shouldn't be possible");
            }

            if (methodTableToTypeDetails == null)
            {
                throw new Exception("Type details not loaded, this shouldn't be possible");
            }

            if (asyncDetails == null)
            {
                throw new Exception("Async details not loaded, this shouldn't be possible");
            }

            var asyncStateMachineTypes =
                asyncDetails
                    .Select(a => methodTableToTypeDetails[a.MethodTable])
                    .Distinct()
                    .ToImmutableList();

            var stateMachineArgs = await GetGenericTypeParametersAsync(asyncStateMachineTypes).ConfigureAwait(false);

            var paired = asyncDetails.Select(x => (Details: x, GenericArguments: stateMachineArgs[methodTableToTypeDetails[x.MethodTable]])).ToImmutableList();

            var byProc =
                paired
                    .Select((t, ix) => (t, ix))
                    .GroupBy(t => t.ix % numProcs)
                    .Select(t => t.Select(x => x.t).ToImmutableList())
                    .ToImmutableList();

            var tasks = new ValueTask<ImmutableList<AsyncMachineBreakdown>>[byProc.Count];
            var methodTableToTypeLookup = new ConcurrentDictionary<long, TypeDetails>(methodTableToTypeDetails);

            for (var i = 0; i < tasks.Length; i++)
            {
                var machinesForProc = byProc[i];

                tasks[i] = procs.RunWithLeasedAsync(proc => LoadAsyncMachineBreakdownAsync(proc, methodTableToTypeDetails, machinesForProc, methodTableToTypeLookup));
            }

            var res = await tasks.WhenAll().ConfigureAwait(false);

            var uniques =
                res
                    .SelectMany(x => x)
                    .ToImmutableList();

            return uniques;

            static async ValueTask<ImmutableList<AsyncMachineBreakdown>> LoadAsyncMachineBreakdownAsync(
                TAnalyzer proc,
                ImmutableDictionary<long, TypeDetails> typeDetails,
                ImmutableList<(AsyncStateMachineDetails Details, ImmutableList<TypeDetails> GenericArguments)> forProc,
                ConcurrentDictionary<long, TypeDetails> methodTableToTypeLookup
            )
            {
                var ret = ImmutableList.CreateBuilder<AsyncMachineBreakdown>();

                foreach (var sm in forProc)
                {
                    var builderType = await proc.LoadMethodTableTypeDetailsAsync(sm.Details.MethodTable).ConfigureAwait(false);
                    var stateMachineClass = await proc.LoadEEClassAsync(sm.GenericArguments[1].MethodTable).ConfigureAwait(false);

                    var asyncBoxDetails = await proc.LoadEEClassDetailsAsync(stateMachineClass).ConfigureAwait(false);

                    await AddBreakdownAsync(
                            proc,
                            ret,
                            methodTableToTypeLookup,
                            builderType.Value,
                            sm.Details.SizeBytes,
                            asyncBoxDetails.InstanceFields
                        )
                        .ConfigureAwait(false);
                }

                return ret.ToImmutable();
            }

            static async ValueTask AddBreakdownAsync(
                TAnalyzer proc,
                ImmutableList<AsyncMachineBreakdown>.Builder ret,
                ConcurrentDictionary<long, TypeDetails> methodTableToTypeLookup,
                TypeDetails type,
                int sizeBytes,
                ImmutableList<InstanceField> instanceFields
            )
            {
                var fields = ImmutableList.CreateBuilder<InstanceFieldWithTypeDetails>();
                foreach (var field in instanceFields)
                {
                    if (field.Name.StartsWith("<>"))
                    {
                        // don't report auto generated fields, there's nothing to be done for them
                        // AND they just clutter stuff up
                        continue;
                    }

                    var fieldMt = field.MethodTable;

                    TypeDetails cached;
                    if (fieldMt == 0)
                    {
                        cached = new TypeDetails("!!UNKNOWN!!", 0);
                    }
                    else if (!methodTableToTypeLookup.TryGetValue(fieldMt, out cached))
                    {
                        var cachedNull = await proc.LoadMethodTableTypeDetailsAsync(fieldMt).ConfigureAwait(false);
                        if (cachedNull == null)
                        {
                            throw new InvalidOperationException($"Couldn't load type details for {fieldMt:X2}");
                        }

                        cached = cachedNull.Value;
                        methodTableToTypeLookup.TryAdd(fieldMt, cached);
                    }

                    fields.Add(new InstanceFieldWithTypeDetails(cached, field));
                }

                ret.Add(new AsyncMachineBreakdown(type, sizeBytes, fields.ToImmutable()));
            }
        }

        internal async ValueTask<ImmutableDictionary<TypeDetails, ImmutableList<TypeDetails>>> GetGenericTypeParametersAsync(IEnumerable<TypeDetails> methodTables)
        {
            if (methodTableToTypeDetails == null)
            {
                throw new Exception("Type details not loaded, this shouldn't be possible");
            }

            var byProc =
                methodTables
                    .Select((t, ix) => (t, ix % numProcs))
                    .GroupBy(t => t.Item2)
                    .Select(g => g.Select(x => x.t).ToImmutableList())
                    .ToImmutableList();

            var tasks = new ValueTask<ImmutableDictionary<TypeDetails, ImmutableList<TypeDetails>>>[byProc.Count];

            for (var i = 0; i < byProc.Count; i++)
            {
                var forProc = byProc[i];
                var task = procs.RunWithLeasedAsync(proc => LoadGenericTypeParameters(methodTableToTypeDetails, proc, forProc));
                tasks[i] = task;
            }

            var res = await tasks.WhenAll().ConfigureAwait(false);

            var ret = res.SelectMany(x => x).ToImmutableDictionary();

            return ret;
        }

        private static async ValueTask<ImmutableDictionary<TypeDetails, ImmutableList<TypeDetails>>> LoadGenericTypeParameters(
            ImmutableDictionary<long, TypeDetails> typeDetailsLookup,
            TAnalyzer proc,
            ImmutableList<TypeDetails> types)
        {
            // todo: this for sure does not work in 32-bit
            // todo: this probably doesn't work in framework (whatever), and needs testing in Core 3.1, and 6

            // if you wanna dig into the runtime code... look at:
            // https://github.com/dotnet/runtime/blob/509d6c38e6a65131e3d334ce70bdcea816146c5b/src/coreclr/vm/genericdict.h
            // &
            // https://github.com/dotnet/runtime/blob/509d6c38e6a65131e3d334ce70bdcea816146c5b/src/coreclr/vm/genericdict.cpp

            // looks like there's a fixed offset (48 or 0x30) to an address, which is per instance info
            // at a negative -8 offset from that info (-1 long) there's a header that lists:
            //    - num entries, num type pairs
            //    - and 4 bytes of padding
            // this is followed by num entries pointers
            //    - the very last entry pointer points to a list of method tables
            //    - there will be num pair method tables, one for each generic argument
            //
            // this is all super specific to .NET version

            const int ARG_LIST_ADDR_OFFSET = 0x30;
            const int POINTER_SIZE = 0x8;
            const long NUM_ENTRIES_MASK = 0x0FFFF;
            const long NUM_PAIRS_MASK = 0xFFFF0000;

            var ret = ImmutableDictionary.CreateBuilder<TypeDetails, ImmutableList<TypeDetails>>();

            foreach (var type in types)
            {
                var mt = type.MethodTable;

                // address where a pointer to the generic arg list is stored
                var perInstInfoAddr = mt + ARG_LIST_ADDR_OFFSET;
                var instInfoAddrs = await proc.LoadLongsAsync(perInstInfoAddr, 1).ConfigureAwait(false);

                // address where header starts
                var instInfoAddr = instInfoAddrs[0];
                var headerAddr = instInfoAddr - POINTER_SIZE;

                var headers = await proc.LoadLongsAsync(headerAddr, 1).ConfigureAwait(false);
                var header = (ulong)(headers[0]) >> 32;
                var numEntries = (int)(header & NUM_ENTRIES_MASK);
                var numPairs = (int)(header & NUM_PAIRS_MASK) >> 16;

                var lastEntryAddr = instInfoAddr + (numEntries - 1) * POINTER_SIZE;
                var entries = await proc.LoadLongsAsync(lastEntryAddr, 1).ConfigureAwait(false);
                var genArgListAddr = entries[0];

                var argDetails = ImmutableList.CreateBuilder<TypeDetails>();

                var genArgPtrs = await proc.LoadLongsAsync(genArgListAddr, numPairs).ConfigureAwait(false);

                var genArgMtsBuilder = ImmutableArray.CreateBuilder<long>(numPairs);

                // some pointers have low bits set, if that's the case they seem to be indirections
                // where the next highest address is the desired one
                foreach(var ptr in genArgPtrs)
                {
                    if((ptr & 0x7) == 0)
                    {
                        genArgMtsBuilder.Add(ptr);
                        continue;
                    }

                    var indirectToAddr = (ptr & ~0x7) + 8;

                    var indirectMt = await proc.LoadLongsAsync(indirectToAddr, 1).ConfigureAwait(false);
                    genArgMtsBuilder.Add(indirectMt[0]);
                }

                var genArgMts = genArgMtsBuilder.ToImmutable();

                foreach (var argMt in genArgMts)
                {
                    TypeDetails details;
                    if (!typeDetailsLookup.TryGetValue(argMt, out details))
                    {
                        var detailsNull = await proc.LoadMethodTableTypeDetailsAsync(argMt).ConfigureAwait(false);
                        if (detailsNull == null)
                        {
                            throw new InvalidOperationException($"Couldn't determine generic argument for {type}");
                        }

                        details = detailsNull.Value;
                    }

                    argDetails.Add(details);
                }


                ret[type] = argDetails.ToImmutable();
            }

            return ret.ToImmutable();
        }

        internal async ValueTask<PinAnalysis> LoadPinAnalysisAsync()
        {
            if (heapClassifications == null)
            {
                throw new Exception("Heap classifications not loaded, this shouldn't be possible");
            }

            if (methodTableToTypeDetails == null)
            {
                throw new Exception("Method table lookup not loaded, this shouldn't be possible");
            }

            // load pins
            var pinsRaw = await procs.RunWithLeasedAsync(proc => proc.LoadGCHandlesAsync()).ConfigureAwait(false);
            SetProgressLimit(ProgressKind.AnalyzingPins, pinsRaw.Count);

            // figure out method tables if we couldn't cheat and infer them
            var needExtraState = pinsRaw.Where(p => !p.MethodTableInitialized).ToImmutableList();

            var byProc =
                needExtraState
                    .Select((t, ix) => (t, ix % numProcs))
                    .GroupBy(t => t.Item2)
                    .Select(g => g.Select(x => x.t).ToImmutableList())
                    .ToImmutableList();

            var tasks = new ValueTask<ImmutableDictionary<long, long>>[byProc.Count];

            for (var i = 0; i < tasks.Length; i++)
            {
                var heapGCHandles = byProc[i];

                tasks[i] = procs.RunWithLeasedAsync(proc => LoadMethodTableForHandlesAsync(proc, heapGCHandles));
            }

            await tasks.WhenAll().ConfigureAwait(false);
            var lookup = tasks.SelectMany(kv => kv.Result).ToImmutableDictionary();

            var pinsBuilder = ImmutableList.CreateBuilder<HeapGCHandle>();
            foreach (var pin in pinsRaw)
            {
                if (pin.MethodTableInitialized)
                {
                    pinsBuilder.Add(pin);
                    continue;
                }

                var mt = lookup[pin.HandleAddress];
                var withMt = pin.SetMethodTable(mt);

                pinsBuilder.Add(withMt);
            }

            var pins = pinsBuilder.ToImmutable();

            var pinCountsBuilder = ImmutableDictionary.CreateBuilder<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>.Builder>();
            var asyncPinCountsBuilder = ImmutableDictionary.CreateBuilder<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>.Builder>();

            var pinsHandled = 0;

            foreach (var pin in pins)
            {
                pinsHandled++;

                if ((pinsHandled % 100) == 0)
                {
                    MadeProgress(ProgressKind.AnalyzingPins, pinsHandled);
                    pinsHandled = 0;
                }

                ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>.Builder>.Builder toUsePinCounts;

                if (pin.HandleType == HeapGCHandle.HandleTypes.AsyncPinned)
                {
                    toUsePinCounts = asyncPinCountsBuilder;
                }
                else if (pin.HandleType == HeapGCHandle.HandleTypes.Pinned)
                {
                    toUsePinCounts = pinCountsBuilder;
                }
                else
                {
                    continue;
                }

                var he = new HeapEntry(pin.ObjectAddress, pin.MethodTable, pin.Size, true);
                var classified = DetermineGeneration(heapClassifications, he);

                var pinType = methodTableToTypeDetails[pin.MethodTable];

                if (!toUsePinCounts.TryGetValue(classified, out var byTypeName))
                {
                    toUsePinCounts[classified] = byTypeName = ImmutableDictionary.CreateBuilder<TypeDetails, (int Count, long Size)>();
                }

                if (!byTypeName.TryGetValue(pinType, out var cur))
                {
                    byTypeName[pinType] = cur = (0, 0);
                }

                byTypeName[pinType] = (cur.Count + 1, cur.Size + pin.Size);
            }

            if (pinsHandled != 0)
            {
                MadeProgress(ProgressKind.AnalyzingPins, pinsHandled);
            }

            var pinCounts = pinCountsBuilder.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutable());
            var asyncCounts = asyncPinCountsBuilder.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutable());

            return new PinAnalysis(pinCounts, asyncCounts);

            static HeapDetails.HeapClassification DetermineGeneration(ImmutableList<HeapDetails> details, HeapEntry he)
            {
                foreach (var detail in details)
                {
                    if (detail.TryClassify(he, out var classification))
                    {
                        return classification;
                    }
                }

                throw new Exception($"Couldn't classify {he}, shouldn't be possible");
            }

            static async ValueTask<ImmutableDictionary<long, long>> LoadMethodTableForHandlesAsync(TAnalyzer proc, ImmutableList<HeapGCHandle> handles)
            {
                var ret = ImmutableDictionary.CreateBuilder<long, long>();

                foreach (var handle in handles)
                {
                    var details = await proc.LoadObjectInstanceFieldsSpecificsAsync(handle.ObjectAddress).ConfigureAwait(false);

                    if (details == null)
                    {
                        throw new Exception("Failed to load details on object reference, shouldn't be possible");
                    }

                    ret.Add(handle.HandleAddress, details.Value.MethodTable);
                }

                return ret.ToImmutable();
            }
        }

        internal async ValueTask<AnalyzeResult> AnalyzeAsync()
        {
            if (liveHeapEntries == null || deadHeapEntries == null)
            {
                throw new Exception("Heap entries not loaded, this shouldn't be possible");
            }

            if (typeDetails == null)
            {
                throw new Exception("Type details not loaded, this shouldn't be possible");
            }

            if (asyncDetails == null)
            {
                throw new Exception("Async details not loaded, this shouldn't be possible");
            }

            if (heapFragmentation == null)
            {
                throw new Exception("Heap fragmentation not determined, this shouldn't be possible");
            }

            // load string counts
            var stringCounts = await LoadStringCountsAsync().ConfigureAwait(false);

            // we can do this while the strings tasks (typically the longest one) runs since
            // we don't need an AnalyzerProcess for it
            var typeTotals = GetTypeTotals(liveHeapEntries, deadHeapEntries, typeDetails);

            // same for this
            var asyncTotals = GetAsyncTotals(liveHeapEntries, deadHeapEntries, asyncDetails);

            // load char counts in parallel
            var charCounts = await LoadCharacterArrayCountsAsync().ConfigureAwait(false);

            // load delegate counts in parallel
            var delegateCounts = await LoadDelegateCountsAsync().ConfigureAwait(false);

            // load thread details in parallel
            var threadDetails = await LoadThreadDetailsAsync().ConfigureAwait(false);

            // load async state machine details in parallel
            var asyncMachineBreakDowns = await GetAsyncMachineBreakdownsAsync().ConfigureAwait(false);

            // classify pins
            var pinAnalysis = await LoadPinAnalysisAsync().ConfigureAwait(false);

            return new AnalyzeResult(typeTotals, stringCounts, delegateCounts, charCounts, asyncTotals, threadDetails, asyncMachineBreakDowns, pinAnalysis, heapFragmentation.Value);

            // all of this is in memory
            static ImmutableDictionary<TypeDetails, ReferenceStats> GetTypeTotals(
                ImmutableList<HeapEntry> liveHeapEntries,
                ImmutableList<HeapEntry> deadHeapEntries,
                ImmutableDictionary<TypeDetails, ImmutableHashSet<long>> typeDetails
            )
            {
                var typeLookup =
                    typeDetails
                        .SelectMany(kv => kv.Value.Select(mt => (MethodTable: mt, TypeName: kv.Key)))
                        .ToImmutableDictionary(t => t.MethodTable, t => t.TypeName);

                var typeRefTotals = ImmutableDictionary.CreateBuilder<TypeDetails, ReferenceStats>();

                foreach (var entry in liveHeapEntries)
                {
                    if (!typeLookup.TryGetValue(entry.MethodTable, out var typeEntry))
                    {
                        continue;
                    }

                    if (!typeRefTotals.TryGetValue(typeEntry, out var existing))
                    {
                        existing = new ReferenceStats(0, 0, 0, 0);
                    }

                    typeRefTotals[typeEntry] = new ReferenceStats(existing.Live + 1, existing.LiveBytes + entry.SizeBytes, existing.Dead, existing.DeadBytes);
                }

                foreach (var entry in deadHeapEntries)
                {
                    if (!typeLookup.TryGetValue(entry.MethodTable, out var typeEntry))
                    {
                        continue;
                    }

                    if (!typeRefTotals.TryGetValue(typeEntry, out var existing))
                    {
                        existing = new ReferenceStats(0, 0, 0, 0);
                    }

                    typeRefTotals[typeEntry] = new ReferenceStats(existing.Live, existing.LiveBytes, existing.Dead + 1, existing.DeadBytes + entry.SizeBytes);
                }

                return typeRefTotals.ToImmutable();
            }

            // all of this is in memory
            static ImmutableDictionary<TypeDetails, ReferenceStats> GetAsyncTotals(
                ImmutableList<HeapEntry> liveHeapEntries,
                ImmutableList<HeapEntry> deadHeapEntries,
                ImmutableList<AsyncStateMachineDetails> asyncDetails
            )
            {
                var asyncLookup =
                    asyncDetails
                        .GroupBy(d => d.MethodTable)
                        .ToImmutableDictionary(
                            g => g.Key,
                            g => new TypeDetails(InferBackingMethodName(g.First().Description), g.Key)
                        );

                var asyncTotals = ImmutableDictionary.CreateBuilder<TypeDetails, ReferenceStats>();

                foreach (var entry in liveHeapEntries)
                {
                    if (!asyncLookup.TryGetValue(entry.MethodTable, out var methodName))
                    {
                        continue;
                    }

                    if (!asyncTotals.TryGetValue(methodName, out var existing))
                    {
                        existing = new ReferenceStats(0, 0, 0, 0);
                    }

                    asyncTotals[methodName] = new ReferenceStats(existing.Live + 1, existing.LiveBytes + entry.SizeBytes, existing.Dead, existing.DeadBytes);
                }

                foreach (var entry in deadHeapEntries)
                {
                    if (!asyncLookup.TryGetValue(entry.MethodTable, out var methodName))
                    {
                        continue;
                    }

                    if (!asyncTotals.TryGetValue(methodName, out var existing))
                    {
                        existing = new ReferenceStats(0, 0, 0, 0);
                    }

                    asyncTotals[methodName] = new ReferenceStats(existing.Live, existing.LiveBytes, existing.Dead + 1, existing.DeadBytes + entry.SizeBytes);
                }

                return asyncTotals.ToImmutable();
            }

            static string InferBackingMethodName(string desc)
            {
                if (desc.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder"))
                {
                    // this is pretty hacky, but close enough probably?
                    var startIx = desc.LastIndexOf('[');
                    if (startIx == -1)
                    {
                        return desc;
                    }

                    var endIx = desc.IndexOf(']', startIx + 1);
                    if (endIx == -1)
                    {
                        return desc;
                    }

                    // with will give the type name (which will include method), followed by a , followed by the module it's defined in
                    var name = desc[(startIx + 1)..endIx];

                    return name;
                }

                return desc;
            }
        }

        public ValueTask DisposeAsync()
        {
            progressTimer.Dispose();

            return procs.DisposeAsync();
        }

        private static ImmutableHashSet<long> DetermineMaximumRelevantTypeMethodTables(
            ImmutableDictionary<TypeDetails, ImmutableHashSet<long>> types,
            TypeDetails stringTypeDetails,
            TypeDetails charArrayTypeDetails
        )
        {
            var dels = GetCandidateDelegateTypes(types);

            dels = dels.Add((stringTypeDetails.TypeName, types[stringTypeDetails]));
            dels = dels.Add((charArrayTypeDetails.TypeName, types[charArrayTypeDetails]));

            return dels.SelectMany(x => x.MethodTables).ToImmutableHashSet();
        }

        internal async ValueTask StartAnalyzersAsync(Func<int, Task<TAnalyzer>> createDel)
        {
            SetProgressLimit(ProgressKind.StartingTasks, numProcs);

            this.progressTimer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));

            var procTasks = new Task<TAnalyzer>[numProcs];
            for (var i = 0; i < procTasks.Length; i++)
            {
                procTasks[i] = createDel(i);
            }

            var ready = await Task.WhenAll(procTasks).ConfigureAwait(false);

            procs.SetTrackedValues(ready);
        }

        private ValueTask<int> LoadThreadCountAsync()
        {
            SetProgressLimit(ProgressKind.ThreadCount, 1);

            return
                procs.RunWithLeasedAsync(
                    async proc =>
                    {
                        var ret = await proc.CountActiveThreadsAsync().ConfigureAwait(false);
                        MadeProgress(ProgressKind.ThreadCount, 1);
                        return ret;
                    }
                );
        }

        private async ValueTask<ImmutableDictionary<TypeDetails, ImmutableHashSet<long>>> LoadTypeDetailsAsync()
        {
            var uniqueMethodTables =
                await procs.RunWithLeasedAsync(
                    proc => proc.LoadUniqueMethodTablesAsync()
                )
                .ConfigureAwait(false);

            SetProgressLimit(ProgressKind.TypeDetails, uniqueMethodTables.Count + 1);
            MadeProgress(ProgressKind.TypeDetails, 1);

            var mtsByProc =
                uniqueMethodTables
                    .Select((mt, ix) => (mt, ix))
                    .GroupBy(t => t.ix % numProcs)
                    .Select(t => t.Select(t => t.mt).ToImmutableList())
                    .ToImmutableList();
            var mtNamesTasks = new ValueTask<ImmutableList<TypeDetails>>[mtsByProc.Count];
            for (var i = 0; i < mtsByProc.Count; i++)
            {
                var forTask = mtsByProc[i];
                var newTask =
                    procs.RunWithLeasedAsync(
                        async proc =>
                        {
                            var ret = ImmutableList.CreateBuilder<TypeDetails>();

                            var soFar = 0;

                            foreach (var mt in forTask)
                            {
                                soFar++;

                                if (soFar == 1000)
                                {
                                    MadeProgress(ProgressKind.TypeDetails, soFar);
                                    soFar = 0;
                                }

                                var name = await proc.LoadMethodTableTypeDetailsAsync(mt).ConfigureAwait(false);
                                if (name == null)
                                {
                                    throw new InvalidOperationException($"Couldn't load type details for {mt:X2}");
                                }

                                ret.Add(name.Value);
                            }

                            if (soFar != 0)
                            {
                                MadeProgress(ProgressKind.TypeDetails, soFar);
                            }

                            return ret.ToImmutable();
                        }
                    );

                mtNamesTasks[i] = newTask;
            }

            var allRes = await mtNamesTasks.WhenAll().ConfigureAwait(false);

            var ret =
                allRes
                    .SelectMany(t => t)
                    .GroupBy(t => t.TypeName)
                    .ToImmutableDictionary(
                        g => g.First(),
                        g => g.Select(t => t.MethodTable).ToImmutableHashSet()
                    );

            return ret;
        }

        private ValueTask<ImmutableList<HeapEntry>> LoadHeapAsync(LoadHeapMode mode, ImmutableHashSet<long> relevantTypes)
        {
            return
                procs.RunWithLeasedAsync(
                    async proc =>
                    {
                        var e = proc.LoadHeapAsync(mode);

                        var ret = await FilterToRelevantHeapEntriesAsync(e, relevantTypes).ConfigureAwait(false);

                        MadeProgress(ProgressKind.LoadingHeap, 1);

                        return ret;
                    }
                );

            // takes a stream of heap entries and only records those that we think we'll need later
            static async ValueTask<ImmutableList<HeapEntry>> FilterToRelevantHeapEntriesAsync(IAsyncEnumerable<HeapEntry> allEntries, ImmutableHashSet<long> relevantMethodTables)
            {
                var ret = ImmutableList.CreateBuilder<HeapEntry>();
                await foreach (var entry in allEntries.ConfigureAwait(false))
                {
                    if (relevantMethodTables.Contains(entry.MethodTable))
                    {
                        ret.Add(entry);
                    }
                }

                return ret.ToImmutable();
            }
        }

        private ValueTask<StringDetails> LoadStringDetailsAsync(HeapEntry stringHeapEntry)
        {
            return
                procs.RunWithLeasedAsync(
                    async proc =>
                    {
                        var ret = await proc.LoadStringDetailsAsync(stringHeapEntry).ConfigureAwait(false);

                        MadeProgress(ProgressKind.LoadingHeap, 1);
                        return ret;
                    }
                );
        }

        private ValueTask<ImmutableList<AsyncStateMachineDetails>> LoadAsyncDetailsAsync()
        {
            SetProgressLimit(ProgressKind.AsyncDetails, 1);

            return procs.RunWithLeasedAsync(
                async proc =>
                {
                    var ret = ImmutableList.CreateBuilder<AsyncStateMachineDetails>();

                    await foreach (var entry in proc.LoadAsyncStateMachinesAsync().ConfigureAwait(false))
                    {
                        ret.Add(entry);
                    }

                    MadeProgress(ProgressKind.AsyncDetails, 1);

                    return ret.ToImmutable();
                }
            );
        }

        private ValueTask<(ImmutableList<HeapDetails> Classifications, HeapFragmentation Fragementation)> LoadHeapClassificationsAsync()
        {
            SetProgressLimit(ProgressKind.HeapAssignments, 2);

            return procs.RunWithLeasedAsync(
                async proc =>
                {
                    var classification = await proc.LoadHeapDetailsAsync().ConfigureAwait(false);
                    MadeProgress(ProgressKind.HeapAssignments, 1);

                    var fragmentation = await proc.LoadHeapFragmentationAsync().ConfigureAwait(false);
                    MadeProgress(ProgressKind.HeapAssignments, 1);

                    return (classification, fragmentation);
                }
            );
        }

        internal async ValueTask LoadNeededStateAsync()
        {
            // load thread counts
            var threadCountTask = LoadThreadCountAsync();

            // load type details
            {
                var typeDetails = await LoadTypeDetailsAsync().ConfigureAwait(false);
                this.typeDetails = typeDetails;
                methodTableToTypeDetails = typeDetails.SelectMany(kv => kv.Value.Select(x => (MethodTable: x, Type: kv.Key))).ToImmutableDictionary(t => t.MethodTable, t => t.Type);

                stringTypeDetails = typeDetails.Keys.Single(k => k.TypeName == typeof(string).FullName);
                charArrayTypeDetails = typeDetails.Keys.Single(k => k.TypeName == typeof(char[]).FullName);
            }

            // load async details
            var asyncDetailsTask = LoadAsyncDetailsAsync();

            // only keep the relevant heap entries in memory, otherwise we're gonna balloon badly
            SetProgressLimit(ProgressKind.LoadingHeap, 3);
            var relevantTypesDueToName = DetermineMaximumRelevantTypeMethodTables(typeDetails, stringTypeDetails, charArrayTypeDetails);

            var asyncDetails = await asyncDetailsTask.ConfigureAwait(false);    // need to keep the state machine heap entries around...
            this.asyncDetails = asyncDetails;

            var maximumRelevantTypes = relevantTypesDueToName.Union(asyncDetails.Select(a => a.MethodTable));

            var liveHeapTask = LoadHeapAsync(LoadHeapMode.Live, maximumRelevantTypes);
            var deadHeapTask = LoadHeapAsync(LoadHeapMode.Dead, maximumRelevantTypes);

            // string details needs heap, so wait for them...
            await liveHeapTask.ConfigureAwait(false);
            liveHeapEntries = liveHeapTask.Result;

            var stringTypeMethodTable = typeDetails[stringTypeDetails].Single();
            var stringHeapEntry = liveHeapEntries.First(x => stringTypeMethodTable == x.MethodTable);
            var stringDetailsTask = LoadStringDetailsAsync(stringHeapEntry);

            var loadHeapClassificationTask = LoadHeapClassificationsAsync();

            // wait for everything that remains
            deadHeapEntries = await deadHeapTask.ConfigureAwait(false);
            threadCount = await threadCountTask.ConfigureAwait(false);
            stringDetails = await stringDetailsTask.ConfigureAwait(false);
            (heapClassifications, heapFragmentation) = await loadHeapClassificationTask.ConfigureAwait(false);
        }
    }

    internal static class DumpDiagnoser
    {
        internal static async ValueTask<DumpDiagnoser<DotNetDumpAnalyzerProcess>> CreateDotNetDumpAsync(string dotnetDump, string dumpPath, int processCount, IProgress<DumpDiagnoserProgress>? progress = null)
        {
            Debug.Assert(processCount > 0);

            var ret = new DumpDiagnoser<DotNetDumpAnalyzerProcess>(processCount, progress);
            await ret.StartAnalyzersAsync(_ => StartAnalyzerProcessAsync(ret, dotnetDump, dumpPath)).ConfigureAwait(false);
            await ret.LoadNeededStateAsync().ConfigureAwait(false);

            ret.SplitHeapByProcs();

            return ret;

            static async Task<DotNetDumpAnalyzerProcess> StartAnalyzerProcessAsync(
                DumpDiagnoser<DotNetDumpAnalyzerProcess> self,
                string dotnetDump,
                string dumpPath
            )
            {
                var proc = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dotnetDump, dumpPath).ConfigureAwait(false);

                self.MadeProgress(ProgressKind.StartingTasks, 1);

                return proc;
            }
        }

        [SupportedOSPlatform("windows")]
        internal static async ValueTask<DumpDiagnoser<RemoteWinDbg>> CreateRemoteWinDbgAsync(DebugConnectWideThunk connectWideThunk, IEnumerable<RemoteWinDbgAddress> remoteAddresses, TimeSpan timeout, IProgress<DumpDiagnoserProgress>? progress = null)
        {
            var remoteAsList = remoteAddresses.ToImmutableList();

            var ret = new DumpDiagnoser<RemoteWinDbg>(remoteAsList.Count, progress);
            await ret.StartAnalyzersAsync(
                (index) =>
                {
                    var target = remoteAsList[index];

                    return StartRemoteWinDbgAsync(ret, connectWideThunk, target, timeout);
                }
            )
            .ConfigureAwait(false);
            await ret.LoadNeededStateAsync().ConfigureAwait(false);

            ret.SplitHeapByProcs();

            return ret;

            static async Task<RemoteWinDbg> StartRemoteWinDbgAsync(
                DumpDiagnoser<RemoteWinDbg> self,
                DebugConnectWideThunk connectWideThunk,
                RemoteWinDbgAddress target,
                TimeSpan timeout
            )
            {
                var remote = await RemoteWinDbg.CreateAsync(ArrayPool<char>.Shared, connectWideThunk, target.IPAddress, target.Port, timeout).ConfigureAwait(false);

                self.MadeProgress(ProgressKind.StartingTasks, 1);

                return remote;
            }
        }
    }
}
