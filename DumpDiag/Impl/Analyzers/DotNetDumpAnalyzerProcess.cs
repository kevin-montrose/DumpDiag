using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Represents a single instance of a dotnet-dump process with the analyze command.
    /// 
    /// While this type is not thread safe, once a method returns it is safe to call another method
    /// from a different thread.  This applies to async methods as well.
    /// </summary>
    internal sealed class DotNetDumpAnalyzerProcess : AnalyzerBase, IAnalyzer
    {
        private readonly Process process;

        private DotNetDumpAnalyzerProcess(Process proc, ArrayPool<char> arrayPool) 
            : base(proc.StandardInput.BaseStream, proc.StandardInput.Encoding, proc.StandardOutput.BaseStream, proc.StandardOutput.CurrentEncoding, Environment.NewLine, arrayPool)
        {
            process = proc;
        }

        public override ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                // send an exit command and then cleanup
                var command = SendCommand(Command.CreateCommand("exit"));
                return CompleteAsync(this, command);
            }

            return DisposeInnerAsync();

            static async ValueTask CompleteAsync(DotNetDumpAnalyzerProcess self, BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command)
            {
                await foreach (var line in command.ConfigureAwait(false))
                {
                    line.Dispose();
                }

                await self.process.WaitForExitAsync().ConfigureAwait(false);
                await self.DisposeInnerAsync().ConfigureAwait(false);
            }
        }

        public IAsyncEnumerable<HeapEntry> LoadHeapAsync(LoadHeapMode mode)
        {
            bool live;
            Command command;
            switch (mode)
            {
                case LoadHeapMode.Live:
                    command = Command.CreateCommand("dumpheap -live");
                    live = true;
                    break;
                case LoadHeapMode.Dead:
                    command = Command.CreateCommand("dumpheap -dead");
                    live = false;
                    break;
                default: throw new ArgumentException(nameof(mode));
            }

            var commandRes = SendCommand(command);

            return AnalyzerCommonCompletions.LoadHeap_CompleteAsync(commandRes, live);
        }

        public ValueTask<StringDetails> LoadStringDetailsAsync(HeapEntry stringEntry)
        {
            Debug.Assert(stringEntry.Live);

            var command = SendCommand(Command.CreateCommandWithAddress("do", stringEntry.Address));

            return AnalyzerCommonCompletions.LoadStringDetails_CompleteAsync(command, stringEntry);
        }

        public ValueTask<int> CountActiveThreadsAsync()
        {
            var command = SendCommand(Command.CreateCommand("threads"));

            return AnalyzerCommonCompletions.CountActiveThreads_CompleteAsync(command);
        }

        public ValueTask<ImmutableList<AnalyzerStackFrame>> LoadStackTraceForThreadAsync(int threadIx)
        {
            var command = SendCommand(Command.CreateCommandWithCount("threads", threadIx), Command.CreateCommand("clrstack"));

            return AnalyzerCommonCompletions.LoadStackTraceForThread_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<int> LoadStringLengthAsync(StringDetails stringType, HeapEntry stringEntry)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dd -c 1", stringEntry.Address + stringType.LengthOffset));

            return AnalyzerCommonCompletions.LoadStringLength_CompleteAsync(command);
        }

        public ValueTask<string> LoadCharsAsync(long addr, int length)
        {
            Debug.Assert(length >= 0);

            if (length == 0)
            {
                return new ValueTask<string>("");
            }

            var command = SendCommand(Command.CreateCommandWithCountAndAdress("dw -w", length, "-c", addr));

            return CompleteAsync(this, command, length);

            static async ValueTask<string> CompleteAsync(
                DotNetDumpAnalyzerProcess self,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command,
                int length
            )
            {
                string? ret = null;

                await foreach (var line in command.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    var seq = lineRef.GetSequence();

                    if (ret == null)
                    {
                        if (!SequenceReaderHelper.TryParseCharacters(seq, length, self.ArrayPool, out ret))
                        {
                            throw new InvalidOperationException("Couldn't parse characters");
                        }
                    }
                }

                if (ret == null)
                {
                    throw new InvalidOperationException("Couldn't read characters");
                }

                return ret;
            }
        }

        public ValueTask<DelegateDetails> LoadDelegateDetailsAsync(HeapEntry entry)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpdelegate", entry.Address));

            return AnalyzerCommonCompletions.LoadDelegateDetails_CompleteAsync(ArrayPool, command, entry);
        }

        public ValueTask<long> LoadEEClassAsync(long methodTable)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpmt", methodTable));

            return AnalyzerCommonCompletions.LoadEEClass_CompleteAsync(command);
        }

        public ValueTask<EEClassDetails> LoadEEClassDetailsAsync(long eeClass)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpclass", eeClass));

            return AnalyzerCommonCompletions.LoadEEClassDetails_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<ArrayDetails> LoadArrayDetailsAsync(HeapEntry arr)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumparray", arr.Address));

            return AnalyzerCommonCompletions.LoadArrayDetails_CompleteAsync(command);
        }

        public ValueTask<ImmutableHashSet<long>> LoadUniqueMethodTablesAsync()
        {
            var command = SendCommand(Command.CreateCommand("dumpheap"));

            return AnalyzerCommonCompletions.LoadUniqueMethodTables_CompleteAsync(command);
        }

        public ValueTask<TypeDetails?> LoadMethodTableTypeDetailsAsync(long methodTable)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpmt", methodTable));

            return AnalyzerCommonCompletions.LoadMethodTableTypeDetails_CompleteAsync(ArrayPool, command, methodTable);
        }

        public ValueTask<ImmutableArray<long>> LoadLongsAsync(long addr, int count)
        {
            var command = SendCommand(Command.CreateCommandWithCountAndAdress("dq -c", count, " -w ", addr));

            return CompleteAsync(command, count);

            static async ValueTask<ImmutableArray<long>> CompleteAsync(
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes,
                int count
            )
            {
                var builder = ImmutableArray.CreateBuilder<long>(count);

                await foreach (var line in commandRes.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    var seq = lineRef.GetSequence();
                    if (!SequenceReaderHelper.TryParseLongs(seq, builder))
                    {
                        throw new InvalidOperationException("Couldn't read longs");
                    }
                }

                if (builder.Count != count)
                {
                    throw new InvalidOperationException($"Unxpected number of longs read, found {builder.Count} but expected {count}");
                }

                return builder.ToImmutable();
            }
        }

        public IAsyncEnumerable<AsyncStateMachineDetails> LoadAsyncStateMachinesAsync()
        {
            var command = SendCommand(Command.CreateCommand("dumpasync -completed"));

            return AnalyzerCommonCompletions.LoadAsyncStateMachines_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<ObjectInstanceDetails?> LoadObjectInstanceFieldsSpecificsAsync(long objectAddress)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpobj", objectAddress));

            return AnalyzerCommonCompletions.LoadObjectInstanceFieldsSpecifics_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<ImmutableList<HeapDetails>> LoadHeapDetailsAsync()
        {
            const string GENERATION_ZERO_START = "generation 0 starts at 0x";
            const string GENERATION_ONE_START = "generation 1 starts at 0x";
            const string GENERATION_TWO_START = "generation 2 starts at 0x";
            const string LOH_START = "Large object heap starts at 0x";
            const string POH_START = "Pinned object heap starts at 0x";

            var command = SendCommand(Command.CreateCommand("eeheap -gc"));

            return CompleteAsync(command);

            static async ValueTask<ImmutableList<HeapDetails>> CompleteAsync(BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes)
            {
                var ret = ImmutableList.CreateBuilder<HeapDetails>();

                HeapDetailsBuilder cur = new HeapDetailsBuilder();

                await foreach (var line in commandRes.ConfigureAwait(false))
                {
                    using var lineRef = line;
                    var seq = lineRef.GetSequence();

                    if (seq.StartsWith(GENERATION_ZERO_START, StringComparison.Ordinal))
                    {
                        cur.Start(ret.Count);

                        var partial = seq.Slice(GENERATION_ZERO_START.Length);
                        if (!partial.TryParseHexLong(out var gen0Start))
                        {
                            throw new Exception("Should not be possible, gen0 start couldn't be parsed");
                        }

                        cur.Gen0Start = gen0Start;
                        continue;
                    }
                    else if (cur.IsStarted && !cur.GenerationsFinished && seq.StartsWith(GENERATION_ONE_START, StringComparison.Ordinal))
                    {
                        var partial = seq.Slice(GENERATION_ONE_START.Length);
                        if (!partial.TryParseHexLong(out var gen1Start))
                        {
                            throw new Exception("Should not be possible, gen1 start couldn't be parsed");
                        }

                        cur.Gen1Start = gen1Start;
                        continue;
                    }
                    else if (cur.IsStarted && !cur.GenerationsFinished && seq.StartsWith(GENERATION_TWO_START, StringComparison.Ordinal))
                    {
                        var partial = seq.Slice(GENERATION_TWO_START.Length);
                        if (!partial.TryParseHexLong(out var gen2Start))
                        {
                            throw new Exception("Should not be possible, gen2 start couldn't be parsed");
                        }

                        cur.Gen2Start = gen2Start;

                        cur.StartSmallObjectHeap();
                        continue;
                    }
                    else if (cur.IsStarted && cur.GenerationsFinished)
                    {
                        if (seq.StartsWith(LOH_START, StringComparison.Ordinal))
                        {
                            cur.StartLargeObjectHeap();
                            continue;
                        }
                        else if (seq.StartsWith(POH_START, StringComparison.Ordinal))
                        {
                            cur.StartPinnedObjectHeap();
                            continue;
                        }

                        if (SequenceReaderHelper.TryParseHeapSegment(seq, out var beginAddr, out var allocatedSize))
                        {
                            cur.AddSegment(beginAddr, allocatedSize);
                            continue;
                        }
                    }

                    if (cur.IsStarted && SequenceReaderHelper.IsSectionBreak(seq))
                    {
                        ret.Add(cur.ToHeapDetails());

                        cur = new HeapDetailsBuilder();
                    }
                }

                if (cur.IsStarted)
                {
                    ret.Add(cur.ToHeapDetails());
                }

                return ret.ToImmutable();
            }
        }

        public ValueTask<ImmutableList<HeapGCHandle>> LoadGCHandlesAsync()
        {
            var command = SendCommand(Command.CreateCommand("gchandles"));

            return AnalyzerCommonCompletions.LoadGCHandles_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<HeapFragmentation> LoadHeapFragmentationAsync()
        {
            var command = SendCommand(Command.CreateCommand("gcheapstat"));

            return AnalyzerCommonCompletions.LoadHeapFragmentation_CompleteAsync(command);
        }

        /// <summary>
        /// Create a new <see cref="DotNetDumpAnalyzerProcess"/> given a path to dotnet-dump and an argument list to pass.
        /// </summary>
        internal static async ValueTask<DotNetDumpAnalyzerProcess> CreateAsync(ArrayPool<char> arrayPool, string dotNetDumpExecutable, string dumpFile)
        {
            var info = new ProcessStartInfo();
            info.FileName = dotNetDumpExecutable;
            info.UseShellExecute = false;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.CreateNoWindow = true;
            info.Arguments = $"analyze \"{dumpFile}\"";

            var proc = Process.Start(info);

            if (proc == null)
            {
                throw new Exception("Could not start AnalyzerProcess, this shouldn't be possible");
            }

            Job.Instance.AssociateProcess(proc);

            var ret = new DotNetDumpAnalyzerProcess(proc, arrayPool);
            await ret.StartAsync();

            return ret;
        }
    }
}
