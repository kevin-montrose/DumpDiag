using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Helper which makes it efficient to read lines out of a process asynchronously.
    /// 
    /// Since basically all we do is issue commands and parse the response, we need this to be really efficient.
    /// </summary>
    public sealed class ProcessStreamReader : IDisposable
    {
        public readonly struct Enumerable : IEnumerable<OwnedSequence<char>>
        {
            private readonly ProcessStreamReader inner;

            internal Enumerable(ProcessStreamReader inner)
            {
                this.inner = inner;
            }

            public Enumerator GetEnumerator()
            => new Enumerator(inner);

            IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();

            IEnumerator<OwnedSequence<char>> IEnumerable<OwnedSequence<char>>.GetEnumerator()
            => GetEnumerator();
        }

        public struct Enumerator : IEnumerator<OwnedSequence<char>>
        {
            private enum Mode
            {
                /// <summary>
                /// First run, acquire needed resources.
                /// </summary>
                FirstRun,
                /// <summary>
                /// Read bytes into pending buffer, and convert what we can into the char buffer.
                /// </summary>
                StartLoop,
                /// <summary>
                /// Split the char buffer up into lines.
                /// </summary>
                ProcessCharsInLoop,
                /// <summary>
                /// Update state after we've processed chars into lines.
                /// 
                /// This is a separate step, as we might enter <see cref="ProcessCharsInLoop"/> multiple times
                /// but we only need to finish the loop once.
                /// </summary>
                FinishLoop,
                /// <summary>
                /// After we've finished reading bytes and converting characters, handle anything left in the buffer
                /// after we know no more line breaks are coming.
                /// </summary>
                HandleLeftOverCharsInBuffer,
                /// <summary>
                /// After all buffers are empty, if there's a pending sequence we need to return it.
                /// </summary>
                HandleLeftOverSequence,
                /// <summary>
                /// We're never going to return something else, so does any cleanup necessary.
                /// </summary>
                LastRun,
                /// <summary>
                /// State we enter after we've fully enumerated.
                /// </summary>
                Ended
            }

            private readonly ProcessStreamReader inner;

            private char[]? charBufferWhole;
            private Memory<char> charBuffer;
            private int readFromCharBufferIx;
            private int writeToCharBufferIx;

            private int startOfNewCharsIx;

            private RingBuffer byteBuffer;

            private OwnedSequence<char>? pendingSequenceHead;
            private OwnedSequence<char>? pendingSequenceTail;
            private bool savedSequenceForMultiCharNewLine;

            private bool streamFinished;
            private bool conversionFinished;

            private Mode mode;

            private OwnedSequence<char>? _current;
            public OwnedSequence<char> Current
            {
                get
                {
                    var ret = _current;
                    if (ret == null)
                    {
                        throw new InvalidOperationException("Accessed enumerator in invalid state");
                    }

                    return ret;
                }
            }
            object IEnumerator.Current => Current;

            internal Enumerator(ProcessStreamReader inner)
            {
                this.inner = inner;

                _current = default;

                charBufferWhole = null;
                charBuffer = Memory<char>.Empty;
                readFromCharBufferIx = 0;
                writeToCharBufferIx = 0;

                startOfNewCharsIx = 0;

                byteBuffer = default;
                pendingSequenceHead = pendingSequenceTail = null;
                savedSequenceForMultiCharNewLine = false;

                streamFinished = false;
                conversionFinished = false;

                mode = Mode.FirstRun;
            }

            public bool MoveNext()
            {
                while (true)
                {
                    switch (mode)
                    {
                        case Mode.FirstRun:
                            FirstRun(ref this);
                            break;
                        case Mode.StartLoop:
                            StartLoop(ref this);
                            break;
                        case Mode.ProcessCharsInLoop:
                            if (ProcessCharsInLoop(ref this, out var toReturnLoop))
                            {
                                _current = toReturnLoop;
                                return true;
                            }
                            break;
                        case Mode.FinishLoop:
                            FinishLoop(ref this);
                            break;
                        case Mode.HandleLeftOverCharsInBuffer:
                            if (HandleLeftOverCharsInBuffer(ref this, out var toReturnBuffer))
                            {
                                _current = toReturnBuffer;
                                return true;
                            }
                            break;
                        case Mode.HandleLeftOverSequence:
                            if (HandleLeftOverSequence(ref this, out var toReturnSequence))
                            {
                                _current = toReturnSequence;
                                return true;
                            }
                            break;
                        case Mode.LastRun:
                            LastRun(ref this);
                            return false;

                        default:
                        case Mode.Ended:
                            throw new InvalidOperationException($"Unexpected mode: {mode}");
                    }
                }

                static void LastRun(ref Enumerator self)
                {
                    // release the enumerator's hold on the char buffer
                    if (OwnedSequence<char>.DecrRefCount(self.charBufferWhole))
                    {
                        self.inner.charPool.Return(self.charBufferWhole);
                        self.charBufferWhole = null;
                    }

                    self.mode = Mode.Ended;
                }

                static bool HandleLeftOverSequence(ref Enumerator self, out OwnedSequence<char>? toReturn)
                {
                    // if there's any partial sequence, but the sequence ended exactly, handle it
                    if (self.pendingSequenceHead != null)
                    {
                        toReturn = self.pendingSequenceHead;
                        self.pendingSequenceHead = null;

                        self.mode = Mode.LastRun;
                        return true;
                    }

                    toReturn = null;

                    self.mode = Mode.LastRun;
                    return false;
                }

                static bool HandleLeftOverCharsInBuffer(ref Enumerator self, out OwnedSequence<char>? toReturn)
                {
                    // if there's anything left in the buffer when the stream ends, handle it
                    if (self.readFromCharBufferIx != self.writeToCharBufferIx)
                    {
                        if (self.charBufferWhole == null)
                        {
                            throw new Exception("Shouldn't be possible");
                        }

                        var toAddMem = self.charBuffer[self.readFromCharBufferIx..self.writeToCharBufferIx];

                        toReturn = PrepareReturn(ref self.pendingSequenceHead, ref self.pendingSequenceTail, self.inner.charPool, self.charBufferWhole, toAddMem);

                        self.mode = Mode.HandleLeftOverSequence;
                        return true;
                    }

                    toReturn = null;

                    self.mode = Mode.HandleLeftOverSequence;
                    return false;
                }

                static void FinishLoop(ref Enumerator self)
                {
                    // handle the character buffer being full
                    if (self.writeToCharBufferIx == self.charBuffer.Length)
                    {
                        if (self.readFromCharBufferIx == self.charBuffer.Length)
                        {
                            // we've fully used the buffer, so nothing to save
                            // note that we can't always REUSE it because yielded sequences may not have been disposed

                            // release the enumerators claim
                            if (OwnedSequence<char>.DecrRefCount(self.charBufferWhole))
                            {
                                // in this can we CAN reuse it
                                OwnedSequence<char>.InitRefCount(self.charBufferWhole);
                                self.readFromCharBufferIx = 0;
                                self.writeToCharBufferIx = 0;
                            }
                            else
                            {
                                // grab a new buffer
                                self.charBufferWhole = self.inner.charPool.Rent(CharBufferSize);
                                self.charBuffer = OwnedSequence<char>.InitRefCount(self.charBufferWhole);
                                self.readFromCharBufferIx = 0;
                                self.writeToCharBufferIx = 0;
                            }
                        }
                        else
                        {
                            // we have to save some of the buffer

                            var toAddMem = self.charBuffer[self.readFromCharBufferIx..];
                            if (toAddMem.Length > 0)
                            {
                                if (self.charBufferWhole == null)
                                {
                                    throw new Exception("Shouldn't be possible");
                                }

                                var toAdd = OwnedSequence<char>.Create(self.inner.charPool, self.charBufferWhole, toAddMem);

                                if (self.pendingSequenceTail != null)
                                {
                                    // this isn't the first bit we've had to roll over
                                    self.pendingSequenceTail.SetNext(toAdd);
                                    self.pendingSequenceTail = toAdd;
                                }
                                else
                                {
                                    // this is the first bit of rollover
                                    self.pendingSequenceHead = self.pendingSequenceTail = toAdd;
                                }
                            }

                            self.savedSequenceForMultiCharNewLine = self.inner.newLine.Length > 1;

                            // we've claimed above, but this drops for the enumerator
                            var decrRes = OwnedSequence<char>.DecrRefCount(self.charBufferWhole);
                            Debug.Assert(!decrRes); // releasing should NEVER requiring freeing the array

                            // grab a new buffer
                            self.charBufferWhole = self.inner.charPool.Rent(CharBufferSize);
                            self.charBuffer = OwnedSequence<char>.InitRefCount(self.charBufferWhole);
                            self.readFromCharBufferIx = 0;
                            self.writeToCharBufferIx = 0;
                        }
                    }

                    self.mode = Mode.StartLoop;
                }

                static bool ProcessCharsInLoop(ref Enumerator self, out OwnedSequence<char>? toReturn)
                {
                    var newLineSpan = self.inner.newLine.Span;

                    if (self.savedSequenceForMultiCharNewLine)
                    {
                        // we're going to assume nobody uses a new line sequence longer than \r\n
                        Debug.Assert(newLineSpan.Length == 2);

                        // we should only do this immediately after a roll over, which means new
                        // characters should be going into the front of the buffer
                        Debug.Assert(self.startOfNewCharsIx == 0);

                        if (self.pendingSequenceTail == null)
                        {
                            throw new Exception("Shouldn't be possible");
                        }

                        // our last loop through resulted in rolling over a buffer,
                        // which means that we _might_ have a newline split across
                        // charBuffer and the old sequence

                        // regardless of the choice we make, we're done with this check
                        self.savedSequenceForMultiCharNewLine = false;

                        Span<char> potentialNewLine = stackalloc char[2];
                        potentialNewLine[0] = self.pendingSequenceTail.Memory.Span[^1];
                        potentialNewLine[1] = self.charBuffer.Span[0];

                        if (potentialNewLine.SequenceEqual(newLineSpan))
                        {
                            var ret = PrepareNewlineStraddledReturn(ref self.pendingSequenceHead, ref self.pendingSequenceTail);
                            self.startOfNewCharsIx = 1;
                            self.readFromCharBufferIx = self.startOfNewCharsIx;

                            self.mode = Mode.ProcessCharsInLoop;

                            toReturn = ret;
                            return true;
                        }
                    }

                    int newLineIx;
                    if ((newLineIx = self.charBuffer[self.startOfNewCharsIx..self.writeToCharBufferIx].Span.IndexOf(newLineSpan)) != -1)
                    {
                        var startOfCharsToAddIx = self.readFromCharBufferIx;
                        var endOfCharsToAddIx = self.startOfNewCharsIx + newLineIx;

                        var toAddMem = self.charBuffer[startOfCharsToAddIx..endOfCharsToAddIx];

                        if (self.charBufferWhole == null)
                        {
                            throw new Exception("Shouldn't be possible");
                        }

                        var ret = PrepareReturn(ref self.pendingSequenceHead, ref self.pendingSequenceTail, self.inner.charPool, self.charBufferWhole, toAddMem);

                        // advance indexes to account for the characters we've handled
                        self.startOfNewCharsIx = endOfCharsToAddIx + newLineSpan.Length;
                        self.readFromCharBufferIx = self.startOfNewCharsIx;

                        self.mode = Mode.ProcessCharsInLoop;

                        toReturn = ret;
                        return true;
                    }

                    self.mode = Mode.FinishLoop;

                    toReturn = null;
                    return false;
                }

                static void StartLoop(ref Enumerator self)
                {
                    if (!self.conversionFinished)
                    {
                        ref var byteBuffer = ref self.byteBuffer;
                        if (!self.streamFinished && !byteBuffer.HasData)
                        {
                            // we only need to read if we haven't previously reached the end of the stream
                            // AND there's no pending data
                            //
                            // pending data is less obvious, but if we issue a read when there's still stuff in the buffer
                            // we might block because it could have the "end of command" text in it
                            if (!byteBuffer.Read(self.inner.stream))
                            {
                                self.streamFinished = true;
                            }
                        }

                        // figure out where we can store new chars
                        var writeCharsInto = self.charBuffer[self.writeToCharBufferIx..].Span;

                        // convert anything in the ring buffer
                        byteBuffer.Convert(self.inner.decoder, writeCharsInto, self.streamFinished, out var charsWritten, out var completed);

                        self.writeToCharBufferIx += charsWritten;

                        // are we going to finished after this ends?
                        if (self.streamFinished && completed)
                        {
                            self.conversionFinished = true;
                        }

                        // now split up the char buffer, IF there are any new lines
                        // we only need to look at a small chunk of the character buffer,
                        // since we've already scanned for new lines on any pending chars
                        self.startOfNewCharsIx = self.writeToCharBufferIx - charsWritten - (self.inner.newLine.Length - 1);
                        self.startOfNewCharsIx = Math.Max(0, self.startOfNewCharsIx);

                        self.mode = Mode.ProcessCharsInLoop;
                        return;
                    }

                    self.mode = Mode.HandleLeftOverCharsInBuffer;
                }

                static void FirstRun(ref Enumerator self)
                {
                    self.charBufferWhole = self.inner.charPool.Rent(CharBufferSize);
                    self.charBuffer = OwnedSequence<char>.InitRefCount(self.charBufferWhole); // note that the enumerator OWNS this, so the ref count is 1 now

                    RingBuffer.Intialize(ref self.byteBuffer);

                    self.mode = Mode.StartLoop;
                }

                static OwnedSequence<char> PrepareReturn(
                    ref OwnedSequence<char>? pendingSequenceHead,
                    ref OwnedSequence<char>? pendingSequenceTail,
                    ArrayPool<char> pool,
                    char[] root,
                    ReadOnlyMemory<char> mem
                )
                {
                    OwnedSequence<char> ret;

                    if (pendingSequenceHead != null && pendingSequenceTail != null)
                    {
                        if (!mem.IsEmpty)
                        {
                            var newItem = OwnedSequence<char>.Create(pool, root, mem);
                            pendingSequenceTail.SetNext(newItem);
                        }

                        ret = pendingSequenceHead;

                        pendingSequenceHead = pendingSequenceTail = null;
                    }
                    else
                    {
                        if (!mem.IsEmpty)
                        {
                            var newItem = OwnedSequence<char>.Create(pool, root, mem);
                            ret = newItem;
                        }
                        else
                        {
                            ret = OwnedSequence<char>.Empty;
                        }
                    }

                    return ret;
                }

                static OwnedSequence<char> PrepareNewlineStraddledReturn(ref OwnedSequence<char>? pendingSequenceHead, ref OwnedSequence<char>? pendingSequenceTail)
                {
                    // by definition, this can only happen if a new line straddled the end of a buffer
                    // so these MUST be non-null
                    Debug.Assert(pendingSequenceHead != null);
                    Debug.Assert(pendingSequenceTail != null);

                    pendingSequenceTail.TruncateMemoryTail(1);

                    var ret = pendingSequenceHead;

                    pendingSequenceHead = pendingSequenceTail = null;

                    return ret;
                }
            }

            public void Reset()
            => throw new NotImplementedException();

            public void Dispose()
            {
                if (mode == Mode.Ended)
                {
                    return;
                }

                mode = Mode.Ended;

                if (pendingSequenceHead != null)
                {
                    // never yielded this, so free it
                    pendingSequenceHead.Dispose();
                    pendingSequenceHead = null;
                }

                if (charBufferWhole != null)
                {
                    // still had a buffer, release a reference and free if needed
                    if (OwnedSequence<char>.DecrRefCount(charBufferWhole))
                    {
                        inner.charPool.Return(charBufferWhole);
                        charBufferWhole = null;
                    }
                }
            }
        }

        private const int ByteBufferSize = 1024 * 4;
        internal const int CharBufferSize = ByteBufferSize / sizeof(char);

        private readonly ArrayPool<char> charPool;
        private readonly Stream stream;
        private readonly Decoder decoder;
        private readonly ReadOnlyMemory<char> newLine;

        public ProcessStreamReader(ArrayPool<char> charPool, Stream stream, Encoding encoding, string newLine)
        {
            this.charPool = charPool;
            this.stream = stream;
            this.newLine = newLine.AsMemory();

            this.decoder = EncodingHelper.MakeDecoder(encoding);
        }

        public Enumerable ReadAllLines()
        => new Enumerable(this);

        public void Dispose()
        {
            stream.Dispose();
        }
    }
}
