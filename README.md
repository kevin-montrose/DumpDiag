# DumpDiag

Pre-Release - this probably has loads of bugs!

## Install

`dotnet tool install DumpDiag`

DumpDiag assumes [`dotnet-dump`](https://docs.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump) is also installed.

## Options

Run with `dumpdiag` after installation, assuming global installation.

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

## Example Output

This analysis is of a LinqPad process.

```
[2021-10-10 03:03:43Z]: Writing report to standard output
[2021-10-10 03:03:43Z]: dotnet-dump location: C:\Users\kevin\.dotnet\tools\dotnet-dump.exe
[2021-10-10 03:03:43Z]: Taking dump of process id: 17404
[2021-10-10 03:03:44Z]: Analyzing dump file: C:\Users\kevin\AppData\Local\Temp\tmp6A6D.tmp
[2021-10-10 03:03:44Z]: starting: 6.7%
[2021-10-10 03:03:44Z]: starting: 13.3%
[2021-10-10 03:03:44Z]: starting: 20%

...

[2021-10-10 03:04:21Z]: Analyzing complete

Types
=====
 Total     Dead     Live   Value
--------------------------------
31,631       70   31,561   System.String
10,805   10,785       20   System.Action
 3,105       90    3,015   System.EventHandler
   816        0      816   System.Func`1[[System.Collections.Generic.IDictionary`2[[System.String, System.Private.CoreLib],[System.Object, System.Private.CoreLib]], System.Private.CoreLib]]
   586        0      586   System.Action`2[[System.Int32, System.Private.CoreLib],[System.Int32, System.Private.CoreLib]]
   410        0      410   System.Func`1[[Microsoft.CodeAnalysis.IdentifierCollection, Microsoft.CodeAnalysis]]
   353        0      353   System.Windows.Forms.ToolStripDropDownClosedEventHandler
   353        0      353   System.Windows.Forms.ToolStripItemClickedEventHandler
   286        0      286   System.Windows.Forms.MouseEventHandler

...

Delegates
=========
 Total     Dead   Live   Value
------------------------------
10,785   10,785      0   LINQPad.AsyncDisposable.<TryDeferDisposal>b__9_0()
   816        0    816   System.Composition.TypedParts.Discovery.DiscoveredPart+<>c__DisplayClass11_0.<.ctor>b__0()
   689        0    689   System.Windows.Forms.ToolStripDropDownItem.ToolStrip_RescaleConstants(Int32, Int32)
   515        0    515   System.Composition.Hosting.Providers.Lazy.LazyWithMetadataExportDescriptorProvider+<>c__DisplayClass2_2`2[[System.__Canon, System.Private.CoreLib],[System.__Canon, System.Private.CoreLib]].<GetLazyDefinitions>b__4()
   396        0    396   System.Windows.Forms.ToolStripItem.ToolStrip_RescaleConstants(Int32, Int32)
   353        0    353   System.Windows.Forms.ToolStripDropDownItem.DropDown_ItemClicked(System.Object, System.Windows.Forms.ToolStripItemClickedEventArgs)

...

Strings
=======
Total   Dead   Live   Value
---------------------------
  698      0    698   C#
  545      0    545   Visual Basic
  431      1    430   Default
  383     19    364   System
  287      0    287   Microsoft
  278      0    278   Runtime
  256      0    256   CompilerServices
  206      0    206   .rsrc
  206      0    206   .reloc
  206      0    206   .text
  184      0    184   FixableDiagnosticIds

...

Char[]
======
Total   Dead   Live   Value
---------------------------
   17      0     17   ESCAPED: "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"

...

Unique Stack Frames
===================
Count   Call Site
-----------------
    2   [InlinedCallFrame: 000000b1e117da30] System.Windows.Forms.UnsafeNativeMethods.WaitMessage()
    2   [InlinedCallFrame: 000000b1e2afee38] Interop+Kernel32.ConnectNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle, IntPtr)
    1   [DebuggerU2MCatchHandlerFrame: 000000b1e18ffa60] 
    1   [DebuggerU2MCatchHandlerFrame: 000000b1e2aff5f0] 
    1   [GCFrame: 000000b1e117e648] 
    1   [GCFrame: 000000b1e117ebe0] 
    1   [GCFrame: 000000b1e2aff388] 

...

Call Stacks
===========
Thread #0: 13 frames
--------------------
[InlinedCallFrame: 000000b1e117da30] System.Windows.Forms.UnsafeNativeMethods.WaitMessage()
[InlinedCallFrame: 000000b1e117da30] System.Windows.Forms.UnsafeNativeMethods.WaitMessage()
ILStubClass.IL_STUB_PInvoke()
System.Windows.Forms.Application+ComponentManager.System.Windows.Forms.UnsafeNativeMethods.IMsoComponentManager.FPushMessageLoop(IntPtr, Int32, Int32) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 2016]
System.Windows.Forms.Application+ThreadContext.RunMessageLoopInner(Int32, System.Windows.Forms.ApplicationContext) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 3370]

```
