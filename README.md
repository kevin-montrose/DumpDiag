# DumpDiag

Pre-Release - this probably has loads of bugs!

**Note: currently only supports x64 processes**

## Install

`dotnet tool install DumpDiag`

DumpDiag assumes [`dotnet-dump`](https://docs.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump) is also installed.

## Options

Run with `dumpdiag` after installation, assuming global installation.

```
Usage:
  DumpDiag [options]

Options:
  -ddp, --dotnet-dump-path <dotnet-dump-path>     Path to dotnet-dump executable, will be inferred if omitted [default:
                                                  C:\Users\kevin\.dotnet\tools\dotnet-dump.exe]
  -df, --dump-file <dump-file>                    Existing full process dump to analyze [default: ]
  -dpid, --dump-process-id <dump-process-id>      Id of .NET process to analyze [default: ]
  -dp, --degree-parallelism <degree-parallelism>  How many processes to use to analyze the dump [default: <num cores -1>]
  -sd, --save-dump-file <save-dump-file>          Used in conjunction with --dump-process-id, saves a new full process dump to the given file [default: ]
  -mc, --min-count <min-count>                    Minimum count of strings, char[], type instances, etc. to include in analysis [default: 1]
  -mas, --min-async-size <min-async-size>         Minimum size (in bytes) of async state machines to include a field breakdown in analysis [default: 1]
  -rf, --report-file <report-file>                Instead of writing to standard out, saves diagnostic report to the given file [default: ]
  -o, --overwrite                                 Overwrite --report-file and --dump-file if they exist [default: False]
  -q, --quiet                                     Suppress progress updates [default: False]
  --version                                       Show version information
  -?, -h, --help                                  Show help and usage information
```

Only one of `--dump-file` and `--dump-process-id` can be specified.

## Remarks

DumpDiag performs routine analysis of a dump of a .NET process using `dotnet-dump`:

 - Counts of values on the heap, by type
 - Counts of strings on the heap, by content
 - Counts of char[]s on the heap, by content
 - Counts deleges on the heap, by backing method
 - Counts of async state machines, by "backing" method
 - Locals captured by async state machines
 - All managed thread call stacks
 - Counts of unique stack frames from managed threads
 - Heap fragmentation by generation, LOH, and POH
 - Pinned objects including by generation, LOH, and POH

Where relevant total, live, and dead counts are reported.  High % of dead references (especially over time) can indicate inefficient allocation patterns.

High fragementation in a generation, coupled with large numbers of pinned objects in that generation, can indicate holding pins for too long.

Constraints for `dotnet-dump` apply equally to DumpDiag, primarily that dumps must be analyzed on the same OS as they were captured on.

## Example Output

This analysis is of a LinqPad process.

```
[2021-12-30 21:21:36Z]: Writing report to standard output
[2021-12-30 21:21:36Z]: dotnet-dump location: C:\Users\kevin\.dotnet\tools\dotnet-dump.exe
[2021-12-30 21:21:36Z]: Taking dump of process id: 18932
[2021-12-30 21:21:37Z]: Analyzing dump file: C:\Users\kevin\AppData\Local\Temp\tmpE6A6.tmp
[2021-12-30 21:21:38Z]: starting: 6.7%
[2021-12-30 21:21:38Z]: starting: 13.3%
[2021-12-30 21:21:38Z]: starting: 20%

...

---

Types
=====
     Total(bytes)      Dead(bytes)         Live(bytes)   Value
--------------------------------------------------------------
28,611(2,378,450)        20(1,162)   28,591(2,377,288)   System.String
   7,457(477,248)   7,441(476,224)           16(1,024)   System.Action
   3,020(193,280)           4(256)      3,016(193,024)   System.EventHandler
      69(111,276)        10(2,416)         59(108,860)   System.Char[]
      816(52,224)             0(0)         816(52,224)   System.Func`1[[System.Collections.Generic.IDictionary`2[[System.String, System.Private.CoreLib],[System.Object, System.Private.CoreLib]], System.Private.CoreLib]]

...

Delegates
=========
Total    Dead   Live   Value
----------------------------
7,438   7,438      0   LINQPad.AsyncDisposable.<TryDeferDisposal>b__9_0()
  816       0    816   System.Composition.TypedParts.Discovery.DiscoveredPart+<>c__DisplayClass11_0.<.ctor>b__0()
  689       0    689   System.Windows.Forms.ToolStripDropDownItem.ToolStrip_RescaleConstants(Int32, Int32)
  396       0    396   System.Windows.Forms.ToolStripItem.ToolStrip_RescaleConstants(Int32, Int32)
  353       0    353   System.Windows.Forms.ToolStripDropDownItem.DropDown_Closed(System.Object, System.Windows.Forms.ToolStripDropDownClosedEventArgs)

...

Strings
=======
Total   Dead   Live   Value
---------------------------
  696      0    696   C#
  545      0    545   Visual Basic
  430      0    430   Default
  206      0    206   .rsrc
  206      0    206   .text

...

Char[]
======
Total   Dead   Live   Value
---------------------------
   16      2     14   ESCAPED: "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    2      2      0   ESCAPED: "Assuming assembly reference 'System.Runtime, Version=4.2.1.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a' used by 'LINQPad.Runtime' matches identity 'System.Runtime, Version=5.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a' of 'System.Runtime', you may need to supply runtime policy\0\0\0"
    1      1      0   ESCAPED: "yrqkk\" \"18932\" \"True\" \"False\" \"False\"\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    1      1      0   am Files\LINQPad6\LINQPad.Runtime.dll"
    1      1      0   "*:PS" "C:\Program Files\LINQPad6\LINQPad.Runtime.dll" "C:\Program Files\LINQPad6" "LINQPad6.iibyrqkk.nxadkcgdir.2" "iib

...

Async State Machines
====================
Total(bytes)   Dead(bytes)   Live(bytes)   Value
------------------------------------------------
      2(192)        2(192)          0(0)   LINQPad.UI.QueryEditor+<CheckSuggestionsSmartTagAtCaret>d__140, LINQPad.GUI
      2(192)          0(0)        2(192)   LINQPad.ExecutionModel.InPipe+<Go>d__6
      1(136)          0(0)        1(136)   Microsoft.Web.WebView2.WinForms.WebView2+<InitCoreWebView2Async>d__13
       1(96)         1(96)          0(0)   LINQPad.UI.MainForm+<<OnActivated>g__Continue|115_0>d, LINQPad.GUI
       1(96)         1(96)          0(0)   LINQPad.LanguageServices.WSAgent+<CheckActivation>d__17, Resources

...

Large Async State Machines
==========================

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[Microsoft.Web.WebView2.WinForms.WebView2+<InitCoreWebView2Async>d__13, Microsoft.Web.WebView2.WinForms]] (136 bytes) (1 fields in state)
                  Field   Type
------------------------------
            environment   Microsoft.Web.WebView2.Core.CoreWebView2Environment

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core],[LINQPad.UI.WebView2Util+<<GetWeb2EnvironmentBSync>g__GetBSync|12_0>d, LINQPad.GUI]] (96 bytes) (6 fields in state)
                  Field   Type
------------------------------
    <browserFolder>5__1   System.String
<configUpdatedFile>5__3   System.String
       <dataFolder>5__2   System.String
              <env>5__4   Microsoft.Web.WebView2.Core.CoreWebView2Environment
               <ex>5__7   System.Exception
         <webView2>5__6   Microsoft.Web.WebView2.WinForms.WebView2

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.ExecutionModel.InPipe+<Go>d__6, LINQPad.Runtime]] (96 bytes) (5 fields in state)
                  Field   Type
------------------------------
               <ex>5__6   System.Exception
  <latestMessageID>5__3   System.Nullable`1[[System.Int32, System.Private.CoreLib]]
              <msg>5__4   System.Byte[]
       <spinCycles>5__1   System.Int32
               <sw>5__2   System.Diagnostics.Stopwatch

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.ExecutionModel.InPipe+<Go>d__6, LINQPad.Runtime]] (96 bytes) (5 fields in state)
                  Field   Type
------------------------------
               <ex>5__6   System.Exception
  <latestMessageID>5__3   System.Nullable`1[[System.Int32, System.Private.CoreLib]]
              <msg>5__4   System.Byte[]
       <spinCycles>5__1   System.Int32
               <sw>5__2   System.Diagnostics.Stopwatch

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.UI.QueryEditor+<CheckSuggestionsSmartTagAtCaret>d__140, LINQPad.GUI]] (96 bytes) (3 fields in state)
                  Field   Type
------------------------------
                <c>5__2   LINQPad.ExecutionModel.QueryCompiler
             <lang>5__1   LINQPad.UI.Lexers.RoslynSyntaxLanguage
           <offset>5__3   System.Int32

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.UI.QueryEditor+<CheckSuggestionsSmartTagAtCaret>d__140, LINQPad.GUI]] (96 bytes) (3 fields in state)
                  Field   Type
------------------------------
                <c>5__2   LINQPad.ExecutionModel.QueryCompiler
             <lang>5__1   LINQPad.UI.Lexers.RoslynSyntaxLanguage
           <offset>5__3   System.Int32

...

Unique Stack Frames
===================
Count   Call Site
-----------------
    2   [InlinedCallFrame: 000000890a5fe020] System.Windows.Forms.UnsafeNativeMethods.WaitMessage()
    2   [InlinedCallFrame: 000000890c37edf8] Interop+Kernel32.ConnectNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle, IntPtr)
    1   [DebuggerU2MCatchHandlerFrame: 000000890af7f880] 
    1   [DebuggerU2MCatchHandlerFrame: 000000890c37f5b0] 
    1   [GCFrame: 000000890a5fec68] 

...

Call Stacks
===========
Thread #0: 13 frames
--------------------
[InlinedCallFrame: 000000890a5fe020] System.Windows.Forms.UnsafeNativeMethods.WaitMessage()
[InlinedCallFrame: 000000890a5fe020] System.Windows.Forms.UnsafeNativeMethods.WaitMessage()
ILStubClass.IL_STUB_PInvoke()
System.Windows.Forms.Application+ComponentManager.System.Windows.Forms.UnsafeNativeMethods.IMsoComponentManager.FPushMessageLoop(IntPtr, Int32, Int32) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 2016]
System.Windows.Forms.Application+ThreadContext.RunMessageLoopInner(Int32, System.Windows.Forms.ApplicationContext) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 3370]
System.Windows.Forms.Application+ThreadContext.RunMessageLoop(Int32, System.Windows.Forms.ApplicationContext) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 3233]
System.Windows.Forms.Application.Run(System.Windows.Forms.Form) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 1360]
LINQPad.UIProgram.Run()
LINQPad.UIProgram.Go(System.String[])
LINQPad.UIProgram.Start(System.String[])
LINQPad.UI.Loader.Main(System.String[])
[GCFrame: 000000890a5fec68] 
[GCFrame: 000000890a5ff200] 

Thread #1: 1 frames
-------------------
[DebuggerU2MCatchHandlerFrame: 000000890af7f880] 

Thread #2: 10 frames
--------------------
[InlinedCallFrame: 000000890c37edf8] Interop+Kernel32.ConnectNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle, IntPtr)
[InlinedCallFrame: 000000890c37edf8] Interop+Kernel32.ConnectNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle, IntPtr)
ILStubClass.IL_STUB_PInvoke(Microsoft.Win32.SafeHandles.SafePipeHandle, IntPtr)
System.IO.Pipes.NamedPipeServerStream.WaitForConnection() [/_/src/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs @ 147]
LINQPad.UIProgram.Listen()
System.Threading.ThreadHelper.ThreadStart_Context(System.Object) [/_/src/System.Private.CoreLib/src/System/Threading/Thread.CoreCLR.cs @ 44]
System.Threading.ExecutionContext.RunInternal(System.Threading.ExecutionContext, System.Threading.ContextCallback, System.Object) [/_/src/System.Private.CoreLib/shared/System/Threading/ExecutionContext.cs @ 172]
System.Threading.ThreadHelper.ThreadStart() [/_/src/System.Private.CoreLib/src/System/Threading/Thread.CoreCLR.cs @ 93]
[GCFrame: 000000890c37f348] 
[DebuggerU2MCatchHandlerFrame: 000000890c37f5b0] 



Heap Fragmentation
==================

       Type   Size Bytes   Free Bytes   Fragmentation %
-------------------------------------------------------
LargeObject   36,672,792   31,741,536        86.6%
       Gen2   14,120,960    3,631,312        25.7%
       Gen1    6,438,432    1,281,136        19.9%
       Gen0    1,466,392       56,120         3.8%


Async Pins
==========
                           Type      Location   Count(bytes)
------------------------------------------------------------
System.Threading.OverlappedData   Generation2         4(288)

Explicit Pins
=============
           Type          Location      Count(bytes)
---------------------------------------------------
System.Object[]   LargeObjectHeap       15(440,232)
  System.Object       Generation2             1(24)

[2021-12-30 21:22:12Z]: Removing dump file
```
