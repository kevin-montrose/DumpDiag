using DumpDiag.Impl;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DumpDiag.CommandLine
{
    [SupportedOSPlatform("windows")]
    internal sealed class RemoteWinDbgTarget
    {
        private readonly FileInfo dbgEngDllPath;
        private readonly ImmutableList<RemoteWinDbgAddress> remoteAddresses;
        private readonly TextWriter resultWriter;
        private readonly bool quiet;
        private readonly FileInfo? saveReportTo;
        private readonly int minAsyncSize;
        private readonly int minCount;
        private readonly bool overwrite;
        private readonly FileInfo? resumeFile;

        internal RemoteWinDbgTarget(
            FileInfo dbgEngDllPath, 
            IEnumerable<RemoteWinDbgAddress> remoteAddresses, 
            int minAsyncSize, 
            int minCount, 
            bool overwrite,
            bool quiet, 
            TextWriter resultWriter, 
            FileInfo? saveReportTo,
            FileInfo? resumeFile
        )
        {
            this.dbgEngDllPath = dbgEngDllPath;
            this.remoteAddresses = remoteAddresses.ToImmutableList();
            this.resultWriter = resultWriter;
            this.saveReportTo = saveReportTo;
            this.minAsyncSize = minAsyncSize;
            this.minCount = minCount;
            this.overwrite = overwrite;
            this.quiet = quiet;
            this.resumeFile = resumeFile;
        }

        internal async ValueTask<(ExitCodes Result, string? ErrorMessagE)> RunAsync()
        {
            if (!dbgEngDllPath.Exists)
            {
                return (ExitCodes.DbgEngDllNotFound, $"Could not find {dbgEngDllPath.FullName}");
            }

            // we need to load this here, because if we wait until we're in DbgEngWrapper
            // or a type that directly touches it we might implicitly pull something in off 
            // of PATH
            if(!NativeLibrary.TryLoad(this.dbgEngDllPath.FullName, out var dbgEngHandle))
            {
                return (ExitCodes.DbgEngCouldNotBeLoaded, $"Could not load {dbgEngDllPath.FullName}");
            }

            if(!DebugConnectWideThunk.TryCreate(dbgEngHandle, out var thunk, out var error))
            {
                return (ExitCodes.DbgEngCouldNotBeLoaded, $"Could not load {dbgEngDllPath.FullName}: {error}");
            }

            Report(resultWriter, $"DbgEng.dll location: {dbgEngDllPath.FullName}", quiet);

            var prog = new ProgressWrapper(quiet, resultWriter);
            var storage = resumeFile != null ? new FileBackedDiagnosisStorage(resumeFile.Open(FileMode.OpenOrCreate)) : null;

            await using var diag = await DumpDiagnoser.CreateRemoteWinDbgAsync(thunk, remoteAddresses, TimeSpan.FromSeconds(30), prog, storage);
            var res = await diag.AnalyzeAsync().ConfigureAwait(false);

            Report(resultWriter, "Analyzing complete", quiet);

            string? saveReportToPath;
            if (saveReportTo != null)
            {
                if (saveReportTo.Exists && !overwrite)
                {
                    return (ExitCodes.ReportFileExists, $"Report file already exists: {saveReportTo.FullName}");
                }

                saveReportToPath = saveReportTo.FullName;

                Report(resultWriter, $"Writing report to: {saveReportToPath}", quiet);
            }
            else
            {
                saveReportToPath = null;

                Report(resultWriter, "Writing report to standard output", quiet);
            }

            if (saveReportToPath == null)
            {
                if (!quiet)
                {
                    await resultWriter.WriteLineAsync().ConfigureAwait(false);
                    await resultWriter.WriteLineAsync("---").ConfigureAwait(false);
                    await resultWriter.WriteLineAsync().ConfigureAwait(false);
                }

                await res.WriteToAsync(resultWriter, minCount, minAsyncSize).ConfigureAwait(false);
            }
            else
            {
                Report(resultWriter, $"Saving report to: {saveReportTo}", quiet);

                using (var fs = File.CreateText(saveReportToPath))
                {
                    await res.WriteToAsync(fs, minCount, minAsyncSize).ConfigureAwait(false);
                }
            }

            return (ExitCodes.Success, null);

            // report some other messsage
            static void Report(TextWriter writeTo, string message, bool quiet)
            {
                if (quiet)
                {
                    return;
                }

                writeTo.Write($"[{DateTime.UtcNow:u}]: ");
                writeTo.WriteLine(message);
            }
        }
    }
}
