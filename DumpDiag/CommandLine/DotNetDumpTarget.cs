using DumpDiag.Impl;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DumpDiag.CommandLine
{
    internal sealed class DotNetDumpTarget
    {
        private readonly FileInfo? dumpFile;
        private readonly int? dumpProcessId;
        private readonly int degreeParallelism;
        private readonly int minCount;
        private readonly int minAsyncSize;
        private readonly bool overwrite;
        private readonly bool quiet;
        private readonly TextWriter resultWriter;
        private readonly FileInfo? dotNetDump;
        private readonly FileInfo? saveDumpTo;
        private readonly FileInfo? saveReportTo;

        internal DotNetDumpTarget(
            int degreeParallelism,
            FileInfo? dotNetDump,
            FileInfo? dumpFile,
            int? dumpProcessId, 
            int minCount,
            int minAsyncSize,
            bool overwrite,
            bool quiet,
            TextWriter resultWriter,
            FileInfo? saveDumpTo,
            FileInfo? saveReportTo
        )
        {
            this.dumpFile = dumpFile;
            this.dumpProcessId = dumpProcessId;
            this.degreeParallelism = degreeParallelism;
            this.minCount = minCount;
            this.minAsyncSize = minAsyncSize;
            this.overwrite = overwrite;
            this.quiet = quiet;
            this.resultWriter = resultWriter;
            this.dotNetDump = dotNetDump;
            this.saveReportTo = saveReportTo;
            this.saveDumpTo = saveDumpTo;
        }

        internal async ValueTask<(ExitCodes Result, string? ErrorMessage)> RunAsync()
        {
            string? dotnetDumpPath;
            if (dotNetDump == null)
            {
                if (!DotNetToolFinder.TryFind("dotnet-dump", out dotnetDumpPath, out var error))
                {
                    return (ExitCodes.CouldNotFindDotNetDump, $"Could not find dotnet-dump: {error}");
                }
            }
            else
            {
                if (!dotNetDump.Exists)
                {
                    return (ExitCodes.CouldNotFindDotNetDump, $"dotnet-dump does not exist at: {dotNetDump.FullName}");
                }

                dotnetDumpPath = dotNetDump.FullName;
            }

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

            Report(resultWriter, $"dotnet-dump location: {dotnetDumpPath}", quiet);

            bool deleteDumpFile;
            string dumpFilePath;
            if (dumpFile == null)
            {
                if (saveDumpTo != null)
                {
                    if (saveDumpTo.Exists && !overwrite)
                    {
                        return (ExitCodes.DumpFileExists, $"Dump file already exists: {saveDumpTo}");
                    }

                    dumpFilePath = saveDumpTo.FullName;
                    deleteDumpFile = false;

                    var dumpDir = Path.GetDirectoryName(dumpFilePath);
                    if (dumpDir == null)
                    {
                        return (ExitCodes.DumpFileDirectoryError, $"Could not get directory for: {dumpFilePath}");
                    }

                    Directory.CreateDirectory(dumpDir);
                }
                else
                {
                    dumpFilePath = Path.GetTempFileName();
                    File.Delete(dumpFilePath);
                    deleteDumpFile = true;
                }

                if (dumpProcessId == null)
                {
                    throw new Exception("Shouldn't be possible");
                }

                Report(resultWriter, $"Taking dump of process id: {dumpProcessId.Value}", quiet);
                var (success, log) = await DumpProcess.TakeDumpAsync(dotnetDumpPath, dumpProcessId.Value, dumpFilePath);

                if (!success)
                {
                    return (ExitCodes.DumpFailed, log);
                }
            }
            else
            {
                if (!dumpFile.Exists)
                {
                    return (ExitCodes.CouldNotFindDumpFile, $"Could not find dump file: {dumpFile.FullName}");
                }

                dumpFilePath = dumpFile.FullName;
                deleteDumpFile = false;
            }

            Report(resultWriter, $"Analyzing dump file: {dumpFilePath}", quiet);

            try
            {
                var prog = new ProgressWrapper(prog => ReportProgress(resultWriter, prog, quiet));

                await using var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dotnetDumpPath, dumpFilePath, degreeParallelism, prog).ConfigureAwait(false);
                var res = await diag.AnalyzeAsync().ConfigureAwait(false);

                Report(resultWriter, "Analyzing complete", quiet);

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
            }
            finally
            {
                if (deleteDumpFile)
                {
                    Report(resultWriter, $"Removing dump file", quiet);

                    var attempt = 0;
                    while (attempt < 3)
                    {
                        try
                        {
                            File.Delete(dumpFilePath);
                            break;
                        }
                        catch { }

                        await Task.Delay(100).ConfigureAwait(false);
                        attempt++;
                    }
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
