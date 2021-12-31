using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal static class AnalyzerCommonCompletions
    {
        /// <summary>
        /// Shared between <see cref="DotNetDumpAnalyzerProcess"/> and <see cref="RemoteWinDbg"/>'s LoadHeap(Live|Dead)Async.
        /// </summary>
        internal static async IAsyncEnumerable<HeapEntry> LoadHeap_CompleteAsync(
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes,
            bool live
        )
        {
            // first there are some headers and maybe warning messages, so some state to track that
            var fetching = false;

            // eventually there's a summary, so we track some state to know to stop then
            var doneFetching = false;

            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;   // free the line after we parse it

                if (doneFetching)
                {
                    // we're done, just need to fully enumerate and free everything
                    continue;
                }

                var seq = lineRef.GetSequence();

                if (!SequenceReaderHelper.TryParseHeapEntry(seq, live, out var entry, out var free))
                {
                    if (fetching)
                    {
                        doneFetching = true;
                        fetching = false;
                    }

                    continue;
                }
                else
                {
                    fetching = true;
                }

                if (!free)
                {
                    yield return entry;
                }
            }
        }

        internal static async ValueTask<StringDetails> LoadStringDetails_CompleteAsync(
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command,
            HeapEntry stringEntry
        )
        {
            const string STRING_LENGTH_FIELD_NAME = "_stringLength";
            const string FIRST_CHAR_FIELD_NAME = "_firstChar";

            int? length = null;
            int? firstChar = null;

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();
                if (!SequenceReaderHelper.TryParseFieldOffset(seq, out var fieldOffset))
                {
                    continue;
                }

                if (fieldOffset.Name.Equals(STRING_LENGTH_FIELD_NAME.AsSpan(), StringComparison.Ordinal))
                {
                    Debug.Assert(length == null);

                    length = fieldOffset.Offset;
                }
                else if (fieldOffset.Name.Equals(FIRST_CHAR_FIELD_NAME.AsSpan(), StringComparison.Ordinal))
                {
                    Debug.Assert(firstChar == null);

                    firstChar = fieldOffset.Offset;
                }

                // we can't terminate early, because we need to fully enumerate the enumerator
            }

            if (length == null || firstChar == null)
            {
                throw new InvalidOperationException("No live strings found in process dump, which is practically impossible");
            }

            return new StringDetails(stringEntry.MethodTable, length.Value, firstChar.Value);
        }

        internal static async ValueTask<int> CountActiveThreads_CompleteAsync(BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command)
        {
            var ret = 0;
            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();
                if (!seq.IsEmpty)
                {
                    ret++;
                }
            }

            return ret;
        }

        internal static async ValueTask<ImmutableList<AnalyzerStackFrame>> LoadStackTraceForThread_CompleteAsync(
            ArrayPool<char> arrayPool,
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
        )
        {
            var ret = ImmutableList.CreateBuilder<AnalyzerStackFrame>();

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();
                if (!SequenceReaderHelper.TryParseStackFrame(seq, arrayPool, out var frame))
                {
                    continue;
                }

                ret.Add(frame);
            }

            return ret.ToImmutable();
        }

        internal static async ValueTask<int> LoadStringLength_CompleteAsync(
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
        )
        {
            int? length = null;

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();
                if (length == null && SequenceReaderHelper.TryParseStringLength(seq, out var lengthValue))
                {
                    length = lengthValue;
                }
            }

            if (length == null)
            {
                throw new InvalidOperationException($"Could not determine length for string");
            }

            return length.Value;
        }

        internal static async ValueTask<DelegateDetails> LoadDelegateDetails_CompleteAsync(
                ArrayPool<char> arrayPool,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command,
                HeapEntry entry
            )
        {
            var mtdDetails = ImmutableArray.CreateBuilder<DelegateMethodDetails>();

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();

                if (SequenceReaderHelper.TryParseDelegateMethodDetails(seq, arrayPool, out var details))
                {
                    mtdDetails.Add(details);
                }
            }

            if (mtdDetails.Count == 0)
            {
                throw new InvalidOperationException("Couldn't read delegate");
            }

            return new DelegateDetails(entry, mtdDetails.ToImmutable());
        }

        internal static async ValueTask<long> LoadEEClass_CompleteAsync(
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
        )
        {
            long? eeClass = null;

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                if (eeClass == null)
                {
                    var seq = lineRef.GetSequence();
                    if (SequenceReaderHelper.TryParseEEClass(seq, out var ee))
                    {
                        eeClass = ee;
                    }
                }
            }

            if (eeClass == null)
            {
                throw new InvalidOperationException("Could not determine EE class");
            }

            return eeClass.Value;
        }

        internal static async ValueTask<EEClassDetails> LoadEEClassDetails_CompleteAsync(
            ArrayPool<char> arrayPool,
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
        )
        {
            string? className = null;
            long? parentEEClass = null;

            var instanceFields = ImmutableList.CreateBuilder<InstanceField>();

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();

                if (className == null && SequenceReaderHelper.TryParseClassName(seq, arrayPool, out className))
                {
                    continue;
                }
                else if (parentEEClass == null && SequenceReaderHelper.TryParseParentClass(seq, out var parent))
                {
                    parentEEClass = parent;
                }
                else if (SequenceReaderHelper.TryParseInstanceFieldNoValue(seq, arrayPool, out var field))
                {
                    instanceFields.Add(field);
                }
            }

            if (className == null)
            {
                throw new InvalidOperationException("Couldn't determine class name");
            }

            if (parentEEClass == null)
            {
                throw new InvalidOperationException("Couldn't determine parent class");
            }

            return new EEClassDetails(className, parentEEClass.Value, instanceFields.ToImmutable());
        }

        internal static async ValueTask<ArrayDetails> LoadArrayDetails_CompleteAsync(
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
            )
        {
            long? addr = null;
            int? len = null;

            await foreach (var line in command.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = addr == null || len == null ? lineRef.GetSequence() : default;

                if (addr == null && SequenceReaderHelper.TryParseArrayAddress(seq, out var addrParsed))
                {
                    addr = addrParsed;
                    continue;
                }

                if (len == null && SequenceReaderHelper.TryParseArrayLength(seq, out var lenParsed))
                {
                    len = lenParsed;
                }
            }

            if (len == null)
            {
                throw new InvalidOperationException("Couldn't determine character array length");
            }

            if (len > 0 && addr == null)
            {
                throw new InvalidOperationException("Couldn't determine character array address of non-empty array");
            }

            return new ArrayDetails(addr, len.Value);
        }

        internal static async IAsyncEnumerable<AsyncStateMachineDetails> LoadAsyncStateMachines_CompleteAsync(
            ArrayPool<char> arrayPool,
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes
        )
        {
            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();
                if (SequenceReaderHelper.TryParseAsyncStateMachineDetails(seq, arrayPool, out var details))
                {
                    yield return details;
                }
            }
        }

        internal static async ValueTask<ObjectInstanceDetails?> LoadObjectInstanceFieldsSpecifics_CompleteAsync(
            ArrayPool<char> arrayPool,
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes
        )
        {
            long? eeClass = null;
            long? methodTable = null;

            var hasFields = false;
            var fieldDetails = ImmutableList.CreateBuilder<InstanceFieldWithValue>();

            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();

                if (eeClass == null && SequenceReaderHelper.TryParseEEClass(seq, out var eeClassParsed))
                {
                    eeClass = eeClassParsed;
                }
                else if (methodTable == null && SequenceReaderHelper.TryParseMethodTable(seq, out var mtParsed))
                {
                    methodTable = mtParsed;
                }
                else if (seq.Equals("Fields:", StringComparison.Ordinal))
                {
                    hasFields = true;
                }
                else if (SequenceReaderHelper.TryParseInstanceFieldWithValue(seq, arrayPool, out var field))
                {
                    fieldDetails.Add(field);
                }
            }

            if (eeClass == null)
            {
                return null;
            }

            if (methodTable == null)
            {
                return null;
            }

            if (!hasFields)
            {
                return null;
            }

            return new ObjectInstanceDetails(eeClass.Value, methodTable.Value, fieldDetails.ToImmutable());
        }

        internal static async ValueTask<ImmutableList<HeapGCHandle>> LoadGCHandles_CompleteAsync(ArrayPool<char> arrayPool, BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes)
        {
            const string STATS_START = "Statistics:";

            var retBuilder = ImmutableList.CreateBuilder<HeapGCHandle>();
            var mtLookupBuilder = ImmutableDictionary.CreateBuilder<string, long>();

            var statsStarted = false;

            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = line.GetSequence();

                if (!statsStarted)
                {
                    if (seq.StartsWith(STATS_START, StringComparison.Ordinal))
                    {
                        statsStarted = true;
                        continue;
                    }
                    else if (SequenceReaderHelper.TryParseGCHandle(seq, arrayPool, out var handle))
                    {
                        retBuilder.Add(handle);
                    }
                }
                else
                {
                    if (SequenceReaderHelper.TryParseGCHandleStats(seq, arrayPool, out var mt, out var name))
                    {
                        // there might be a conflict here (two types can share a name, but be different)
                        // so remember that
                        if (mtLookupBuilder.ContainsKey(name))
                        {
                            mtLookupBuilder[name] = -1;
                        }
                        else
                        {
                            mtLookupBuilder.Add(name, mt);
                        }
                    }
                }
            }

            var mtLookup = mtLookupBuilder.ToImmutable();

            for (var i = 0; i < retBuilder.Count; i++)
            {
                var withoutMT = retBuilder[i];

                if (mtLookup.TryGetValue(withoutMT.TypeHint, out var mt) && mt != -1)
                {
                    var withMT = withoutMT.SetMethodTable(mt);
                    retBuilder[i] = withMT;
                }
            }

            var ret = retBuilder.ToImmutable();

            return ret;
        }

        internal static async ValueTask<HeapFragmentation> LoadHeapFragmentation_CompleteAsync(BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes)
        {
            const string START_FREE_SPACE = "Free space:";

            var inFreeSpace = false;

            var free = (Gen0: 0L, Gen1: 0L, Gen2: 0L, LOH: 0L, POH: 0L);
            var used = (Gen0: 0L, Gen1: 0L, Gen2: 0L, LOH: 0L, POH: 0L);

            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;

                var seq = lineRef.GetSequence();

                if (!inFreeSpace && seq.StartsWith(START_FREE_SPACE, StringComparison.Ordinal))
                {
                    inFreeSpace = true;
                    continue;
                }
                else if (SequenceReaderHelper.TryParseHeapSpace(seq, out var gen0, out var gen1, out var gen2, out var loh, out var poh))
                {
                    if (inFreeSpace)
                    {
                        UpdateTotals(gen0, gen1, gen2, loh, poh, ref free);
                    }
                    else
                    {
                        UpdateTotals(gen0, gen1, gen2, loh, poh, ref used);
                    }

                    continue;
                }
            }

            return
                new HeapFragmentation(
                    free.Gen0,
                    used.Gen0,
                    free.Gen1,
                    used.Gen1,
                    free.Gen2,
                    used.Gen2,
                    free.LOH,
                    used.LOH,
                    free.POH,
                    used.POH
                );

            static void UpdateTotals(long gen0, long gen1, long gen2, long loh, long poh, ref (long Gen0, long Gen1, long Gen2, long LOH, long POH) cur)
            {
                cur =
                    (
                        Gen0: cur.Gen0 + gen0,
                        Gen1: cur.Gen1 + gen1,
                        Gen2: cur.Gen2 + gen2,
                        LOH: cur.LOH + loh,
                        POH: cur.POH + poh
                    );
            }
        }

        internal static async ValueTask<ImmutableHashSet<long>> LoadUniqueMethodTables_CompleteAsync(
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes
            )
        {
            var ret = ImmutableHashSet.CreateBuilder<long>();

            // first there are some headers and maybe warning messages, so some state to track that
            var fetching = false;

            // eventually there's a summary, so we track some state to know to stop then
            var doneFetching = false;

            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;   // free the line after we parse it

                if (doneFetching)
                {
                    // we're done, just need to fully enumerate and free everything
                    continue;
                }

                var seq = lineRef.GetSequence();

                if (!SequenceReaderHelper.TryParseHeapEntry(seq, false, out var entry, out var free))
                {
                    if (fetching)
                    {
                        doneFetching = true;
                        fetching = false;
                    }

                    continue;
                }
                else
                {
                    fetching = true;
                }

                if (!free)
                {
                    ret.Add(entry.MethodTable);
                }
            }

            return ret.ToImmutable();
        }

        internal static async ValueTask<TypeDetails?> LoadMethodTableTypeDetails_CompleteAsync(
            ArrayPool<char> arrayPool,
            BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes,
            long methodTable
        )
        {
            string? name = null;

            await foreach (var line in commandRes.ConfigureAwait(false))
            {
                using var lineRef = line;   // free the line after we parse it

                var seq = lineRef.GetSequence();
                if (name == null && SequenceReaderHelper.TryParseTypeName(seq, arrayPool, out var nameParsed))
                {
                    name = nameParsed;
                }
            }

            if (name == null)
            {
                return null;
            }

            return new TypeDetails(name, methodTable);
        }
    }
}
