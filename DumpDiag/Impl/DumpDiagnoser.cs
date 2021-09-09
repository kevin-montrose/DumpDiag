using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace DumpDiag.Impl
{
    internal sealed class DumpDiagnoser : IAsyncDisposable
    {
        private enum ProgressKind : byte
        {
            StartingTasks,
            LoadingHeap,
            TypeDetails,
            ThreadCount,
            ThreadDetails,
            CharacterArrays,
            DeterminingDelegates,
            DelegateDetails,
            Strings
        }

        private readonly AnalyzerProcess[] procs;
        private readonly Task[] taskOnProcs;
        private readonly IProgress<DumpDiagnoserProgress> progress;
        private readonly int[] progressMax;
        private readonly int[] progressCurrent;

        private int nextTaskIndex;
        private int threadCount;
        private ImmutableDictionary<string, ImmutableHashSet<long>> typeDetails;
        private StringDetails stringDetails;
        private ImmutableList<HeapEntry> liveHeapEntries;
        private ImmutableList<HeapEntry> deadHeapEntries;

        private ImmutableArray<ImmutableList<HeapEntry>> liveHeapEntriesByProc;
        private ImmutableArray<ImmutableList<HeapEntry>> deadHeapEntriesByProc;

        private DumpDiagnoser(int numProcs, IProgress<DumpDiagnoserProgress> progress)
        {
            procs = new AnalyzerProcess[numProcs];
            taskOnProcs = new Task[numProcs];
            this.progress = progress;

            progressMax = new int[Enum.GetValues(typeof(ProgressKind)).Length];
            progressCurrent = new int[progressMax.Length];
        }

        private void SetProgressLimit(ProgressKind kind, int limit)
        {
            Debug.Assert(progressMax[(int)kind] == 0);

            lock (progressMax)
            {
                progressMax[(int)kind] = limit;
            }
        }

        private void MadeProgress(ProgressKind kind, int by)
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
                    prog = prog.WithCharacterArrays(CalcPercent(progressCurrent, progressMax, ProgressKind.CharacterArrays));
                    prog = prog.WithDelegateDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.DelegateDetails));
                    prog = prog.WithDeterminingDelegates(CalcPercent(progressCurrent, progressMax, ProgressKind.DeterminingDelegates));
                    prog = prog.WithLoadingHeap(CalcPercent(progressCurrent, progressMax, ProgressKind.LoadingHeap));
                    prog = prog.WithStartingTasks(CalcPercent(progressCurrent, progressMax, ProgressKind.StartingTasks));
                    prog = prog.WithStrings(CalcPercent(progressCurrent, progressMax, ProgressKind.Strings));
                    prog = prog.WithThreadCount(CalcPercent(progressCurrent, progressMax, ProgressKind.ThreadCount));
                    prog = prog.WithThreadDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.ThreadDetails));
                    prog = prog.WithTypeDetails(CalcPercent(progressCurrent, progressMax, ProgressKind.TypeDetails));

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

        private DumpDiagnoser(AnalyzerProcess[] procs, ImmutableList<HeapEntry> liveHeapEntries, ImmutableList<HeapEntry> deadHeapEntries, int threadCount, ImmutableDictionary<string, ImmutableHashSet<long>> typeDetails, StringDetails stringDetails)
        {
            this.procs = procs;
            this.liveHeapEntries = liveHeapEntries;
            this.deadHeapEntries = deadHeapEntries;
            this.threadCount = threadCount;
            this.typeDetails = typeDetails;
            this.stringDetails = stringDetails;
        }

        private async ValueTask<Task<T>> PlaceOnProcessAsync<T>(Func<AnalyzerProcess, Task<T>> del)
        {
            Task<T> createdTask;

            if (procs.Length > nextTaskIndex)
            {
                taskOnProcs[nextTaskIndex] = createdTask = del(procs[nextTaskIndex]);
            }
            else
            {
                var toReuse = await Task.WhenAny(taskOnProcs).ConfigureAwait(false);
                var toReuseIx = Array.IndexOf(taskOnProcs, toReuse);
                taskOnProcs[toReuseIx] = createdTask = del(procs[toReuseIx]);
            }

            nextTaskIndex++;

            return createdTask;
        }

        private async ValueTask<Task> PlaceOnProcessAsync(Func<AnalyzerProcess, Task> del)
        {
            Task createdTask;

            if (procs.Length > nextTaskIndex)
            {
                taskOnProcs[nextTaskIndex] = createdTask = del(procs[nextTaskIndex]);
            }
            else
            {
                var toReuse = await Task.WhenAny(taskOnProcs).ConfigureAwait(false);
                var toReuseIx = Array.IndexOf(taskOnProcs, toReuse);
                taskOnProcs[toReuseIx] = createdTask = del(procs[toReuseIx]);
            }

            nextTaskIndex++;

            return createdTask;
        }

        private void SplitHeapByProcs()
        {
            Debug.Assert(liveHeapEntriesByProc == null);
            Debug.Assert(deadHeapEntriesByProc == null);

            liveHeapEntriesByProc =
                liveHeapEntries
                    .Select((he, ix) => (he, ix))
                    .GroupBy(t => t.ix % procs.Length)
                    .Select(g => g.Select(x => x.he).ToImmutableList())
                    .ToImmutableArray();

            // practically this will never happen, but for correctness sake in the extreme edges...
            while (liveHeapEntriesByProc.Length < procs.Length)
            {
                liveHeapEntriesByProc = liveHeapEntriesByProc.Add(ImmutableList<HeapEntry>.Empty);
            }

            deadHeapEntriesByProc =
                deadHeapEntries
                    .Select((he, ix) => (he, ix))
                    .GroupBy(t => t.ix % procs.Length)
                    .Select(g => g.Select(x => x.he).ToImmutableList())
                    .ToImmutableArray();

            // practically this will never happen, but for correctness sake in the extreme edges...
            while (deadHeapEntriesByProc.Length < procs.Length)
            {
                deadHeapEntriesByProc = liveHeapEntriesByProc.Add(ImmutableList<HeapEntry>.Empty);
            }
        }

        internal async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadStringCountsAsync()
        {
            SetProgressLimit(ProgressKind.Strings, liveHeapEntries.Count + deadHeapEntries.Count);
            
            // load string counts in parallel
            var stringCountTasks = new Task[procs.Length];
            var stringCountsMutable = new ConcurrentDictionary<string, ReferenceStats>();
            for (var i = 0; i < procs.Length; i++)
            {
                var live = liveHeapEntriesByProc[i];
                var dead = deadHeapEntriesByProc[i];
                var partialTask =
                    await PlaceOnProcessAsync(
                        proc => LoadStringCountsInnerAsync(this, proc, stringDetails, live, dead, stringCountsMutable).AsTask()
                    )
                    .ConfigureAwait(false);
                stringCountTasks[i] = partialTask;
            }
            await Task.WhenAll(stringCountTasks).ConfigureAwait(false);

            var stringCounts = stringCountsMutable.ToImmutableDictionary();

            return stringCounts;
        }

        private static async ValueTask LoadStringCountsInnerAsync(
            DumpDiagnoser self,
            AnalyzerProcess proc,
            StringDetails stringDetails,
            IEnumerable<HeapEntry> liveHeap,
            IEnumerable<HeapEntry> deadHeap,
            ConcurrentDictionary<string, ReferenceStats> intoDict
        )
        {
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

                var len = await proc.GetStringLengthAsync(stringDetails, entry).ConfigureAwait(false);
                var str = await proc.ReadCharsAsync(entry.Address + stringDetails.FirstCharOffset, len).ConfigureAwait(false);

                intoDict.AddOrUpdate(str, new ReferenceStats(1, 0), static (key, old) => new ReferenceStats(old.Live + 1, old.Dead));
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

                var len = await proc.GetStringLengthAsync(stringDetails, entry).ConfigureAwait(false);
                var str = await proc.ReadCharsAsync(entry.Address + stringDetails.FirstCharOffset, len).ConfigureAwait(false);

                intoDict.AddOrUpdate(str, new ReferenceStats(0, 1), static (key, old) => new ReferenceStats(old.Live, old.Dead + 1));
            }

            if (pendingHandled != 0)
            {
                self.MadeProgress(ProgressKind.Strings, pendingHandled);
            }
        }

        private static ImmutableList<(string TypeName, ImmutableHashSet<long> MethodTables)> GetCandidateDelegateTypes(ImmutableDictionary<string, ImmutableHashSet<long>> typeDetails)
        {
            var candidateDelegateTypes =
               typeDetails
                   .Where(kv => IsProbablyDelegate(kv.Key))
                   .Select(kv => (TypeName: kv.Key, MethodTables: kv.Value))
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
                bool IsArray(string name)
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
            var candidateDelegateTypes = GetCandidateDelegateTypes(typeDetails);

            SetProgressLimit(ProgressKind.DeterminingDelegates, candidateDelegateTypes.Count);

            var byProc =
                candidateDelegateTypes
                    .Select((cdt, ix) => (cdt, ix))
                    .GroupBy(g => g.ix % procs.Length)
                    .Select(g => g.Select(x => x.cdt).ToImmutableList())
                    .ToImmutableList();

            var actualDelegateTasks = new Task[byProc.Count];
            var actualDelegatesMutable = new ConcurrentDictionary<long, long>();
            for (var i = 0; i < byProc.Count; i++)
            {
                var forProc = byProc[i];
                var partialTask =
                    await PlaceOnProcessAsync(
                        proc => DetermineActualDelegateTypesInnerAsync(this, proc, forProc, actualDelegatesMutable).AsTask()
                    )
                    .ConfigureAwait(false);
                actualDelegateTasks[i] = partialTask;
            }

            await Task.WhenAll(actualDelegateTasks).ConfigureAwait(false);

            return actualDelegatesMutable.Keys.ToImmutableHashSet();
        }

        private static async ValueTask DetermineActualDelegateTypesInnerAsync(
            DumpDiagnoser self,
            AnalyzerProcess proc,
            IEnumerable<(string TypeName, ImmutableHashSet<long> MethodTables)> candidateDelegateTypes,
            ConcurrentDictionary<long, long> intoSet
        )
        {
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
                        intoSet[mt] = mt;
                    }
                }
            }

            if (pending != 0)
            {
                self.MadeProgress(ProgressKind.DeterminingDelegates, pending);
            }
        }

        internal async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadDelegateCountsAsync()
        {
            var actualDelegates = await DetermineActualDelegateTypesAsync().ConfigureAwait(false);

            SetProgressLimit(ProgressKind.DelegateDetails, liveHeapEntries.Count + deadHeapEntries.Count);

            var delegateCountTasks = new Task[procs.Length];
            var delegateCountsMutable = new ConcurrentDictionary<string, ReferenceStats>();
            for (var i = 0; i < procs.Length; i++)
            {
                var live = liveHeapEntriesByProc[i];
                var dead = deadHeapEntriesByProc[i];
                var partialTask =
                    await PlaceOnProcessAsync(
                        proc => LoadDelegateCountsInnerAsync(this, proc, actualDelegates, live, dead, delegateCountsMutable).AsTask()
                    )
                    .ConfigureAwait(false);
                delegateCountTasks[i] = partialTask;
            }

            await Task.WhenAll(delegateCountTasks).ConfigureAwait(false);

            return delegateCountsMutable.ToImmutableDictionary();
        }

        private static async ValueTask LoadDelegateCountsInnerAsync(
            DumpDiagnoser self,
            AnalyzerProcess proc,
            ImmutableHashSet<long> delegateMethodTables,
            IEnumerable<HeapEntry> liveHeap,
            IEnumerable<HeapEntry> deadHeap,
            ConcurrentDictionary<string, ReferenceStats> into
        )
        {
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

                var del = await proc.ReadDelegateDetailsAsync(entry).ConfigureAwait(false);

                foreach (var detail in del.MethodDetails)
                {
                    into.AddOrUpdate(detail.BackingMethodName, new ReferenceStats(1, 0), static (key, old) => new ReferenceStats(old.Live + 1, old.Dead));
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

                var del = await proc.ReadDelegateDetailsAsync(entry).ConfigureAwait(false);

                foreach (var detail in del.MethodDetails)
                {
                    into.AddOrUpdate(detail.BackingMethodName, new ReferenceStats(0, 1), static (key, old) => new ReferenceStats(old.Live, old.Dead + 1));
                }
            }

            if (pending != 0)
            {
                self.MadeProgress(ProgressKind.DelegateDetails, pending);
            }
        }

        internal async ValueTask<ImmutableDictionary<string, ReferenceStats>> LoadCharacterArrayCountsAsync()
        {
            SetProgressLimit(ProgressKind.CharacterArrays, liveHeapEntries.Count + deadHeapEntries.Count);

            var charCountTasks = new Task[procs.Length];
            var charCountsMutable = new ConcurrentDictionary<string, ReferenceStats>();
            var charType = typeDetails[typeof(char[]).FullName].Single();
            for (var i = 0; i < procs.Length; i++)
            {
                var live = liveHeapEntriesByProc[i];
                var dead = deadHeapEntriesByProc[i];
                var partialTask =
                    await PlaceOnProcessAsync(
                        proc => LoadCharacterArrayCountsInnerAsync(this, proc, charType, live, dead, charCountsMutable).AsTask()
                    )
                    .ConfigureAwait(false);
                charCountTasks[i] = partialTask;
            }
            await Task.WhenAll(charCountTasks).ConfigureAwait(false);

            return charCountsMutable.ToImmutableDictionary();
        }

        private static async ValueTask LoadCharacterArrayCountsInnerAsync(
            DumpDiagnoser self,
            AnalyzerProcess proc,
            long charArrayTypeMethodTable,
            IEnumerable<HeapEntry> liveHeap,
            IEnumerable<HeapEntry> deadHeap,
            ConcurrentDictionary<string, ReferenceStats> intoDict
        )
        {
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

                var details = await proc.ReadArrayDetailsAsync(entry).ConfigureAwait(false);
                var str = details.Length == 0 ? "" : (await proc.ReadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false));

                intoDict.AddOrUpdate(str, new ReferenceStats(1, 0), static (key, old) => new ReferenceStats(old.Live + 1, old.Dead));
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

                var details = await proc.ReadArrayDetailsAsync(entry).ConfigureAwait(false);
                var str = details.Length == 0 ? "" : (await proc.ReadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false));

                intoDict.AddOrUpdate(str, new ReferenceStats(0, 1), static (key, old) => new ReferenceStats(old.Live, old.Dead + 1));
            }

            if (curHandled != 0)
            {
                self.MadeProgress(ProgressKind.CharacterArrays, curHandled);
            }
        }

        internal async ValueTask<ThreadAnalysis> LoadThreadDetailsAsync()
        {
            SetProgressLimit(ProgressKind.ThreadDetails, threadCount);

            var threadIxByProc =
                    Enumerable.Range(0, threadCount)
                        .GroupBy(t => t % procs.Length)
                        .Select(t => t.Select(x => x).ToImmutableArray())
                        .ToImmutableArray();

            var threadTasks = new Task[threadIxByProc.Length];
            var tracesMutable = new ConcurrentBag<(int ThreadIndex, ImmutableList<AnalyzerStackFrame> StackTrace)>();
            var callSitesMutable = new ConcurrentDictionary<string, int>();
            for (var i = 0; i < threadIxByProc.Length; i++)
            {
                var threadIndexes = threadIxByProc[i];
                var partialTask =
                    await PlaceOnProcessAsync(
                        proc => LoadThreadDetailsInnerAsync(this, proc, threadIndexes, tracesMutable, callSitesMutable).AsTask()
                    )
                    .ConfigureAwait(false);
                threadTasks[i] = partialTask;
            }

            await Task.WhenAll(threadTasks).ConfigureAwait(false);

            return
                new ThreadAnalysis(
                    tracesMutable.OrderBy(t => t.ThreadIndex).Select(t => t.StackTrace).ToImmutableList(),
                    callSitesMutable.ToImmutableDictionary()
                );
        }

        private static async ValueTask LoadThreadDetailsInnerAsync(
            DumpDiagnoser self,
            AnalyzerProcess proc,
            IEnumerable<int> threadIndexes,
            ConcurrentBag<(int ThreadIndex, ImmutableList<AnalyzerStackFrame> StackTrace)> intoTraces,
            ConcurrentDictionary<string, int> intoCallSiteCounts
        )
        {
            foreach (var threadIndex in threadIndexes)
            {
                self.MadeProgress(ProgressKind.ThreadDetails, 1);
                
                var stack = await proc.GetStackTraceForThreadAsync(threadIndex).ConfigureAwait(false);

                if (stack.Count == 0)
                {
                    continue;
                }

                intoTraces.Add((threadIndex, stack));

                foreach (var frame in stack)
                {
                    intoCallSiteCounts.AddOrUpdate(frame.CallSite, 1, static (key, old) => old + 1);
                }
            }
        }


        internal async ValueTask<AnalyzeResult> AnalyzeAsync()
        {
            // load string counts in parallel
            var stringTask = LoadStringCountsAsync();

            // we can do this while the strings tasks (typically the longest one) runs since
            // we don't need an AnalyzerProcess for it
            var typeTotals = GetTypeTotals(liveHeapEntries, deadHeapEntries, typeDetails);

            var stringCounts = await stringTask.ConfigureAwait(false);

            // load char counts in parallel
            var charCounts = await LoadCharacterArrayCountsAsync().ConfigureAwait(false);

            // load delegate counts in parallel
            var delegateCounts = await LoadDelegateCountsAsync().ConfigureAwait(false);

            // load thread details in parallel
            var threadDetails = await LoadThreadDetailsAsync().ConfigureAwait(false);

            return new AnalyzeResult(typeTotals, stringCounts, delegateCounts, charCounts, threadDetails);

            // all of this is in memory
            static ImmutableDictionary<string, ReferenceStats> GetTypeTotals(
                ImmutableList<HeapEntry> liveHeapEntries,
                ImmutableList<HeapEntry> deadHeapEntries,
                ImmutableDictionary<string, ImmutableHashSet<long>> typeDetails
            )
            {
                var typeLookup =
                    typeDetails
                        .SelectMany(kv => kv.Value.Select(mt => (MethodTable: mt, TypeName: kv.Key)))
                        .ToImmutableDictionary(t => t.MethodTable, t => t.TypeName);

                var typeTotals = ImmutableDictionary.CreateBuilder<string, ReferenceStats>();

                foreach (var entry in liveHeapEntries)
                {
                    if (!typeLookup.TryGetValue(entry.MethodTable, out var typeName))
                    {
                        continue;
                    }

                    if (!typeTotals.TryGetValue(typeName, out var existing))
                    {
                        existing = new ReferenceStats(0, 0);
                    }

                    typeTotals[typeName] = new ReferenceStats(existing.Live + 1, existing.Dead);
                }

                foreach (var entry in deadHeapEntries)
                {
                    if (!typeLookup.TryGetValue(entry.MethodTable, out var typeName))
                    {
                        continue;
                    }

                    if (!typeTotals.TryGetValue(typeName, out var existing))
                    {
                        existing = new ReferenceStats(0, 0);
                    }

                    typeTotals[typeName] = new ReferenceStats(existing.Live, existing.Dead + 1);
                }

                return typeTotals.ToImmutable();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var proc in procs)
            {
                await proc.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static ImmutableHashSet<long> DetermineMaximumRelevantTypeMethodTables(ImmutableDictionary<string, ImmutableHashSet<long>> types)
        {
            var dels = GetCandidateDelegateTypes(types);
            dels = dels.Add((typeof(string).FullName, types[typeof(string).FullName]));
            dels = dels.Add((typeof(char[]).FullName, types[typeof(char[]).FullName]));

            return dels.SelectMany(x => x.MethodTables).ToImmutableHashSet();
        }

        private ValueTask StartProcessesAsync(string dotnetDump, string dumpPath)
        {
            SetProgressLimit(ProgressKind.StartingTasks, procs.Length);

            var procTasks = new Task[procs.Length];
            for (var i = 0; i < procTasks.Length; i++)
            {
                procTasks[i] = StartAnalyzerProcessAsync(this, dotnetDump, dumpPath, i);
            }

            return new ValueTask(Task.WhenAll(procTasks));

            static async Task StartAnalyzerProcessAsync(
                DumpDiagnoser self,
                string dotnetDump,
                string dumpPath,
                int index
            )
            {
                var proc = await AnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dotnetDump, dumpPath).ConfigureAwait(false);
                self.procs[index] = proc;

                self.MadeProgress(ProgressKind.StartingTasks, 1);
            }
        }

        private async ValueTask<Task<int>> LoadThreadCountAsync()
        {
            SetProgressLimit(ProgressKind.ThreadCount, 1);

            var threadCountTask =
                await PlaceOnProcessAsync(
                    async proc =>
                    {
                        var ret = await proc.CountActiveThreadsAsync().ConfigureAwait(false);
                        MadeProgress(ProgressKind.ThreadCount, 1);
                        return ret;
                    }
                )
                .ConfigureAwait(false);

            return threadCountTask;
        }

        private async ValueTask<Task<ImmutableDictionary<string, ImmutableHashSet<long>>>> LoadTypeDetailsAsync()
        {
            var uniqueMethodTablesTask =
                await PlaceOnProcessAsync(
                    async proc =>
                    {
                        var ret = await proc.LoadUniqueMethodTablesAsync().ConfigureAwait(false);
                        return ret;
                    }
                )
                .ConfigureAwait(false);
            var uniqueMethodTables = await uniqueMethodTablesTask.ConfigureAwait(false);

            SetProgressLimit(ProgressKind.TypeDetails, uniqueMethodTables.Count + 1);
            MadeProgress(ProgressKind.TypeDetails, 1);

            var mtsByProc =
                uniqueMethodTables
                    .Select((mt, ix) => (mt, ix))
                    .GroupBy(t => t.ix % procs.Length)
                    .Select(t => t.Select(t => t.mt).ToImmutableList())
                    .ToImmutableList();
            var mtNamesTasks = new Task<ImmutableList<(string Name, long MethodTable)>>[mtsByProc.Count];
            for (var i = 0; i < mtsByProc.Count; i++)
            {
                var forTask = mtsByProc[i];
                var newTask =
                    await PlaceOnProcessAsync(
                        async proc =>
                        {
                            var ret = ImmutableList.CreateBuilder<(string Name, long MethodTable)>();

                            var soFar = 0;

                            foreach (var mt in forTask)
                            {
                                soFar++;

                                if (soFar == 1000)
                                {
                                    MadeProgress(ProgressKind.TypeDetails, soFar);
                                    soFar = 0;
                                }

                                var name = await proc.ReadMethodTableTypeNameAsync(mt).ConfigureAwait(false);
                                ret.Add((name, mt));
                            }

                            if (soFar != 0)
                            {
                                MadeProgress(ProgressKind.TypeDetails, soFar);
                            }

                            return ret.ToImmutable();
                        }
                    )
                    .ConfigureAwait(false);

                mtNamesTasks[i] = newTask;
            }

            var whenAll = Task.WhenAll(mtNamesTasks);

            var ret =
                whenAll.ContinueWith(
                    t =>
                        t
                            .Result
                            .SelectMany(x => x)
                            .GroupBy(t => t.Name)
                            .ToImmutableDictionary(
                                g => g.Key,
                                g => g.Select(t => t.MethodTable).ToImmutableHashSet()
                            )
                    );

            return ret;
        }

        private async ValueTask<Task<ImmutableList<HeapEntry>>> LoadHeapAsync(LoadHeapMode mode, ImmutableHashSet<long> relevantTypes)
        {
            var heapTask =
                await PlaceOnProcessAsync(
                    async proc =>
                    {
                        var e = proc.LoadHeapAsync(mode);

                        var ret = await FilterToRelevantHeapEntriesAsync(e, relevantTypes).ConfigureAwait(false);

                        MadeProgress(ProgressKind.LoadingHeap, 1);

                        return ret;
                    }
                )
                .ConfigureAwait(false);

            return heapTask;

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

        private async ValueTask<Task<StringDetails>> LoadStringDetailsAsync(HeapEntry stringHeapEntry)
        {
            var stringDetailsTask = 
                await PlaceOnProcessAsync(
                    async proc => 
                    { 
                        var ret = await proc.LoadStringDetailsAsync(stringHeapEntry).ConfigureAwait(false);

                        MadeProgress(ProgressKind.LoadingHeap, 1);
                        return ret; 
                    }
                )
                .ConfigureAwait(false);

            return stringDetailsTask;
        }

        private async ValueTask LoadNeededStateAsync()
        {
            // load thread counts
            var threadCountTask = await LoadThreadCountAsync().ConfigureAwait(false);

            // load type details
            {
                var typeDetailsTask = await LoadTypeDetailsAsync().ConfigureAwait(false);
                var typeDetails = await typeDetailsTask.ConfigureAwait(false);
                this.typeDetails = typeDetails;
            }

            // only keep the relevant heap entries in memory, otherwise we're gonna balloon badly
            SetProgressLimit(ProgressKind.LoadingHeap, 3);
            var maximumRelevantTypes = DetermineMaximumRelevantTypeMethodTables(typeDetails);
            var liveHeapTask = await LoadHeapAsync(LoadHeapMode.Live, maximumRelevantTypes).ConfigureAwait(false);
            var deadHeapTask = await LoadHeapAsync(LoadHeapMode.Dead, maximumRelevantTypes).ConfigureAwait(false);
            
            // string details needs heap, so wait for them...
            await liveHeapTask.ConfigureAwait(false);
            liveHeapEntries = liveHeapTask.Result;

            var stringTypeMethodTable = typeDetails["System.String"].Single();
            var stringHeapEntry = liveHeapEntries.First(x => stringTypeMethodTable == x.MethodTable);
            var stringDetailsTask = await LoadStringDetailsAsync(stringHeapEntry);

            // wait for everything that remains
            await Task.WhenAll(deadHeapTask, threadCountTask, stringDetailsTask).ConfigureAwait(false);

            deadHeapEntries = deadHeapTask.Result;
            threadCount = threadCountTask.Result;
            stringDetails = stringDetailsTask.Result;
        }

        internal static async ValueTask<DumpDiagnoser> CreateAsync(string dotnetDump, string dumpPath, int processCount, IProgress<DumpDiagnoserProgress> progress = null)
        {
            Debug.Assert(processCount > 0);

            var ret = new DumpDiagnoser(processCount, progress);
            await ret.StartProcessesAsync(dotnetDump, dumpPath).ConfigureAwait(false);
            await ret.LoadNeededStateAsync().ConfigureAwait(false);

            ret.SplitHeapByProcs();

            return ret;
        }
    }
}
