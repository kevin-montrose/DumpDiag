using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace DumpDiag.Impl
{
    internal sealed class DbgEngFinder
    {
        const string DBGENG_DLL = "dbgeng.dll";

        internal static bool TryFindDefault([NotNullWhen(returnValue: true)]out string? path)
        {
            foreach(var dir in EnumerateWinDbgDirectories())
            {
                var file = Path.Combine(dir, DBGENG_DLL);
                if(File.Exists(file))
                {
                    path = file;
                    return true;
                }
            }

            path = null;
            return false;
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
    }
}
