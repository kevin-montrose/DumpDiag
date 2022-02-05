using DumpDiag.Impl;
using DumpDiag.CommandLine;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Runtime.Versioning;
using System.Threading.Tasks;

using static DumpDiag.CommandLine.CommandLineArguments;
using static DumpDiag.CommandLine.CommandLineVerbs;

using Command = System.CommandLine.Command;
using System.Collections.Immutable;

namespace DumpDiag
{
    public sealed class Program
    {
        public static async Task Main(string[] args)
        {
            var root = new RootCommand();

            root.AddCommand(CreateDotNetDumpCommand());

            if (OperatingSystem.IsWindows())
            {
                root.AddCommand(CreateRemoteWinDbgCommand());
            }

            await root.InvokeAsync(args);
        }

        private static Command CreateDotNetDumpCommand()
        {
            var command = new Command(DOTNET_DUMP);

            var dotnetDumpOption =
                new Option<FileInfo?>(
                    new[] { DOTNET_DUMP_PATH_SHORT, DOTNET_DUMP_PATH_LONG },
                    getDefaultValue:
                    static () =>
                    {
                        if (DotNetToolFinder.TryFind("dotnet-dump", out var path, out _))
                        {
                            return new FileInfo(path);
                        }

                        return null;
                    },
                    description: "Path to dotnet-dump executable, will be inferred if omitted"
                );
            command.AddOption(dotnetDumpOption);

            var dumpFileOption =
                new Option<FileInfo?>(
                    new[] { DUMP_FILE_PATH_SHORT, DUMP_FILE_PATH_LONG },
                    getDefaultValue: () => null,
                    description: "Existing full process dump to analyze"
                );
            command.AddOption(dumpFileOption);

            var dumpPidOption =
                new Option<int?>(
                    new[] { DUMP_PROCESS_ID_SHORT, DUMP_PROCESS_ID_LONG },
                    getDefaultValue: () => null,
                    description: "Id of .NET process to analyze"
                );
            command.AddOption(dumpPidOption);

            var degreeParallelism =
                new Option<int>(
                    new[] { DEGREE_PARALLELISM_SHORT, DEGREE_PARALLELISM_LONG },
                    getDefaultValue: static () => Math.Max(1, Environment.ProcessorCount - 1),
                    description: "How many processes to use to analyze the dump"
                );
            command.AddOption(degreeParallelism);

            var saveDumpOption =
                new Option<FileInfo?>(
                    new[] { SAVE_DUMP_FILE_PATH_SHORT, SAVE_DUMP_FILE_PATH_LONG },
                    getDefaultValue: () => null,
                    description: $"Used in conjunction with {DUMP_PROCESS_ID_LONG}, saves a new full process dump to the given file"
                );
            command.AddOption(saveDumpOption);

            var reportFileOption =
                new Option<FileInfo?>(
                    new[] { REPORT_FILE_PATH_SHORT, REPORT_FILE_PATH_LONG },
                    getDefaultValue: () => null,
                    description: "Instead of writing to standard out, saves diagnostic report to the given file"
                );
            command.AddOption(reportFileOption);

            var resumeFileOption =
                new Option<FileInfo?>(
                    new[] { RESUMPTION_STATE_FILE_PATH_SHORT, RESUMPTION_STATE_FILE_PATH_LONG },
                    getDefaultValue: () => null,
                    description: "Store state to allow resumption of analysis to the given file"
                );
            command.Add(resumeFileOption);

            var minCountOption =
                new Option<int>(
                    new[] { MIN_COUNT_SHORT, MIN_COUNT_LONG },
                    getDefaultValue: () => 1,
                    description: "Minimum count of strings, char[], type instances, etc. to include in analysis"
                );
            command.AddOption(minCountOption);

            var minAsyncSizeOption =
                new Option<int>(
                    new[] { MIN_ASYNC_SIZE_SHORT, MIN_ASYNC_SIZE_LONG },
                    getDefaultValue: () => 1,
                    description: "Minimum size (in bytes) of async state machines to include a field breakdown in analysis"
                );
            command.AddOption(minAsyncSizeOption);

            var overwriteOption =
                new Option<bool>(
                    new[] { OVERWRITE_SHORT, OVERWRITE_LONG },
                    getDefaultValue: static () => false,
                    description: $"Overwrite {REPORT_FILE_PATH_LONG} and {DUMP_FILE_PATH_LONG} if they exist"
                );
            command.AddOption(overwriteOption);

            var quietOption =
                new Option<bool>(
                    new[] { QUIET_SHORT, QUIET_LONG },
                    getDefaultValue: static () => false,
                    description: "Suppress progress updates"
                );
            command.AddOption(quietOption);

            command.SetHandler<FileInfo?, FileInfo?, int?, int, FileInfo?, int, int, FileInfo?, FileInfo?, bool, bool>(
                static async (dotnetDumpPath, dumpFile, dumpProcessId, degreeParallelism, saveDumpFile, minCount, minAsyncSize, reportFile, resumeFile, overwrite, quiet) =>
                {
                    var target = CreateAndValidateDotNetDumpTarget(degreeParallelism, dotnetDumpPath, dumpFile, dumpProcessId, minCount, minAsyncSize, reportFile, resumeFile, saveDumpFile, overwrite, quiet);
                    var (code, error) = await target.RunAsync().ConfigureAwait(false);

                    if (code != ExitCodes.Success)
                    {
                        Console.Error.WriteLine(error);
                        Exit(code);
                    }
                },
                dotnetDumpOption,
                dumpFileOption,
                dumpPidOption,
                degreeParallelism,
                saveDumpOption,
                minCountOption,
                minAsyncSizeOption,
                reportFileOption,
                resumeFileOption,
                overwriteOption,
                quietOption
            );

            return command;

            static DotNetDumpTarget CreateAndValidateDotNetDumpTarget(
                int degreeParallelism,
                FileInfo? dotNetDump,
                FileInfo? dumpFile,
                int? dumpProcessId,
                int minCount,
                int minAsyncSize,
                FileInfo? reportFile,
                FileInfo? resumeFile,
                FileInfo? saveDumpFile,
                bool overwrite,
                bool quiet
            )
            {
                if (dumpFile != null && dumpProcessId != null)
                {
                    Console.Error.WriteLine($"Only one of {DUMP_FILE_PATH_LONG} and {DUMP_PROCESS_ID_LONG} can be provided");
                    Exit(ExitCodes.BothDumpAndProcessSpecified);
                }

                if (saveDumpFile != null && dumpProcessId == null)
                {
                    Console.Error.WriteLine($"{SAVE_DUMP_FILE_PATH_LONG} must be used with {DUMP_PROCESS_ID_LONG}");
                    Exit(ExitCodes.SaveDumpFileMustHaveDumpProcessId);
                }

                if (degreeParallelism < 1)
                {
                    Console.Error.WriteLine($"{DEGREE_PARALLELISM_LONG} must be >= 1");
                    Exit(ExitCodes.DegreeParallelismTooLow);
                }

                CommonChecks(minAsyncSize, minCount);

                return
                    new DotNetDumpTarget(
                        degreeParallelism,
                        dotNetDump,
                        dumpFile,
                        dumpProcessId,
                        minCount,
                        minAsyncSize,
                        overwrite,
                        quiet,
                        Console.Out,
                        saveDumpFile,
                        reportFile,
                        resumeFile
                    );
            }
        }

        [SupportedOSPlatform("windows")]
        private static Command CreateRemoteWinDbgCommand()
        {
            var command = new Command(WINDBG);

            var connectionStringOptions =
                new Option<string[]?>(
                    new[] { WINDBG_CONNECTION_STRING_LONG, WINDBG_CONNECTION_STRING_SHORT },
                    description: "Connection details for remote WinDbg session, formatted like <ip>:port"
                )
                {
                    Arity = ArgumentArity.OneOrMore
                };
            command.AddOption(connectionStringOptions);

            var dbgEngDllOption =
                new Option<FileInfo?>(
                    new[] { DBGENG_DLL_PATH_LONG, DBGENG_DLL_PATH_SHORT },
                    getDefaultValue:
                        static () =>
                        {
                            if (DbgEngFinder.TryFindDefault(out var path))
                            {
                                return new FileInfo(path);
                            }

                            return null;
                        },
                    description: "Path to dbgeng.dll matching remote WinDbg version, will be inferred if omitted"
                );
            command.AddOption(dbgEngDllOption);

            var reportFileOption =
                new Option<FileInfo?>(
                    new[] { REPORT_FILE_PATH_SHORT, REPORT_FILE_PATH_LONG },
                    getDefaultValue: () => null,
                    description: "Instead of writing to standard out, saves diagnostic report to the given file"
                );
            command.AddOption(reportFileOption);

            var resumeFileOption =
               new Option<FileInfo?>(
                   new[] { RESUMPTION_STATE_FILE_PATH_SHORT, RESUMPTION_STATE_FILE_PATH_LONG },
                   getDefaultValue: () => null,
                   description: "Store state to allow resumption of analysis to the given file"
               );
            command.Add(resumeFileOption);

            var minCountOption =
                new Option<int>(
                    new[] { MIN_COUNT_SHORT, MIN_COUNT_LONG },
                    getDefaultValue: () => 1,
                    description: "Minimum count of strings, char[], type instances, etc. to include in analysis"
                );
            command.AddOption(minCountOption);

            var minAsyncSizeOption =
                new Option<int>(
                    new[] { MIN_ASYNC_SIZE_SHORT, MIN_ASYNC_SIZE_LONG },
                    getDefaultValue: () => 1,
                    description: "Minimum size (in bytes) of async state machines to include a field breakdown in analysis"
                );
            command.AddOption(minAsyncSizeOption);

            var overwriteOption =
                new Option<bool>(
                    new[] { OVERWRITE_SHORT, OVERWRITE_LONG },
                    getDefaultValue: static () => false,
                    description: $"Overwrite {REPORT_FILE_PATH_LONG} if it exists"
                );
            command.AddOption(overwriteOption);

            var quietOption =
                new Option<bool>(
                    new[] { QUIET_SHORT, QUIET_LONG },
                    getDefaultValue: static () => false,
                    description: "Suppress progress updates"
                );
            command.AddOption(quietOption);

           command.SetHandler<string[]?, FileInfo?, int, int, bool, bool, FileInfo?, FileInfo?>(
                static async (connectionStrings, dbgEngPath, minCount, minAsync, overwrite, quiet, reportFile, resumeFile) =>
                {
                    var target = CreateAndValidateRemoteWinDbg(connectionStrings, dbgEngPath, minAsync, minCount, reportFile, resumeFile, overwrite, quiet);
                    var (code, error) = await target.RunAsync().ConfigureAwait(false);

                    if (code != ExitCodes.Success)
                    {
                        Console.Error.WriteLine(error);
                        Exit(code);
                    }
                },
                connectionStringOptions, dbgEngDllOption,minCountOption, minAsyncSizeOption, overwriteOption, quietOption, reportFileOption, resumeFileOption
            );

            return command;

            static RemoteWinDbgTarget CreateAndValidateRemoteWinDbg(
                string[]? connectionStrings,
                FileInfo? dbgEngPath,
                int minAsyncSize,
                int minCount,
                FileInfo? reportFile,
                FileInfo? resumeFile,
                bool overwrite,
                bool quiet
            )
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new Exception("Shouldn't be possible");
                }

                if (dbgEngPath == null)
                {
                    Console.Error.WriteLine($"{DBGENG_DLL_PATH_LONG} must be set");
                    Exit(ExitCodes.DbgEngDllPathNotSet);
                }

                ImmutableList<RemoteWinDbgAddress> connectionStringsParsed;
                if (connectionStrings == null || connectionStrings.Length == 0)
                {
                    connectionStringsParsed = ImmutableList<RemoteWinDbgAddress>.Empty;

                    Console.Error.WriteLine($"{WINDBG_CONNECTION_STRING_LONG} must be set");
                    Exit(ExitCodes.WindbgConnectionStringNotSet);
                }
                else
                {
                    var connectionStringsBuilder = ImmutableList.CreateBuilder<RemoteWinDbgAddress>();
                    foreach(var cs in connectionStrings)
                    {
                        if(!RemoteWinDbgAddress.TryParse(cs, out var addr, out var error))
                        {
                            Console.Error.WriteLine($"Bad connection string: {cs} (error was: {error})");
                            Exit(ExitCodes.WindbgConnectionStringBad);
                        }

                        connectionStringsBuilder.Add(addr);
                    }

                    connectionStringsParsed = connectionStringsBuilder.ToImmutable();
                }

                CommonChecks(minAsyncSize, minCount);

                return new RemoteWinDbgTarget(dbgEngPath, connectionStringsParsed, minAsyncSize, minCount, overwrite, quiet, Console.Out, reportFile, resumeFile);
            }
        }

        private static void CommonChecks(int minAsyncSize, int minCount)
        {
            if (minCount < 1)
            {
                Console.Error.WriteLine($"{MIN_COUNT_LONG} must be >= 1");
                Exit(ExitCodes.MinCountTooLow);
            }

            if (minAsyncSize < 1)
            {
                Console.Error.WriteLine($"{MIN_ASYNC_SIZE_LONG} must be >= 1");
                Exit(ExitCodes.MinAsyncSizeTooLow);
            }
        }

        [DoesNotReturn]
        private static void Exit(ExitCodes code)
        {
            Environment.Exit((int)code);
        }
    }
}
