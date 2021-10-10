# DumpDiag

Pre-Release - this probably has loads of bugs!

## Install

`dotnet tool install DumpDiag`

DumpDiag assumes [`dotnet-dump`](https://docs.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump) is also installed.

## Options

Run with `dotnet dumpdiag` after installation.

```
Usage:
  DumpDiag [options]

Options:
  -ddp, --dotnet-dump-path <dotnet-dump-path>     Path to dotnet-dump executable, will be inferred if omitted [default: ]
  -df, --dump-file <dump-file>                    Existing full process dump to analyze [default: ]
  -dpid, --dump-process-id <dump-process-id>      Id of .NET process to analyze [default: ]
  -dp, --degree-parallelism <degree-parallelism>  How many processes to use to analyze the dump [default: <num cores - 1>]
  -sd, --save-dump-file <save-dump-file>          Used in conjunction with --dump-process-id, saves a new full process dump to the given file [default: ]
  -mc, --min-count <min-count>                    Minimum count of strings, char[], type instances, etc. to include in analysis [default: 1]
  -rf, --report-file <report-file>                Instead of writing to standard out, saves diagnostic report to the given file [default: ]
  -o, --overwrite                                 Overwrite --report-file and --dump-file if they exist [default: False]
  -q, --quiet                                     Suppress progress updates [default: False]
  --version                                       Show version information
  -?, -h, --help                                  Show help and usage information
```

Only one of `--dump-file` and `--dump-process-id` can be specified.

## Remarks

DumpDiag performs routine analysis of a dump of a .NET process using `dotnet-dump`:

 - Counts of live and dead types on the heap
 - Counts of strings on the heap, by content
 - Counts of char[]s on the heap, by content
 - All managed thread call stacks
 - Counts of unique stack frames from managed threads

Constraints for `dotnet-dump` apply equally to DumpDiag, primarily that dumps must be analyzed on the same OS as they were captured on.