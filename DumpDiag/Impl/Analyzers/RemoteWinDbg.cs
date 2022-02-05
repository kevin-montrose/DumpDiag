using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal sealed class RemoteWinDbg : AnalyzerBase, IAnalyzer
    {
        private static readonly Encoding UnicodeWithoutBOM = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);

        private readonly RemoteWinDbgStream stream;

        private RemoteWinDbg(ArrayPool<char> arrayPool, RemoteWinDbgStream stream)
            : base(stream, UnicodeWithoutBOM, stream, UnicodeWithoutBOM, "\n", arrayPool)
        {
            this.stream = stream;
        }

        public IAsyncEnumerable<HeapEntry> LoadHeapAsync(LoadHeapMode mode)
        {
            bool live;
            Command command;
            switch (mode)
            {
                case LoadHeapMode.Live:
                    command = Command.CreateCommand("!dumpheap -live");
                    live = true;
                    break;
                case LoadHeapMode.Dead:
                    command = Command.CreateCommand("!dumpheap -dead");
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

            var command = SendCommand(Command.CreateCommandWithAddress("!do", stringEntry.Address));

            return AnalyzerCommonCompletions.LoadStringDetails_CompleteAsync(command, stringEntry);
        }

        public ValueTask<int> CountActiveThreadsAsync()
        {
            var command = SendCommand(Command.CreateCommand("~"));

            return AnalyzerCommonCompletions.CountActiveThreads_CompleteAsync(command);
        }

        public ValueTask<ImmutableList<AnalyzerStackFrame>> LoadStackTraceForThreadAsync(int threadIx)
        {
            var command = SendCommand(Command.CreateCommandWithCountAndSuffix("~", threadIx, "s"), Command.CreateCommand("!clrstack"));

            return AnalyzerCommonCompletions.LoadStackTraceForThread_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<int> LoadStringLengthAsync(StringDetails stringType, HeapEntry stringEntry)
        {
            var command = SendCommand(Command.CreateCommandWithAddressAndSuffix("dd /c 1", stringEntry.Address + stringType.LengthOffset, "L1"));

            return AnalyzerCommonCompletions.LoadStringLength_CompleteAsync(command);
        }

        public ValueTask<string> LoadCharsAsync(long addr, int length)
        {
            const int CHARS_PER_LINE = 65_535;
            const string COMMAND_PREFIX = "dw /c FFFF"; // must match CHARS_PER_LINE, in hex

            Debug.Assert(length >= 0);

            if (length == 0)
            {
                return new ValueTask<string>("");
            }

            // note that windbg has a maximum column count, so we might have to process multiple lines
            var command = SendCommand(Command.CreateCommandWithAddressAndHexCountSuffix(COMMAND_PREFIX, addr, length));

            return CompleteAsync(ArrayPool, command, length);

            static async ValueTask<string> CompleteAsync(
                ArrayPool<char> arrayPool,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command,
                int length
            )
            {
                var buffer = arrayPool.Rent(length);
                var bufferIndex = 0;

                var remainingLength = length;

                await foreach (var line in command.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    var seq = lineRef.GetSequence();

                    if (seq.IsEmpty)
                    {
                        continue;
                    }

                    var toReadThisLine = Math.Min(remainingLength, CHARS_PER_LINE);
                    if (!SequenceReaderHelper.TryParseWinDbgCharacters(seq, toReadThisLine, buffer.AsSpan()[bufferIndex..]))
                    {
                        throw new InvalidOperationException("Couldn't parse characters");
                    }

                    remainingLength -= toReadThisLine;
                    bufferIndex += toReadThisLine;
                }

                if (remainingLength != 0)
                {
                    throw new InvalidOperationException("Couldn't read characters");
                }

                var ret = new string(buffer.AsSpan()[0..bufferIndex]);

                arrayPool.Return(buffer);

                return ret;
            }
        }

        public ValueTask<DelegateDetails> LoadDelegateDetailsAsync(HeapEntry entry)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("!dumpdelegate", entry.Address));

            return AnalyzerCommonCompletions.LoadDelegateDetails_CompleteAsync(ArrayPool, command, entry);
        }

        public ValueTask<long> LoadEEClassAsync(long methodTable)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("!dumpmt", methodTable));

            return AnalyzerCommonCompletions.LoadEEClass_CompleteAsync(command);
        }

        public ValueTask<EEClassDetails> LoadEEClassDetailsAsync(long eeClass)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("!dumpclass", eeClass));

            return AnalyzerCommonCompletions.LoadEEClassDetails_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<ArrayDetails> LoadArrayDetailsAsync(HeapEntry arr)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("!dumparray", arr.Address));

            return AnalyzerCommonCompletions.LoadArrayDetails_CompleteAsync(command);
        }

        public IAsyncEnumerable<AsyncStateMachineDetails> LoadAsyncStateMachinesAsync()
        {
            var command = SendCommand(Command.CreateCommand("!dumpasync -completed"));

            return AnalyzerCommonCompletions.LoadAsyncStateMachines_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<ObjectInstanceDetails?> LoadObjectInstanceFieldsSpecificsAsync(long objectAddress)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("!dumpobj", objectAddress));

            return AnalyzerCommonCompletions.LoadObjectInstanceFieldsSpecifics_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<ImmutableList<HeapDetails>> LoadHeapDetailsAsync()
        {
            const string GENERATION_ZERO_START = "generation 0 starts at 0x";
            const string GENERATION_ONE_START = "generation 1 starts at 0x";
            const string GENERATION_TWO_START = "generation 2 starts at 0x";
            const string LOH_START = "Large object heap starts at 0x";
            const string POH_START = "Pinned object heap starts at 0x";

            var command = SendCommand(Command.CreateCommand("!eeheap -gc"));

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

                        if (SequenceReaderHelper.TryParseWinDbgHeapSegment(seq, out var beginAddr, out var allocatedSize))
                        {
                            cur.AddSegment(beginAddr, allocatedSize);
                            continue;
                        }
                        else if (SequenceReaderHelper.TryParseHeapSegment(seq, out beginAddr, out allocatedSize))
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
            var command = SendCommand(Command.CreateCommand("!gchandles"));

            return AnalyzerCommonCompletions.LoadGCHandles_CompleteAsync(ArrayPool, command);
        }

        public ValueTask<HeapFragmentation> LoadHeapFragmentationAsync()
        {
            var command = SendCommand(Command.CreateCommand("!gcheapstat"));

            return AnalyzerCommonCompletions.LoadHeapFragmentation_CompleteAsync(command);
        }

        public ValueTask<ImmutableHashSet<long>> LoadUniqueMethodTablesAsync()
        {
            var command = SendCommand(Command.CreateCommand("!dumpheap -stat"));

            return AnalyzerCommonCompletions.LoadUniqueMethodTables_CompleteAsync(command);
        }

        public ValueTask<ImmutableArray<long>> LoadLongsAsync(long addr, int count)
        {
            var command = SendCommand(Command.CreateCommandWithAddressAndHexCountSuffix("dq /c FFFF", addr, count));

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

                    if (seq.IsEmpty)
                    {
                        continue;
                    }

                    if (!SequenceReaderHelper.TryParseWinDbgLongs(seq, builder))
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

        public ValueTask<TypeDetails?> LoadMethodTableTypeDetailsAsync(long methodTable)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("!dumpmt", methodTable));

            return AnalyzerCommonCompletions.LoadMethodTableTypeDetails_CompleteAsync(ArrayPool, command, methodTable);
        }

        public ValueTask<StringPeak> PeakStringAsync(StringDetails stringDetails, HeapEntry entry)
        {
            const string COMMAND = "dw /c 80";  // 80 == hex of StringPeak.PeakLength

            var startAt = entry.Address + stringDetails.LengthOffset;

            var command = SendCommand(Command.CreateCommandWithAddressAndHexCountSuffix(COMMAND, startAt, StringPeak.PeakLength));

            return CompleteAsync(this, command);

            static async ValueTask<StringPeak> CompleteAsync(
                RemoteWinDbg self,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
            )
            {
                var length = 0;
                var isFirstLine = true;
                string? peakedString = null;
                StringBuilder? builder = null;

                var remainingLength = 0;

                await foreach (var line in command.ConfigureAwait(false))
                {
                    using var lineRef = line;
                    var seq = lineRef.GetSequence();

                    if (seq.IsEmpty)
                    {
                        continue;
                    }

                    if (isFirstLine)
                    {
                        if (!SequenceReaderHelper.TryParsePeakStringFirstLineWinDbg(self.ArrayPool, seq, out length, out peakedString))
                        {
                            throw new Exception($"Could not peak string, shouldn't be possible");
                        }

                        isFirstLine = false;

                        if (peakedString.Length != length)
                        {
                            builder = self.StringBuilders.Obtain();
                            builder.Append(peakedString);

                            remainingLength = length - peakedString.Length;
                        }
                    }
                    else if (builder != null)
                    {
                        if (!SequenceReaderHelper.TryParsePeakStringLaterLinesWinDbg(self.ArrayPool, seq, remainingLength, out var nextString))
                        {
                            throw new Exception($"Could not peak string, shouldn't be possible");
                        }

                        builder.Append(nextString);
                        remainingLength -= nextString.Length;
                    }
                }

                string? toRetString;
                if (builder != null)
                {
                    toRetString = builder.ToString();
                    builder.Clear();
                    self.StringBuilders.Return(builder);
                }
                else
                {
                    toRetString = peakedString;
                }

                if (toRetString == null)
                {
                    throw new Exception("Shouldn't be possible");
                }

                return new StringPeak(length, toRetString);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await DisposeInnerAsync().ConfigureAwait(false);

            await stream.DisposeAsync().ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows")]
        internal static async ValueTask<RemoteWinDbg> CreateAsync(ArrayPool<char> arrayPool, DebugConnectWideThunk connectWideThunk, string ip, ushort port, TimeSpan timeout)
        {
            var innerStream = await RemoteWinDbgStream.CreateAsync(connectWideThunk, ip, port, timeout).ConfigureAwait(false);

            var ret = new RemoteWinDbg(arrayPool, innerStream);
            await ret.StartAsync().ConfigureAwait(false);

            return ret;
        }
    }
}
