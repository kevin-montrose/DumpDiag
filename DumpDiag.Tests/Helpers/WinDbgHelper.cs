using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests.Helpers
{
    internal sealed class WinDbgHelper : IAsyncDisposable
    {
        const string DBGENG_DLL = "dbgeng.dll";

        internal static readonly ImmutableArray<string> WinDbgLocations = EnumerateWinDbgDirectories().ToImmutableArray();

        internal string DbgEngDllPath { get; }
        internal ushort LocalPort { get; }
        internal Process WinDbg { get; }
        internal string DumpFilePath { get; }

        private WinDbgHelper(string dbgEngDll, ushort port, Process winDbg, string dumpFilePath)
        {
            DbgEngDllPath = dbgEngDll;
            LocalPort = port;
            WinDbg = winDbg;
            DumpFilePath = dumpFilePath;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                WinDbg.Kill();

                await WinDbg.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { }
        }

        private static IEnumerable<string> EnumerateWinDbgDirectories()
        {
            // these are located in <Program Files>\Windows Kits\<Blah>\Debuggers\x64

            var alreadyYielded = new HashSet<string>();

            foreach (var toYield in ProbeForDbgEng(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), alreadyYielded))
            {
                yield return toYield;
            }

            foreach (var toYield in ProbeForDbgEng(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), alreadyYielded))
            {
                yield return toYield;
            }

            static IEnumerable<string> ProbeForDbgEng(string progDir, HashSet<string> alreadyYielded)
            {
                const string WINDOWS_KITS = "Windows Kits";
                const string DEBUGGERS = "Debuggers";
                const string X64 = "x64";

                var kitDir = Path.Combine(progDir, WINDOWS_KITS);
                if (!Directory.Exists(kitDir))
                {
                    yield break;
                }

                foreach (var subDir in Directory.EnumerateDirectories(kitDir))
                {
                    var debuggerPath = Path.Combine(subDir, DEBUGGERS, X64);
                    var probePath = Path.Combine(debuggerPath, DBGENG_DLL);
                    if (File.Exists(probePath) && alreadyYielded.Add(debuggerPath))
                    {
                        yield return debuggerPath;
                    }
                }
            }
        }

        internal static async ValueTask<WinDbgHelper> CreateWinDbgInstanceAsync(string winDbgDirectory, SelfDumpHelper dump = null, ushort chosenPort = 10_000)
        {
            const string WINDBG_EXE = "windbg.exe";

            dump ??= await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            var winDbgPath = Path.Combine(winDbgDirectory, WINDBG_EXE);
            var dbgEngPath = Path.Combine(winDbgDirectory, DBGENG_DLL);

            var winDbgStart = new ProcessStartInfo();
            winDbgStart.FileName = winDbgPath;
            winDbgStart.WorkingDirectory = Path.GetDirectoryName(winDbgPath);
            winDbgStart.Arguments = $"-server tcp:port={chosenPort} -z \"{dump.DumpFile}\"";

            var winDbg = Process.Start(winDbgStart);

            Impl.Job.Instance.AssociateProcess(winDbg);

            Thread.Sleep(100);

            Assert.False(winDbg.HasExited);

            return new WinDbgHelper(dbgEngPath, chosenPort, winDbg, dump.DumpFile);
        }
    }
}
