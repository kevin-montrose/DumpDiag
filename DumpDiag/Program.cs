using DumpDiag.Impl;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DumpDiag
{
    public sealed class Program
    {
        public static async Task Main(string[] args)
        {
            var dotnetDumpOption =
                new Option<FileInfo?>(
                    new[] { "-ddp", "--dotnet-dump-path" },
                    getDefaultValue:
                    () =>
                    {
                        if (DotNetToolFinder.TryFind("dotnet-dump", out var path, out _))
                        {
                            return new FileInfo(path);
                        }

                        return null;
                    },
                    description: "Path to dotnet-dump executable, will be inferred if omitted"
                );
            var dumpFileOption =
                new Option<FileInfo?>(
                    new[] { "-df", "--dump-file" },
                    getDefaultValue: () => null,
                    description: "Existing full process dump to analyze"
                );
            var dumpPidOption =
                new Option<int?>(
                    new[] { "-dpid", "--dump-process-id" },
                    getDefaultValue: () => null,
                    description: "Id of .NET process to analyze"
                );
            var degreeParallelism =
                new Option<int>(
                    new[] { "-dp", "--degree-parallelism" },
                    getDefaultValue: static () => Math.Max(1, Environment.ProcessorCount - 1),
                    description: "How many processes to use to analyze the dump"
                );
            var saveDumpOption =
                new Option<FileInfo?>(
                    new[] { "-sd", "--save-dump-file" },
                    getDefaultValue: () => null,
                    description: "Used in conjunction with --dump-process-id, saves a new full process dump to the given file"
                );
            var reportFileOption =
                new Option<FileInfo?>(
                    new[] { "-rf", "--report-file" },
                    getDefaultValue: () => null,
                    description: "Instead of writing to standard out, saves diagnostic report to the given file"
                );
            var minCountOption =
                new Option<int>(
                    new[] { "-mc", "--min-count" },
                    getDefaultValue: () => 1,
                    description: "Minimum count of strings, char[], type instances, etc. to include in analysis"
                );
            var minAsyncSizeOption =
                new Option<int>(
                    new[] { "-mas", "--min-async-size" },
                    getDefaultValue: () => 1,
                    description: "Minimum size (in bytes) of async state machines to include a field breakdown in analysis"
                );
            var overwriteOption =
                new Option<bool>(
                    new[] { "-o", "--overwrite" },
                    getDefaultValue: static () => false,
                    description: "Overwrite --report-file and --dump-file if they exist"
                );
            var quietOption =
                new Option<bool>(
                    new[] { "-q", "--quiet" },
                    getDefaultValue: static () => false,
                    description: "Suppress progress updates"
                );
            var commands = new RootCommand { dotnetDumpOption, dumpFileOption, dumpPidOption, degreeParallelism, saveDumpOption, minCountOption, minAsyncSizeOption, reportFileOption, overwriteOption, quietOption };

            commands.Handler = CommandHandler.Create<FileInfo?, FileInfo?, int?, int, FileInfo?, int, int, FileInfo?, bool, bool>(
                async static (dotnetDumpPath, dumpFile, dumpProcessId, degreeParallelism, saveDumpFile, minCount, minAsyncSize, reportFile, overwrite, quiet) =>
                {
                    if (dumpFile == null && dumpProcessId == null)
                    {
                        Console.Error.WriteLine("One of --dump-file or --dump-process-id must be provided");
                        Environment.Exit(-2);
                    }

                    if (dumpFile != null && dumpProcessId != null)
                    {
                        Console.Error.WriteLine("Only one of --dump-file and --dump-process-id can be provided");
                        Environment.Exit(-3);
                    }

                    if (saveDumpFile != null && dumpProcessId == null)
                    {
                        Console.Error.WriteLine("--save-dump-file must be used with --dump-process-id");
                        Environment.Exit(-4);
                    }

                    if (degreeParallelism <= 0)
                    {
                        Console.Error.WriteLine("--degree-parallelism must be >= 1");
                        Environment.Exit(-5);
                    }

                    if (minCount < 1)
                    {
                        Console.Error.WriteLine("--min-count must be >= 1");
                        Environment.Exit(-6);
                    }

                    if (minAsyncSize < 1)
                    {
                        Console.Error.WriteLine("--min-async-size must be >= 1");
                        Environment.Exit(-7);
                    }

                    await RunAsync(
                        dotnetDumpPath,
                        dumpFile,
                        dumpProcessId,
                        degreeParallelism,
                        saveDumpFile,
                        minCount,
                        minAsyncSize,
                        reportFile,
                        overwrite,
                        quiet
                    );
                }
            );

            await commands.InvokeAsync(args);
        }

        private sealed class ProgressWrapper : IProgress<DumpDiagnoserProgress>
        {
            private readonly Action<DumpDiagnoserProgress> del;

            internal ProgressWrapper(Action<DumpDiagnoserProgress> del)
            {
                this.del = del;
            }

            public void Report(DumpDiagnoserProgress progress)
            => del(progress);
        }

        private static async ValueTask RunAsync(
            FileInfo? dotnetDump,
            FileInfo? dumpFile,
            int? dumpPid,
            int degreeParallelism,
            FileInfo? saveDumpTo,
            int minCount,
            int minAsyncStateMachineSize,
            FileInfo? saveReportTo,
            bool overwrite,
            bool quiet
        )
        {
            // todo: async state machine size minimum

            string? dotnetDumpPath;
            if (dotnetDump == null)
            {
                if (!DotNetToolFinder.TryFind("dotnet-dump", out dotnetDumpPath, out var error))
                {
                    Console.Error.WriteLine($"Could not find dotnet-dump: {error}");
                    Environment.Exit(-8);
                }
            }
            else
            {
                if (!dotnetDump.Exists)
                {
                    Console.Error.WriteLine($"dotnet-dump does not exist at: {dotnetDump.FullName}");
                    Environment.Exit(-9);
                }

                dotnetDumpPath = dotnetDump.FullName;
            }

            string? saveReportToPath;
            if (saveReportTo != null)
            {
                if (saveReportTo.Exists && !overwrite)
                {
                    Console.Error.WriteLine($"Report file already exists: {saveReportTo.FullName}");
                    Environment.Exit(-10);
                }

                saveReportToPath = saveReportTo.FullName;

                Report($"Writing report to: {saveReportToPath}", quiet);
            }
            else
            {
                saveReportToPath = null;

                Report("Writing report to standard output", quiet);
            }

            Report($"dotnet-dump location: {dotnetDumpPath}", quiet);

            bool deleteDumpFile;
            string dumpFilePath;
            if (dumpFile == null)
            {
                if (saveDumpTo != null)
                {
                    if (saveDumpTo.Exists && !overwrite)
                    {
                        Console.Error.WriteLine($"Dump file already exists: {saveDumpTo}");
                        Environment.Exit(-11);
                    }

                    dumpFilePath = saveDumpTo.FullName;
                    deleteDumpFile = false;


                    var dumpDir = Path.GetDirectoryName(dumpFilePath);
                    if (dumpDir == null)
                    {
                        Console.Error.WriteLine($"Could not get directory for: {dumpFilePath}");
                        Environment.Exit(-12);
                    }

                    Directory.CreateDirectory(dumpDir);
                }
                else
                {
                    dumpFilePath = Path.GetTempFileName();
                    File.Delete(dumpFilePath);
                    deleteDumpFile = true;
                }

                if (dumpPid == null)
                {
                    throw new Exception("Shouldn't be possible");
                }

                Report($"Taking dump of process id: {dumpPid.Value}", quiet);
                var (success, log) = await DumpProcess.TakeDumpAsync(dotnetDumpPath, dumpPid.Value, dumpFilePath);

                if (!success)
                {
                    Console.Error.WriteLine("Dump failed");
                    Console.Error.WriteLine(log);
                    Environment.Exit(-13);
                }
            }
            else
            {
                if (!dumpFile.Exists)
                {
                    Console.Error.WriteLine($"Could not find dump file: {dumpFile.FullName}");
                    Environment.Exit(-14);
                }

                dumpFilePath = dumpFile.FullName;
                deleteDumpFile = false;
            }

            Report($"Analyzing dump file: {dumpFilePath}", quiet);

            try
            {
                var prog = new ProgressWrapper(prog => ReportProgress(prog, quiet));

                await using var diag = await DumpDiagnoser.CreateAsync(dotnetDumpPath, dumpFilePath, degreeParallelism, prog);
                var res = await diag.AnalyzeAsync();

                Report("Analyzing complete", quiet);

                if (saveReportToPath == null)
                {
                    if (!quiet)
                    {
                        Console.Out.WriteLine();
                        Console.Out.WriteLine("---");
                        Console.Out.WriteLine();
                    }

                    using (var writer = new StringWriter())
                    {
                        await res.WriteToAsync(writer, minCount, minAsyncStateMachineSize);

                        Console.Out.WriteLine(writer.ToString());
                    }
                }
                else
                {
                    Report($"Saving report to: {saveReportTo}", quiet);

                    using (var fs = File.CreateText(saveReportToPath))
                    {
                        await res.WriteToAsync(fs, minCount, minAsyncStateMachineSize);
                    }
                }
            }
            finally
            {
                if (deleteDumpFile)
                {
                    Report($"Removing dump file", quiet);

                    var attempt = 0;
                    while (attempt < 3)
                    {
                        try
                        {
                            File.Delete(dumpFilePath);
                            break;
                        }
                        catch { }

                        await Task.Delay(100);
                        attempt++;
                    }
                }
            }

            static void Report(string message, bool quiet)
            {
                if (quiet)
                {
                    return;
                }

                Console.Out.Write($"[{DateTime.UtcNow:u}]: ");
                Console.Out.WriteLine(message);
            }

            static void ReportProgress(DumpDiagnoserProgress progress, bool quiet)
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

                Console.Out.Write($"[{DateTime.UtcNow:u}]: ");
                Console.Out.WriteLine(str);
            }
        }
    }
}
