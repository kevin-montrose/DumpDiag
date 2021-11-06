﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ThreadState = System.Threading.ThreadState;

namespace DumpDiag.Impl
{
    // todo: naming in here is really inconsistent, fix that up?

    /// <summary>
    /// Represents a single instance of a dotnet-dump process with the analyze command.
    /// 
    /// While this type is not thread safe, once a method returns it is safe to call another method
    /// from a different thread.  This applies to async methods as well.
    /// </summary>
    internal sealed class AnalyzerProcess : IAsyncDisposable
    {
        private sealed class Message : IDisposable
        {
            private static readonly ThreadAffinitizedObjectPool<Message> ObjectPool = new ThreadAffinitizedObjectPool<Message>();

            internal Command FirstCommand { get; private set; }
            internal Command? SecondCommand { get; private set; }

            private BoundedSharedChannel<OwnedSequence<char>>? _response;
            internal BoundedSharedChannel<OwnedSequence<char>> Response
            {
                get
                {
                    if (_response == null)
                    {
                        throw new InvalidOperationException("Attempted to use uninitialized Message");
                    }

                    return _response;
                }
            }

            [Obsolete("Do not use this directly, it's only provided for internal pooling purposes")]
            public Message() { }

            private void Initialize(Command firstCommand, Command? secondCommand, BoundedSharedChannel<OwnedSequence<char>> response)
            {
                FirstCommand = firstCommand;
                SecondCommand = secondCommand;
                _response = response;
            }

            internal static Message Create(Command firstCommand, Command? secondCommand, BoundedSharedChannel<OwnedSequence<char>> response)
            {
                var ret = ObjectPool.Obtain();
                ret.Initialize(firstCommand, secondCommand, response);

                return ret;
            }

            public void Dispose()
            {
                FirstCommand = default;
                SecondCommand = null;
                _response = null;

                ObjectPool.Return(this);
            }
        }

        private readonly Process process;
        private readonly ArrayPool<char> arrayPool;

        private readonly Thread thread;
        private readonly SemaphoreSlim threadReadySignal;

        private readonly SemaphoreSlim messageReadySignal;
        private Message? message;

        private bool disposed;

        private readonly Stream underlying;

        private AnalyzerProcess(Process proc, ArrayPool<char> arrayPool)
        {
            process = proc;
            this.arrayPool = arrayPool;

            thread = new Thread(ThreadLoop);
            thread.Name = $"{nameof(AnalyzerProcess)}.{nameof(ThreadLoop)} for {proc.Id}";
            threadReadySignal = new SemaphoreSlim(0);
            messageReadySignal = new SemaphoreSlim(0);

#if DEBUG
            underlying = new EchoTextStream(process.StandardOutput.BaseStream, process.StandardOutput.CurrentEncoding);
#else
            underlying = process.StandardOutput.BaseStream;
#endif

            disposed = false;
        }

        private ValueTask StartAsync()
        {
            if (thread.ThreadState != ThreadState.Unstarted)
            {
                throw new InvalidOperationException("Double start");
            }

            thread.Start();

            return new ValueTask(threadReadySignal.WaitAsync());
        }

        private void ThreadLoop()
        {
            const string EndCommandOutput = "<END_COMMAND_OUTPUT>";
            const string PromptStart = "> ";

            using var reader = new ProcessStreamReader(arrayPool, underlying, process.StandardOutput.CurrentEncoding, Environment.NewLine);

            var endCommandSpan = EndCommandOutput.AsSpan();
            var promptStartSpan = PromptStart.AsSpan();

            var processInput = process.StandardInput;

            var e = reader.ReadAllLines().GetEnumerator();
            try
            {
                while (e.MoveNext())
                {
                    var line = e.Current;
                    var asSequence = line.GetSequence();
                    if (asSequence.Equals(endCommandSpan, StringComparison.Ordinal))
                    {
                        line.Dispose();
                        break;
                    }

                    line.Dispose();
                }

                threadReadySignal.Release();

                // start processesing requests
                while (true)
                {
                    messageReadySignal.Wait();
                    using var message = Interlocked.Exchange(ref this.message, null);

                    if (message == null)
                    {
                        break;
                    }

                    message.FirstCommand.Write(processInput);
                    PushCommandResults(ref e, message.Response, endCommandSpan, promptStartSpan);

                    if (message.SecondCommand != null)
                    {
                        message.SecondCommand.Value.Write(processInput);
                        PushCommandResults(ref e, message.Response, endCommandSpan, promptStartSpan);
                    }

                    message.Response.Complete();
                }
            }
            finally
            {
                e.Dispose();
            }

            static void PushCommandResults(ref ProcessStreamReader.Enumerator e, BoundedSharedChannel<OwnedSequence<char>> writer, ReadOnlySpan<char> endCommand, ReadOnlySpan<char> promptStart)
            {
                var readPrompt = false;

                while (e.MoveNext())
                {
                    var line = e.Current;

                    var asSequence = line.GetSequence();

                    // skip the prompt line...
                    if (!readPrompt && asSequence.StartsWith(promptStart, StringComparison.Ordinal))
                    {
                        line.Dispose();
                        readPrompt = true;
                        continue;
                    }

                    // stop once we see the magic end command string
                    if (asSequence.Equals(endCommand, StringComparison.Ordinal))
                    {
                        line.Dispose();
                        break;
                    }

                    writer.Append(line);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Debug.Assert(!disposed);

            if (thread.ThreadState == ThreadState.Unstarted)
            {
                // never started or already disposed
                disposed = true;
                return default;
            }
            else if (!process.HasExited)
            {
                // send an exit command and then cleanup
                var command = SendCommand(Command.CreateCommand("exit"));
                disposed = true;
                return CompleteAsync(this, command);
            }

            disposed = true;
            CommonCompleteSync(this);

            return default;

            static async ValueTask CompleteAsync(AnalyzerProcess self, BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command)
            {
                await foreach (var line in command.ConfigureAwait(false))
                {
                    line.Dispose();
                }

                await self.process.WaitForExitAsync();

                CommonCompleteSync(self);
            }

            static void CommonCompleteSync(AnalyzerProcess self)
            {
                self.process.Dispose();

                Interlocked.Exchange(ref self.message, null);
                self.messageReadySignal.Release();

                self.thread.Join();

                self.threadReadySignal.Dispose();
                self.messageReadySignal.Dispose();
            }
        }

        internal BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable SendCommand(Command command, Command? secondCommand = null)
        {
            Debug.Assert(!disposed);

            var response = BoundedSharedChannel<OwnedSequence<char>>.Create();

            var msg = Message.Create(command, secondCommand, response);

            var oldMsg = Interlocked.Exchange(ref message, msg);
            Debug.Assert(oldMsg == null, $"Old message was: {oldMsg}, new message is {msg}");
            messageReadySignal.Release();

            return response.ReadUntilCompletedAsync();
        }

        internal IAsyncEnumerable<HeapEntry> LoadHeapAsync(LoadHeapMode mode)
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

            return CompleteAsync(commandRes, live);

            static async IAsyncEnumerable<HeapEntry> CompleteAsync(
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
        }

        internal ValueTask<StringDetails> LoadStringDetailsAsync(HeapEntry stringEntry)
        {
            Debug.Assert(stringEntry.Live);

            var command = SendCommand(Command.CreateCommandWithAddress("do", stringEntry.Address));

            return CompleteAsync(command, stringEntry);

            static async ValueTask<StringDetails> CompleteAsync(
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
        }

        internal ValueTask<int> CountActiveThreadsAsync()
        {
            var command = SendCommand(Command.CreateCommand("threads"));

            return CompleteAsync(command);

            static async ValueTask<int> CompleteAsync(BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command)
            {
                var ret = 0;
                await foreach (var line in command.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    ret++;
                }

                return ret;
            }
        }

        internal ValueTask<ImmutableList<AnalyzerStackFrame>> GetStackTraceForThreadAsync(int threadIx)
        {
            var command = SendCommand(Command.CreateCommandWithCount("threads", threadIx), Command.CreateCommand("clrstack"));

            return CompleteAsync(this, command);

            static async ValueTask<ImmutableList<AnalyzerStackFrame>> CompleteAsync(
                AnalyzerProcess self,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command
            )
            {
                var ret = ImmutableList.CreateBuilder<AnalyzerStackFrame>();

                await foreach (var line in command.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    var seq = lineRef.GetSequence();
                    if (!SequenceReaderHelper.TryParseStackFrame(seq, self.arrayPool, out var frame))
                    {
                        continue;
                    }

                    ret.Add(frame);
                }

                return ret.ToImmutable();
            }
        }

        internal ValueTask<int> GetStringLengthAsync(StringDetails stringType, HeapEntry stringEntry)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dd -c 1", stringEntry.Address + stringType.LengthOffset));

            return CompleteAsync(command);

            static async ValueTask<int> CompleteAsync(
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
        }

        internal ValueTask<string> ReadCharsAsync(long addr, int length)
        {
            Debug.Assert(length >= 0);

            if (length == 0)
            {
                return new ValueTask<string>("");
            }

            var command = SendCommand(Command.CreateCommandWithCountAndAdress("dw -w", length, "-c", addr));

            return CompleteAsync(this, command, length);

            static async ValueTask<string> CompleteAsync(
                AnalyzerProcess self,
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
                        if (!SequenceReaderHelper.TryParseCharacters(seq, length, self.arrayPool, out ret))
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

        internal ValueTask<DelegateDetails> ReadDelegateDetailsAsync(HeapEntry entry)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpdelegate", entry.Address));

            return CompleteAsync(this, command, entry);

            static async ValueTask<DelegateDetails> CompleteAsync(
                AnalyzerProcess self,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable command,
                HeapEntry entry
            )
            {
                var mtdDetails = ImmutableArray.CreateBuilder<DelegateMethodDetails>();

                await foreach (var line in command.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    var seq = lineRef.GetSequence();

                    if (SequenceReaderHelper.TryParseDelegateMethodDetails(seq, self.arrayPool, out var details))
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
        }

        internal ValueTask<long> LoadEEClassAsync(long methodTable)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpmt", methodTable));

            return CompleteAsync(command);

            static async ValueTask<long> CompleteAsync(
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
        }

        internal ValueTask<EEClassDetails> LoadEEClassDetailsAsync(long eeClass)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpclass", eeClass));

            return CompleteAsync(this, command);

            static async ValueTask<EEClassDetails> CompleteAsync(
                AnalyzerProcess self,
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

                    if (className == null && SequenceReaderHelper.TryParseClassName(seq, self.arrayPool, out className))
                    {
                        continue;
                    }
                    else if (parentEEClass == null && SequenceReaderHelper.TryParseParentClass(seq, out var parent))
                    {
                        parentEEClass = parent;
                    }
                    else if (SequenceReaderHelper.TryParseInstanceFieldNoValue(seq, self.arrayPool, out var field))
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
        }

        internal ValueTask<ArrayDetails> ReadArrayDetailsAsync(HeapEntry arr)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumparray", arr.Address));

            return CompleteAsync(command);

            static async ValueTask<ArrayDetails> CompleteAsync(
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
        }

        internal ValueTask<ImmutableHashSet<long>> LoadUniqueMethodTablesAsync()
        {
            var command = SendCommand(Command.CreateCommand("dumpheap"));

            return CompleteAsync(command);

            static async ValueTask<ImmutableHashSet<long>> CompleteAsync(
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
        }

        internal ValueTask<TypeDetails?> ReadMethodTableTypeDetailsAsync(long methodTable)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpmt", methodTable));

            return CompleteAsync(this, command, methodTable);

            static async ValueTask<TypeDetails?> CompleteAsync(
                AnalyzerProcess self,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes,
                long methodTable
            )
            {
                string? name = null;

                await foreach (var line in commandRes.ConfigureAwait(false))
                {
                    using var lineRef = line;   // free the line after we parse it

                    var seq = lineRef.GetSequence();
                    if (name == null && SequenceReaderHelper.TryParseTypeName(seq, self.arrayPool, out var nameParsed))
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

        internal ValueTask<ImmutableArray<long>> ReadLongsAsync(long addr, int count)
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

        internal IAsyncEnumerable<AsyncStateMachineDetails> LoadAsyncStateMachinesAsync()
        {
            var command = SendCommand(Command.CreateCommand("dumpasync -completed"));

            return CompleteAsync(this, command);

            static async IAsyncEnumerable<AsyncStateMachineDetails> CompleteAsync(
                AnalyzerProcess self,
                BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes
            )
            {
                await foreach (var line in commandRes.ConfigureAwait(false))
                {
                    using var lineRef = line;

                    var seq = lineRef.GetSequence();
                    if (SequenceReaderHelper.TryParseAsyncStateMachineDetails(seq, self.arrayPool, out var details))
                    {
                        yield return details;
                    }
                }
            }
        }

        internal ValueTask<ObjectInstanceDetails?> LoadObjectInstanceFieldsSpecificsAsync(long objectAddress)
        {
            var command = SendCommand(Command.CreateCommandWithAddress("dumpobj", objectAddress));

            return CompleteAsync(this, command);

            static async ValueTask<ObjectInstanceDetails?> CompleteAsync(
                AnalyzerProcess self,
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
                    else if (SequenceReaderHelper.TryParseInstanceFieldWithValue(seq, self.arrayPool, out var field))
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
        }

        internal ValueTask<ImmutableList<HeapDetails>> LoadHeapDetailsAsync()
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

        internal ValueTask<ImmutableList<HeapGCHandle>> LoadGCHandlesAsync()
        {
            var command = SendCommand(Command.CreateCommand("gchandles"));

            return CompleteAsync(this, command);

            static async ValueTask<ImmutableList<HeapGCHandle>> CompleteAsync(AnalyzerProcess self, BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes)
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
                        else if (SequenceReaderHelper.TryParseGCHandle(seq, self.arrayPool, out var handle))
                        {
                            retBuilder.Add(handle);
                        }
                    }
                    else
                    {
                        if (SequenceReaderHelper.TryParseGCHandleStats(seq, self.arrayPool, out var mt, out var name))
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
        }

        internal ValueTask<HeapFragmentation> LoadHeapFragmentationAsync()
        {
            var command = SendCommand(Command.CreateCommand("gcheapstat"));

            return CompleteAsync(command);

            static async ValueTask<HeapFragmentation> CompleteAsync(BoundedSharedChannel<OwnedSequence<char>>.AsyncEnumerable commandRes)
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
        }

        /// <summary>
        /// Create a new <see cref="AnalyzerProcess"/> given a path to dotnet-dump and an argument list to pass.
        /// </summary>
        internal static async ValueTask<AnalyzerProcess> CreateAsync(ArrayPool<char> arrayPool, string dotNetDumpExecutable, string dumpFile)
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

            var ret = new AnalyzerProcess(proc, arrayPool);
            await ret.StartAsync();

            return ret;
        }
    }
}
