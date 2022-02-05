using DbgEngWrapper;
using System;
using System.Diagnostics.CodeAnalysis;

using System.Runtime.InteropServices;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Since we have to match the EXACT LIBRARY VERSION when using DbgEng.dll, 
    /// we need to load libraries manually.
    /// 
    /// Yes, this stinks but it's the reality of it.
    /// </summary>
    internal readonly struct DebugConnectWideThunk // internal for testing purposes
    {
        // only one caller at a time
        private static readonly object SharedStartupLock = new object();

        internal IntPtr LibraryHandle { get; }
        internal IntPtr DebugConnectWidePtr { get; }

        private DebugConnectWideThunk(IntPtr lib, IntPtr func)
        {
            LibraryHandle = lib;
            DebugConnectWidePtr = func;
        }

        internal unsafe WDebugClient CreateClient(string ip, ushort port)
        {
            var remoteOptions = $"tcp:server={ip},port={port}";
            var remoteOptionsLPWStr = Marshal.StringToCoTaskMemUni(remoteOptions);
            var debugClient6Guid = new Guid("e3acb9d7-7ec2-4f0c-a0da-e81e0cbbe628");
            try
            {
                var debugClient6GuidPtr = &debugClient6Guid;
                var debugClient6GuidIntPtr = (IntPtr)debugClient6GuidPtr;

                var debugConnectWideDel = (delegate* unmanaged<IntPtr, IntPtr, out IntPtr, int>)DebugConnectWidePtr;

                int hres;
                IntPtr debugClient;
                lock (SharedStartupLock)
                {
                    hres = debugConnectWideDel(remoteOptionsLPWStr, debugClient6GuidIntPtr, out debugClient);
                }

                if (hres < 0)
                {
                    Marshal.ThrowExceptionForHR(hres);
                }

                return new WDebugClient(debugClient);
            }
            finally
            {
                Marshal.FreeCoTaskMem(remoteOptionsLPWStr);
            }
        }

        internal static bool TryCreate(IntPtr libraryHandle, out DebugConnectWideThunk thunk, [NotNullWhen(returnValue: false)] out string? error)
        {
            const string DEBUG_CONNECT_WIDE = "DebugConnectWide";

            if (!NativeLibrary.TryGetExport(libraryHandle, DEBUG_CONNECT_WIDE, out var debugConnectWidePtr))
            {
                thunk = default;
                error = $"Could not load function '{DEBUG_CONNECT_WIDE}' from library";
                return false;
            }

            thunk = new DebugConnectWideThunk(libraryHandle, debugConnectWidePtr);
            error = null;
            return true;
        }
    }
}
