using DbgEngWrapper;
using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using Microsoft.Diagnostics.Runtime.Interop;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class RemoteWinDbgStreamTests : IAsyncLifetime
    {
        private WinDbgHelper helper;
        private DebugConnectWideThunk thunk;

        public async Task InitializeAsync()
        {
            helper = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First()).ConfigureAwait(false);

            var handle = NativeLibrary.Load(helper.DbgEngDllPath);

            if (!Impl.DebugConnectWideThunk.TryCreate(handle, out thunk, out var error))
            {
                throw new Exception(error);
            }
        }

        public Task DisposeAsync()
        {
            return helper.DisposeAsync().AsTask();
        }

        private sealed class _DebugConnectWideThunk : IDebugOutputCallbacksImp
        {
            internal int OutputCalledCount { get; private set; } = 0;

            public int Output(DEBUG_OUTPUT Mask, IntPtr Text)
            {
                OutputCalledCount++;

                var text = Marshal.PtrToStringUni(Text);

                return 0;
            }
        }

        [Fact]
        public void DebugConnectWideThunk()
        {
            var handle = NativeLibrary.Load(helper.DbgEngDllPath);

            Assert.True(Impl.DebugConnectWideThunk.TryCreate(handle, out var thunk, out var error));
            Assert.Null(error);

            using var client = thunk.CreateClient(IPAddress.Loopback.ToString(), helper.LocalPort);

            var callbacks = new _DebugConnectWideThunk();

            var hr2_5 = client.SetOutputCallbacksWide(callbacks);
            if (hr2_5 < 0)
            {
                Marshal.ThrowExceptionForHR(hr2_5);
            }

            using var control = (WDebugControl)client;

            var strPtr = Marshal.StringToCoTaskMemUni("!help");
            try
            {
                var hr3 = control.ExecuteWide(DEBUG_OUTCTL.ALL_CLIENTS, strPtr, DEBUG_EXECUTE.NO_REPEAT);

                if (hr3 < 0)
                {
                    Marshal.ThrowExceptionForHR(hr3);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(strPtr);
            }

            var hr4 = client.FlushCallbacks();
            if (hr4 < 0)
            {
                Marshal.ThrowExceptionForHR(hr4);
            }

            // release the reference
            client.SetOutputCallbacksWide(null);

            Assert.True(callbacks.OutputCalledCount > 0, "called at least once");

            // this is expected, since !help writes SO MUCH
            // this is also important, otherwise we couldn't stream responses out...
            Assert.True(callbacks.OutputCalledCount > 1, "called at least twice");

            // keep these alive until here, they're used on the native size
            // and explicit lifetimes just make life easier
            GC.KeepAlive(callbacks);
            GC.KeepAlive(control);
            GC.KeepAlive(client);
        }

        [Theory(/*Timeout = 30_000*/)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(6)]
        [InlineData(8)]
        [InlineData(10)]
        [InlineData(12)]
        [InlineData(14)]
        [InlineData(16)]
        [InlineData(18)]
        [InlineData(20)]
        [InlineData(22)]
        [InlineData(24)]
        [InlineData(26)]
        [InlineData(28)]
        [InlineData(30)]
        [InlineData(32)]
        [InlineData(1024)]
        public async Task StartupStressAsync(int stepSizeBytes)
        {
            const string Command = "dd 123";

            if (!OperatingSystem.IsWindows())
            {
                // todo: figure out how to skip this instead of passing on non-Windows
                return;
            }

            Debug.WriteLine($"Step: {stepSizeBytes}");
            await using var winDbg = await RemoteWinDbgStream.CreateAsync(thunk, IPAddress.Loopback.ToString(), helper.LocalPort, TimeSpan.FromMinutes(30)).ConfigureAwait(false);

            winDbg.Write(Encoding.Unicode.GetBytes(Command));

            var scratch = new byte[stepSizeBytes];

            string resp = "";
            while (resp.Length < (RemoteWinDbgStateMachine.END_OUTPUT.Length + 3) || !resp.Trim().EndsWith(RemoteWinDbgStateMachine.END_OUTPUT.Trim()))
            {
                var read = winDbg.Read(scratch);
                //Debug.WriteLine($"\t{read}");

                if (read > 0)
                {
                    var asStr = new string(MemoryMarshal.Cast<byte, char>(scratch[0..read]));
                    resp += asStr;
                }

                //Debug.WriteLine($"So far: {resp}");
            }

            Assert.NotEmpty(resp);
            // end output first, because of the implicit "connect" command
            Assert.StartsWith(RemoteWinDbgStateMachine.END_OUTPUT + RemoteWinDbgStateMachine.PROMPT_START + Command, resp);
        }

        [Fact]
        public void MakeSpanOfNullTerminatedCharPointer()
        {
            for (var i = 0; i <= 52; i++)
            {
                var text = "";
                for(var j = 0; j < i; j++)
                {
                    text += (char)('A' + j);
                }

                var arrSize = ((text.Length * sizeof(char) / sizeof(long)) + 1) * sizeof(long);
                var intoArr = new byte[arrSize];

                var asChar = MemoryMarshal.Cast<byte, char>(intoArr);
                text.AsSpan().CopyTo(asChar);
                asChar[text.Length] = '\0';

                unsafe
                {
                    fixed (byte* bytePtr = intoArr)
                    {
                        var span = RemoteWinDbgStateMachine.MakeSpanOfNullTerminatedCharPointer((nint)bytePtr);

                        var shouldMatch = new string(span);

                        Assert.Equal(text, shouldMatch);
                    }
                }
            }
        }
    }
}