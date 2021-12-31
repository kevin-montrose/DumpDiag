using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Models how we move output from COM callbacsk to a Read(Span(char) call).
    /// </summary>
    internal sealed class RemoteWinDbgStateMachine : IDisposable
    {
        internal const string PROMPT_START = "> ";
        internal const string END_OUTPUT = "<END_COMMAND_OUTPUT>\n";

        private static readonly byte[] PINNED_PROMPT_START = CreatePinnedStringArray(PROMPT_START);
        private static readonly nint PINNED_PROMPT_START_POINTER = GetPointer(PINNED_PROMPT_START);

        private static readonly byte[] PINNED_END_OUTPUT = CreatePinnedStringArray(END_OUTPUT);
        private static readonly nint PINNED_END_OUTPUT_POINTER = GetPointer(PINNED_END_OUTPUT);

        private readonly object syncLock;

        private bool hasStarted;

        private bool disposed;

        private nint copyIntoPointerStart;
        private int copyIntoPointerLengthBytes;
        private int copiedBytes;

        private bool handlePointerHoldingLock;

        internal RemoteWinDbgStateMachine()
        {
            syncLock = new object();
            hasStarted = false;

            copyIntoPointerStart = IntPtr.Zero;
            copyIntoPointerLengthBytes = 0;
            copiedBytes = 0;

            handlePointerHoldingLock = false;
        }

        public void Dispose()
        {
            lock (syncLock)
            {
                disposed = true;

                Monitor.PulseAll(syncLock);
            }
        }

        // called whenever we get a pointer from a WinDbg output callback
        // this method does not return until the pointer is fully consumed
        // and thus can be freed
        internal void GotPointer(nint ptr)
        {
            var ptrSpan = MakeSpanOfNullTerminatedCharPointer(ptr);

            HandlePointer(ptrSpan, isCommandEnd: false);
        }

        // returns false if we're disposed
        private bool HandlePointer(ReadOnlySpan<char> ptrSpan, bool isCommandEnd)
        {
            if (!handlePointerHoldingLock)
            {
                Monitor.Enter(syncLock);
                handlePointerHoldingLock = true;
            }

            if (!hasStarted)
            {
                handlePointerHoldingLock = false;
                Monitor.Exit(syncLock);

                return true;
            }

        waitForActiveRead:
            if (copyIntoPointerStart == IntPtr.Zero && !disposed)
            {
                // need a buffer, wait for one
                Monitor.Pulse(syncLock);

                handlePointerHoldingLock = false;
                Monitor.Wait(syncLock);
                handlePointerHoldingLock = true;

                goto waitForActiveRead;
            }

            Debug.Assert(copyIntoPointerStart != IntPtr.Zero || disposed);

            if (disposed)
            {
                Monitor.Exit(syncLock);
                return false;
            }

            Span<char> copyIntoSpan;
            unsafe
            {
                copyIntoSpan = new Span<char>((char*)copyIntoPointerStart, copyIntoPointerLengthBytes / sizeof(char));
            }

            var toCopyLengthChars = Math.Min(copyIntoSpan.Length, ptrSpan.Length);

            // move as much of the bytes over as we can
            // and let Read() know how many it was
            var toCopy = ptrSpan.Slice(0, toCopyLengthChars);
            toCopy.CopyTo(copyIntoSpan);
            copiedBytes += toCopyLengthChars * sizeof(char); // this is a += instead of an = because we might pass through this multiple times for one buffer

            // advance ptrSpan, so we copy the next chunk over next 
            ptrSpan = ptrSpan.Slice(toCopyLengthChars);

            var filledBuffer = copyIntoSpan.Length == toCopyLengthChars;

            if (filledBuffer)
            {
                // need another buffer, wake up Read
                copyIntoPointerStart = IntPtr.Zero;
                goto waitForActiveRead;
            }
            else
            {
                // we're going to leave Read blocked, so we need to advance copyIntoPointer
                // and remove copyIntoPointerLengthBytes so the next call into HandlePointer
                // correctly constructs the copyIntoSpan
                var bytesMoved = toCopyLengthChars * sizeof(char);
                copyIntoPointerStart += bytesMoved;
                copyIntoPointerLengthBytes -= bytesMoved;

                if (isCommandEnd)
                {
                    // wake up Read(), we need it to return now
                    Monitor.Pulse(syncLock);

                    handlePointerHoldingLock = false;
                    Monitor.Exit(syncLock);
                }
            }

            return true;
        }

        // The very first command is the implicit "connect" command, which needs
        // special handling
        //
        // this returns false if the machine is shutdown
        internal unsafe bool TryEndIninitialCommand()
        {
            Monitor.Enter(syncLock);
            handlePointerHoldingLock = true;

            hasStarted = true;

            return HandlePointer(new Span<char>((char*)PINNED_END_OUTPUT_POINTER, PINNED_END_OUTPUT.Length / sizeof(char)), isCommandEnd: true);
        }

        // we're about to write a command out
        internal unsafe bool TryStartExplicitCommand()
        {
            return HandlePointer(new Span<char>((char*)PINNED_PROMPT_START_POINTER, PINNED_PROMPT_START.Length / sizeof(char)), isCommandEnd: false);
        }

        // finish a command, causing the active reader to get the END_OUTPUT
        // string and then enter an idle state
        //
        // this returns false if the machine is shutdown
        internal unsafe bool TryEndExplicitCommand()
        {
            return HandlePointer(new Span<char>((char*)PINNED_END_OUTPUT_POINTER, PINNED_END_OUTPUT.Length / sizeof(char)), isCommandEnd: true);
        }

        internal unsafe int Read(Span<byte> into)
        {
            // need an even number of bytes to work with
            Debug.Assert(into.Length > 0 && (into.Length & 0x1) == 0);

            Monitor.Enter(syncLock);

            fixed (byte* intoPtr = into)
            {
                copyIntoPointerStart = (nint)intoPtr;
                copyIntoPointerLengthBytes = into.Length;
                copiedBytes = 0;    // have to clear this here, so HandlePointer correctly increments it

                // wake up HandlePointer
                Monitor.Pulse(syncLock);
                Monitor.Wait(syncLock);
            }

            if (disposed)
            {
                copyIntoPointerStart = IntPtr.Zero;

                Monitor.Exit(syncLock);
                return 0;
            }

            int ret = copiedBytes;

            copyIntoPointerStart = IntPtr.Zero;

            Monitor.Exit(syncLock);
            return ret;
        }

        // internal for testing purposes
        internal unsafe static ReadOnlySpan<char> MakeSpanOfNullTerminatedCharPointer(nint ptr)
        {
            if (IntPtr.Size != 8)
            {
                throw new NotSupportedException("DumpDiag assumes 64-bit for now");
            }

            char* charPtr = (char*)ptr;

            // a carefuly reading of COM guarantees suggests that we can assume the allocation is at least a multiple of 4 bytes
            // so we can _read_ 2 bytes pass the end (not necessarily write)
            //
            // on 64-bit systems we may be able to assume 8-byte alignment... but that's less clear
            // I'm doing it for now under the assumption that something will explode in testing if I'm wrong

            long* asLongPtr = (long*)charPtr;
            while (true)
            {
                var val = *asLongPtr;

                // val holds two chars, 0xaaaa_bbbb
                // we subtract 1 from each, if a == 0 or b == 0 (or >= 0x80) results in the high bit being set in each char
                // 
                // ~val & 0x8000_8000 sets the high bits only if the high bits in v are not set
                //
                // we then and those togethers, and if they are not 0 then at least one of aaaa or bbbb is 0
                var hasZeroChar = ((val - 0x0001_0001_0001_0001) & (~val & unchecked((long)0x8000_8000_8000_8000))) != 0;

                if (!hasZeroChar)
                {
                    asLongPtr++;
                    continue;
                }

                break;
            }

            // we stopped because there's a null char somewhere in the last 8 bytes
            // we need to check the first 3 to see if
            var endPtr = (char*)asLongPtr;
            if (*endPtr != 0)
            {
                // it's not the first char
                endPtr++;

                if (*endPtr != 0)
                {
                    // it's not the second char
                    endPtr++;

                    if (*endPtr != 0)
                    {
                        // it's not the third char (so it must be the fourth)
                        endPtr++;
                    }
                }
            }

            var len = (int)(endPtr - charPtr);
            return new ReadOnlySpan<char>(charPtr, len);
        }

        // move a pre-pinned chunk of data into a span
        private static unsafe bool CopySpecialString(nint ptr, int lengthBytes, ushort bytesAlreadyCopied, out ushort bytesCopied, ref Span<byte> copyInto)
        {
            var startCopyFrom = (byte*)ptr + bytesAlreadyCopied;
            var needsCopyLength = lengthBytes - bytesAlreadyCopied;
            bytesCopied = (ushort)Math.Min(needsCopyLength, copyInto.Length);

            var toCopySpan = new Span<byte>(startCopyFrom, bytesCopied);
            toCopySpan.CopyTo(copyInto);
            copyInto = copyInto.Slice(bytesCopied);

            return needsCopyLength == bytesCopied;
        }

        private static byte[] CreatePinnedStringArray(string str)
        {
            var ret = GC.AllocateUninitializedArray<byte>(str.Length * sizeof(char), pinned: true);

            var copied = Encoding.Unicode.GetBytes(str, ret);

            if (copied != ret.Length)
            {
                throw new Exception("Shouldn't be possible");
            }

            return ret;
        }

        // we know these are pinned, so this is safe
        private static unsafe nint GetPointer(byte[] arr)
        {
            fixed (byte* ptr = arr)
            {
                return (nint)ptr;
            }
        }
    }
}
