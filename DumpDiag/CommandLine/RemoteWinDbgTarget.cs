using DumpDiag.Impl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DumpDiag.CommandLine
{
    [SupportedOSPlatform("windows")]
    internal sealed class RemoteWinDbgTarget
    {
        private readonly FileInfo dbgEngDllPath;
        private readonly string ip;
        private readonly ushort port;
        private readonly TextWriter resultWriter;
        private readonly bool quiet;
        private readonly FileInfo? saveReportTo;
        private readonly int minAsyncSize;
        private readonly int minCount;
        private readonly bool overwrite;

        internal RemoteWinDbgTarget(FileInfo dbgEngDllPath, string ip, int minAsyncSize, int minCount, bool overwrite, ushort port, bool quiet, TextWriter resultWriter, FileInfo? saveReportTo)
        {
            this.dbgEngDllPath = dbgEngDllPath;
            this.ip = ip;
            this.port = port;
            this.quiet = quiet;
            this.resultWriter = resultWriter;
            this.saveReportTo = saveReportTo;
            this.minAsyncSize = minAsyncSize;
            this.minCount = minCount;
            this.overwrite = overwrite;
        }

        internal async ValueTask<(ExitCodes Result, string? ErrorMessagE)> RunAsync()
        {
            if (!dbgEngDllPath.Exists)
            {
                return (ExitCodes.DbgEngDllNotFound, $"Could not find {dbgEngDllPath.FullName}");
            }

            Report(resultWriter, $"DbgEng.dll location: {dbgEngDllPath.FullName}", quiet);

            var prog = new ProgressWrapper(prog => ReportProgress(resultWriter, prog, quiet));

            await using var diag = await DumpDiagnoser.CreateRemoteWinDbgAsync(dbgEngDllPath.FullName, ip, port, TimeSpan.FromSeconds(30), prog);
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

            // report analysis progress
            static void ReportProgress(TextWriter writer, DumpDiagnoserProgress progress, bool quiet)
            {
                if (quiet)
                {
                    return;
                }

                var parts = new List<string>();

                if (progress.PercentStartingTasks > 0 && progress.PercentStartingTasks < 100)
                {
                    parts.Add($"starting: {progress.PercentStartingTasks}%");
                }

                if (progress.PercentCharacterArrays > 0 && progress.PercentCharacterArrays < 100)
                {
                    parts.Add($"char[]s: {progress.PercentCharacterArrays}%");
                }

                if (progress.PercentDelegateDetails > 0 && progress.PercentDelegateDetails < 100)
                {
                    parts.Add($"delegates stats: {progress.PercentDelegateDetails}%");
                }

                if (progress.PercentDeterminingDelegates > 0 && progress.PercentDeterminingDelegates < 100)
                {
                    parts.Add($"finding delegates: {progress.PercentDeterminingDelegates}%");
                }

                if (progress.PercentLoadHeap > 0 && progress.PercentLoadHeap < 100)
                {
                    parts.Add($"scanning heap: {progress.PercentLoadHeap}%");
                }

                if (progress.PercentStrings > 0 && progress.PercentStrings < 100)
                {
                    parts.Add($"strings: {progress.PercentStrings}%");
                }

                if (progress.PercentThreadCount > 0 && progress.PercentThreadCount < 100)
                {
                    parts.Add($"thread count: {progress.PercentThreadCount}%");
                }

                if (progress.PercentThreadDetails > 0 && progress.PercentThreadDetails < 100)
                {
                    parts.Add($"thread details: {progress.PercentThreadDetails}%");
                }

                if (progress.PercentTypeDetails > 0 && progress.PercentTypeDetails < 100)
                {
                    parts.Add($"type details: {progress.PercentTypeDetails}%");
                }

                if (progress.PercentAsyncDetails > 0 && progress.PercentAsyncDetails < 100)
                {
                    parts.Add($"async details: {progress.PercentAsyncDetails}%");
                }

                if (progress.PercentHeapAssignments > 0 && progress.PercentHeapAssignments < 100)
                {
                    parts.Add($"heap assignments: {progress.PercentHeapAssignments}%");
                }

                if (progress.PercentAnalyzingPins > 0 && progress.PercentAnalyzingPins < 100)
                {
                    parts.Add($"pins: {progress.PercentAnalyzingPins}%");
                }

                if (parts.Count == 0)
                {
                    return;
                }

                var str = string.Join(", ", parts);

                writer.Write($"[{DateTime.UtcNow:u}]: ");
                writer.WriteLine(str);
            }
        }
    }
}
