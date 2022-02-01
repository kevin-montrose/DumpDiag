using DbgEngWrapper;
using Microsoft.Diagnostics.Runtime.Interop;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal sealed class RemoteWinDbgStream : Stream, IDebugOutputCallbacksImp
    {
        private readonly string uniqueString;

        private readonly DebugConnectWideThunk thunk;
        private readonly Thread thread;
        private readonly string ip;
        private readonly ushort port;
        private readonly TimeSpan startTimeout;

        private readonly AutoResetEvent writeReady;

        private readonly ManualResetEventSlim startupComplete;

        private TaskCompletionSource? startTcs;
        private TaskCompletionSource? stopTcs;
        private bool running;

        // if not empty, this contains a null terminator Unicode (ie. UCS2) string to write
        // todo: we can do better than this
        private char[] pendingWrite;

        private StringBuilder? startupBuffer;

        private RemoteWinDbgStateMachine state;

        public override bool CanRead { get; } = true;

        public override bool CanSeek { get; } = false;

        public override bool CanWrite { get; } = true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        private RemoteWinDbgStream(DebugConnectWideThunk thunk, string ip, ushort port, TimeSpan startTimeout)
        {
            this.thunk = thunk;
            this.ip = ip;
            this.port = port;
            this.startTimeout = startTimeout;

            thread = new Thread(ThreadLoop);
            thread.Name = $"{nameof(DebugConnectWideThunk)} Dedicated Thread";

            startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            writeReady = new AutoResetEvent(false);
            pendingWrite = Array.Empty<char>();

            uniqueString = Guid.NewGuid().ToString();

            startupBuffer = new StringBuilder();
            startupComplete = new ManualResetEventSlim(false);

            state = new RemoteWinDbgStateMachine();
        }

        int IDebugOutputCallbacksImp.Output(DEBUG_OUTPUT _, IntPtr textPtr)
        {
            if (!Volatile.Read(ref running))
            {
                return 0;
            }

            // textPtr is a LPCWSTR which lives for just this method
            // so we have to BLOCK if we're not going to handle the string
            // just yet

            // only one of these calls can be in progress at once

            // if we get a completely empty string, just ignore it
            // there's no reason to do all the thread transitioning
            // and whatnot to just... do nothing
            unsafe
            {
                char* ptr = (char*)textPtr;
                if (*ptr == '\0')
                {
                    return 0;
                }
            }

            if (startupBuffer != null)
            {
                var asStr = Marshal.PtrToStringUni(textPtr);
                startupBuffer.Append(asStr);

                // todo: can do better that this...
                var fullText = startupBuffer.ToString();
                var lines = fullText.Split("\n");
                if (lines.Any(x => x == uniqueString))
                {
                    // because this callback always comes from a fixed thread, we don't need to synchronize this
                    startupBuffer = null;
                    startupComplete.Set();

                    Debug.WriteLine(fullText);
                }

                return 0;
            }

            state.GotPointer(textPtr);

            return 0;
        }

        private void ThreadLoop()
        {
            var toComplete = Interlocked.Exchange(ref startTcs, null);
            if (toComplete == null)
            {
                throw new Exception("Shouldn't be possible");
            }

            var startingUp = true;

            try
            {
                var initStartTimestamp = Stopwatch.GetTimestamp();

                // EVERYTHING that touches the debugger needs to come from this thread
                // so we don't let any references escape this thread entry method
                using var client = thunk.CreateClient(ip, port);
                using var control = (WDebugControl)client;

                var sessionHRes = client.ConnectSession(DEBUG_CONNECT_SESSION.DEFAULT, 0);
                if (sessionHRes != 0)
                {
                    var connectFailure = Marshal.GetExceptionForHR(sessionHRes)!;
                    toComplete.SetException(connectFailure);
                    return;
                }

                if (!TryWaitForBreakStatus(initStartTimestamp, startTimeout, client, control, out var waitForBreakExc))
                {
                    toComplete.SetException(waitForBreakExc);
                    return;
                }

                // hook up getting output
                client.SetOutputMask(DEBUG_OUTPUT.NORMAL);
                var registerOutputRes = client.SetOutputCallbacksWide(this);
                if (registerOutputRes != 0)
                {
                    toComplete.SetException(Marshal.GetExceptionForHR(registerOutputRes)!);
                    return;
                }

                // wait for output to be idle
                if (!TryWaitForOutputIdle(this, client, startTimeout, initStartTimestamp, out var waitForIdleExc))
                {
                    client.SetOutputCallbacksWide(null);
                    toComplete.SetException(waitForIdleExc);
                    return;
                }

                // run a command to make sure we're getting responses we expect
                if (!TryWaitForEchoCommand(initStartTimestamp, startTimeout, client, control, uniqueString, startupComplete, out var waitForEchoExc))
                {
                    client.SetOutputCallbacksWide(null);
                    toComplete.SetException(waitForEchoExc);
                    return;
                }

                // we're done starting, so we're golden so far as StartAsync() is concerned
                startingUp = false;
                toComplete.SetResult();
                toComplete = null;

                // the implicit command of "connect" is done, so indicate as such
                if (state.TryEndIninitialCommand())
                {
                    // loop for all commands now
                    while (WriteIsReady(writeReady, ref running))
                    {
                        // get the buffer, letting the writing thread unblock
                        var toWrite = Volatile.Read(ref pendingWrite);

                        // shove a > into the stream
                        if (!state.TryStartExplicitCommand())
                        {
                            break;
                        }

                        // make the call, this will BLOCK until complete...
                        unsafe
                        {
                            fixed (char* charPtr = toWrite)  // during the Write() call, we've already null terminated this, so we should be fine
                            {
                                byte* lpwstr = (byte*)charPtr;
                                var executeHRes = control.ExecuteWide(DEBUG_OUTCTL.THIS_CLIENT, (IntPtr)lpwstr, DEBUG_EXECUTE.ECHO | DEBUG_EXECUTE.NO_REPEAT);
                                if (executeHRes < 0)
                                {
                                    Marshal.ThrowExceptionForHR(executeHRes);
                                }
                            }
                        }

                        // ... but we need to explicitly flush to make sure we get all the output
                        var flushHRes = client.FlushCallbacks();
                        if (flushHRes < 0)
                        {
                            Marshal.ThrowExceptionForHR(flushHRes);
                        }

                        // shove a <END_COMMAND_OUTPUT> into the stream
                        if (!state.TryEndExplicitCommand())
                        {
                            break;
                        }
                    }
                }

                // signal that output is dead to us, releasing any reference that might have been made on the native side
                client.SetOutputCallbacksWide(null);

                // make sure we keep these alive, as they might be referenced (indirectly) from native code
                // and deterministic destruction is just nicer in those cases
                GC.KeepAlive(control);
                GC.KeepAlive(client);
            }
            catch (Exception e)
            {
                if (startingUp)
                {
                    toComplete!.SetException(e);
                    return;
                }

                throw;
            }

            var toNotifyOnShutdown = Interlocked.Exchange(ref stopTcs, null);
            toNotifyOnShutdown?.SetResult();

            // wait for a write to be queued, or for this to be disposed
            static bool WriteIsReady(AutoResetEvent writeReader, ref bool running)
            {
                // still running?
                if (!Volatile.Read(ref running))
                {
                    return false;
                }

                // wait for a write
                writeReader.WaitOne();

                // we could also be woken up to exit, so we need a second check here
                if (!Volatile.Read(ref running))
                {
                    return false;
                }

                return true;
            }

            // we have no way to detect that we're "ready"
            // so just spin some and see if we get any output...
            static bool TryWaitForOutputIdle(
                RemoteWinDbgStream self,
                WDebugClient client,
                TimeSpan startTimeout,
                long initStartTimestamp,
                [NotNullWhen(returnValue: false)] out Exception? error
            )
            {
                // ok, there doesn't appear to be a way to wait for the "I'm loading..." stuff to finish
                // nor a way to isolate just output to our client commands...
                // so let's just idle a bit
                var elapsedTicks = Stopwatch.GetTimestamp() - initStartTimestamp;
                var ticksRemaining = startTimeout.TotalSeconds * Stopwatch.Frequency - elapsedTicks;
                var maxWaitTicks = ticksRemaining / 2;
                var maxWaitMs = maxWaitTicks * 1_000 / Stopwatch.Frequency;
                if (maxWaitMs >= 200)
                {
                    var maxAttempts = (maxWaitMs / 100) / 2;

                    // todo: really should just set something, not check length here
                    var lastObservedOutputLength = self.startupBuffer!.Length;
                    var noChangeCount = 0;
                    var attemptCount = 0;

                    while (attemptCount < maxAttempts && noChangeCount < 2)
                    {
                        Thread.Sleep(100);
                        var latestLength = self.startupBuffer!.Length;
                        if (lastObservedOutputLength != latestLength)
                        {
                            lastObservedOutputLength = latestLength;
                            noChangeCount = 0;
                        }
                        else
                        {
                            noChangeCount++;
                        }

                        attemptCount++;

                        var idleFlushHRes = client.FlushCallbacks();
                        if (idleFlushHRes != 0)
                        {
                            error = Marshal.GetExceptionForHR(idleFlushHRes)!;
                            return false;
                        }
                    }
                }

                error = null;
                return true;
            }

            // wait until the debugger reports it's at a breakpoint
            static bool TryWaitForBreakStatus(
                long timeoutBeginsTimestamp,
                TimeSpan timeout,
                WDebugClient client,
                WDebugControl control,
                [NotNullWhen(returnValue: false)] out Exception? error)
            {
                // gotta wait for the debugger to be ready for input
                while (true)
                {
                    var curTimestamp = Stopwatch.GetTimestamp();

                    var delta = curTimestamp - timeoutBeginsTimestamp;
                    var elapsed = TimeSpan.FromSeconds(((double)delta) / Stopwatch.Frequency);

                    if (elapsed > timeout)
                    {
                        error = new TimeoutException($"Startup did not complete within configured timeout {timeout}, debugger never reported break status");
                        return false;
                    }

                    var statusHRes = control.GetExecutionStatusEx(out var status);
                    if (statusHRes != 0)
                    {
                        error = Marshal.GetExceptionForHR(statusHRes);
                        return false;
                    }

                    if (status == DEBUG_STATUS.BREAK)
                    {
                        break;
                    }

                    Thread.Sleep(100);
                }

                // flush out of buffers before we do anything else
                var clearBuffersHRes = client.FlushCallbacks();
                if (clearBuffersHRes != 0)
                {
                    error = Marshal.GetExceptionForHR(clearBuffersHRes);
                    return false;
                }

                error = null;
                return true;
            }

            // run an echo command, and wait to read it
            static bool TryWaitForEchoCommand(
                long timeoutBeginsTimestamp,
                TimeSpan timeout,
                WDebugClient client,
                WDebugControl control,
                string uniqueString,
                ManualResetEventSlim uniqueStringResponseReceived,
                [NotNullWhen(returnValue: false)] out Exception? error)
            {
                // ok, it's ready for input and we're listening...
                // but other output may still be happening so let's do an .echo and _wait_
                unsafe
                {
                    var echoCommand = $".echo \"{uniqueString}\"";
                    fixed (char* charPtr = echoCommand)  // during the Write() call, we've already null terminated this, so we should be fine
                    {
                        byte* lpwstr = (byte*)charPtr;
                        var executeHRes = control.ExecuteWide(DEBUG_OUTCTL.THIS_CLIENT, (IntPtr)lpwstr, DEBUG_EXECUTE.ECHO | DEBUG_EXECUTE.NO_REPEAT);
                        if (executeHRes != 0)
                        {
                            error = Marshal.GetExceptionForHR(executeHRes);
                            return false;
                        }
                    }
                }

                var getEchoResHRes = client.FlushCallbacks();
                if (getEchoResHRes != 0)
                {
                    error = Marshal.GetExceptionForHR(getEchoResHRes);
                    return false;
                }

                var elapseTicksSinceStart = Stopwatch.GetTimestamp() - timeoutBeginsTimestamp;
                var secondsLeft = timeout.TotalSeconds - elapseTicksSinceStart / Stopwatch.Frequency;
                if (secondsLeft <= 0 || !uniqueStringResponseReceived.Wait(TimeSpan.FromSeconds(secondsLeft)))
                {
                    error = new TimeoutException($"Startup did not complete within configured timeout {timeout}, probe command never executed");
                    return false;
                }

                error = null;
                return true;
            }
        }

        private Task StartAsync()
        {
            var cur = startTcs;
            cur = Interlocked.Exchange(ref startTcs, cur);
            Debug.Assert(cur != null);

            Volatile.Write(ref running, true);
            
            thread.Start();

            return cur.Task;
        }

        public override int Read(Span<byte> buffer)
        => state.Read(buffer);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            const int PADDING = sizeof(char);  // need space for a null terminator

            var neededSizeBytes = buffer.Length + PADDING;
            var neededSizeChars = neededSizeBytes / sizeof(char);

            if (pendingWrite.Length < neededSizeChars)
            {
                var targetSize = (neededSizeBytes / 128 + 1) * 128;
                Volatile.Write(ref pendingWrite, GC.AllocateUninitializedArray<char>(targetSize, pinned: true));
            }

            var asCharSpan = pendingWrite.AsSpan();
            var asByteSpan = MemoryMarshal.AsBytes(asCharSpan);

            buffer.CopyTo(asByteSpan);

            // slap a null terminator in there
            asByteSpan[buffer.Length] = 0;
            asByteSpan[buffer.Length + 1] = 0;

            // signal the COM thread
            writeReady.Set();
        }

        public override ValueTask DisposeAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref stopTcs, tcs);
            Volatile.Write(ref running, false);
            
            // wake everything up
            writeReady.Set();
            state.Dispose();

            return new ValueTask(tcs.Task);
        }

        [SupportedOSPlatform("windows")]
        internal static async ValueTask<RemoteWinDbgStream> CreateAsync(DebugConnectWideThunk libraryHandle, string ip, ushort port, TimeSpan startTimeout)
        {
            if (!IPAddress.TryParse(ip, out _))
            {
                throw new ArgumentException(nameof(ip));
            }

            var ret = new RemoteWinDbgStream(libraryHandle, ip, port, startTimeout);
            await ret.StartAsync().ConfigureAwait(false);

            return ret;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

        public override void SetLength(long value)
        => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
    }
}
