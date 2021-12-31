using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal interface IAnalyzer : IAsyncDisposable
    {
        IAsyncEnumerable<HeapEntry> LoadHeapAsync(LoadHeapMode mode);
        ValueTask<StringDetails> LoadStringDetailsAsync(HeapEntry stringEntry);
        ValueTask<int> CountActiveThreadsAsync();
        ValueTask<ImmutableList<AnalyzerStackFrame>> LoadStackTraceForThreadAsync(int threadIx);
        ValueTask<int> LoadStringLengthAsync(StringDetails stringType, HeapEntry stringEntry);
        ValueTask<string> LoadCharsAsync(long addr, int length);
        ValueTask<DelegateDetails> LoadDelegateDetailsAsync(HeapEntry entry);
        ValueTask<long> LoadEEClassAsync(long methodTable);
        ValueTask<EEClassDetails> LoadEEClassDetailsAsync(long eeClass);
        ValueTask<ArrayDetails> LoadArrayDetailsAsync(HeapEntry arr);
        ValueTask<ImmutableHashSet<long>> LoadUniqueMethodTablesAsync();
        ValueTask<TypeDetails?> LoadMethodTableTypeDetailsAsync(long methodTable);
        ValueTask<ImmutableArray<long>> LoadLongsAsync(long addr, int count);
        IAsyncEnumerable<AsyncStateMachineDetails> LoadAsyncStateMachinesAsync();
        ValueTask<ObjectInstanceDetails?> LoadObjectInstanceFieldsSpecificsAsync(long objectAddress);
        ValueTask<ImmutableList<HeapDetails>> LoadHeapDetailsAsync();
        ValueTask<ImmutableList<HeapGCHandle>> LoadGCHandlesAsync();
        ValueTask<HeapFragmentation> LoadHeapFragmentationAsync();
    }
}
