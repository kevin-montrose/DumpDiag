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

 - Counts of values on the heap, by type
 - Counts of strings on the heap, by content
 - Counts of char[]s on the heap, by content
 - Counts deleges on the heap, by backing method
 - Counts of async state machines, by "backing" method
 - All managed thread call stacks
 - Counts of unique stack frames from managed threads

Where relevant total, live, and dead counts are reported.  High % of dead references (especially over time) can indicate inefficient allocation patterns.

Constraints for `dotnet-dump` apply equally to DumpDiag, primarily that dumps must be analyzed on the same OS as they were captured on.

## Example Output

This analysis is of a LinqPad process.

```
[2021-10-10 22:41:38Z]: Writing report to standard output
[2021-10-10 22:41:38Z]: dotnet-dump location: C:\Users\kevin\.dotnet\tools\dotnet-dump.exe
[2021-10-10 22:41:38Z]: Taking dump of process id: 17404
[2021-10-10 22:41:42Z]: Analyzing dump file: C:\Users\kevin\AppData\Local\Temp\tmpEADC.tmp
[2021-10-10 22:41:43Z]: starting: 20%
[2021-10-10 22:41:43Z]: starting: 20%
[2021-10-10 22:41:43Z]: starting: 20%

...

[2021-10-10 22:42:14Z]: Analyzing complete

---

Types
=====
      Total(bytes)         Dead(bytes)          Live(bytes)   Value
-------------------------------------------------------------------
 31,561(2,619,563)                0(0)    31,561(2,619,563)   System.String
    4,974(318,336)      4,954(317,056)            20(1,280)   System.Action
    3,016(193,024)                0(0)       3,016(193,024)   System.EventHandler
       70(179,550)          10(25,650)          60(153,900)   System.Char[]
       816(52,224)                0(0)          816(52,224)   System.Func`1[[System.Collections.Generic.IDictionary`2[[System.String, System.Private.CoreLib],[System.Object, System.Private.CoreLib]], System.Private.CoreLib]]
       586(37,504)                0(0)          586(37,504)   System.Action`2[[System.Int32, System.Private.CoreLib],[System.Int32, System.Private.CoreLib]]

...

Delegates
=========
Total    Dead   Live   Value
----------------------------
4,954   4,954      0   LINQPad.AsyncDisposable.<TryDeferDisposal>b__9_0()
  816       0    816   System.Composition.TypedParts.Discovery.DiscoveredPart+<>c__DisplayClass11_0.<.ctor>b__0()
  689       0    689   System.Windows.Forms.ToolStripDropDownItem.ToolStrip_RescaleConstants(Int32, Int32)
  515       0    515   System.Composition.Hosting.Providers.Lazy.LazyWithMetadataExportDescriptorProvider+<>c__DisplayClass2_2`2[[System.__Canon, System.Private.CoreLib],[System.__Canon, System.Private.CoreLib]].<GetLazyDefinitions>b__4()
  396       0    396   System.Windows.Forms.ToolStripItem.ToolStrip_RescaleConstants(Int32, Int32)
  353       0    353   System.Windows.Forms.ToolStripDropDownItem.DropDown_ItemClicked(System.Object, System.Windows.Forms.ToolStripItemClickedEventArgs)
  353       0    353   System.Windows.Forms.ToolStripDropDownItem.DropDown_Closed(System.Object, System.Windows.Forms.ToolStripDropDownClosedEventArgs)
  353       0    353   System.Windows.Forms.ToolStripDropDownItem.DropDown_Opened(System.Object, System.EventArgs)

...

Strings
=======
Total   Dead   Live   Value
---------------------------
  698      0    698   C#
  545      0    545   Visual Basic
  430      0    430   Default
  364      0    364   System
  287      0    287   Microsoft
  278      0    278   Runtime
  256      0    256   CompilerServices
  206      0    206   .reloc
  206      0    206   .rsrc
  206      0    206   .text

...

Char[]
======
Total   Dead   Live   Value
---------------------------
   17      0     17   ESCAPED: "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    2      1      1   ESCAPED: "Activate premium features\0\0\0\0\0\0\0"
    2      1      1   ESCAPED: "\0Language\0n\0.\0\0\0"
    1      1      0   html,body,div,span,iframe,p,pre,a,abbr,acronym,code,del,em,img,ins,q,strong,var,i,fieldset,form,label,legend,table,caption,tbody,tfoot,thead,tr,th,td,article,aside,canvas,details,figure,figcaption,footer,header,hgroup,nav,output,section,summary,time,mark,audio,video{margin:0;padding:0;border:0;vertical-align:baseline;font:inherit;font-size:100%;}h1,h2,h3,h4,h5,h6{margin:.2em 0 .05em 0;padding:0;border:0;vertical-align:baseline;}i,em{font-style:italic}body{margin:0.5em;font-family:Segoe UI,Verdana,sans-serif
    1      1      0   ESCAPED: "html,body,div,span,iframe,p,pre,a,abbr,acronym,code,del,em,img,ins,q,strong,var,i,fieldset,form,label,legend,table,caption,tbody,tfoot,thead,tr,th,td,article,aside,canvas,details,figure,figcaption,footer,header,hgroup,nav,output,section,summary,time,mark,audio,video{margin:0;padding:0;border:0;vertical-align:baseline;font:inherit;font-size:100%;}h1,h2,h3,h4,h5,h6{margin:.2em 0 .05em 0;padding:0;border:0;vertical-align:baseline;}i,em{font-style:italic}body{margin:0.5em;font-family:Segoe UI,Verdana,sans-serif;font-size:82%;background:white}pre,code,.fixedfont{font-family:Consolas,monospace;font-size:10pt;}a,a:visited{text-decoration:none;font-family:Segoe UI Semibold,sans-serif;font-weight:bold;cursor:pointer;}a:hover,a:visited:hover{text-decoration:underline;}table{border-collapse:collapse;border-spacing:0;border:2px solid #4C74B2;margin:0.3em 0.1em 0.2em 0.1em;}table.limit{border-bottom-color:#B56172;}table.expandable{border-bottom-style:dashed;}table.error{border-bottom-width:4px;}td,th{vertical-align:top;\0\0"
    1      1      0   ESCAPED: "Can't remember what you changed in the last edit session?\r\n\r\nPress Ctrl+Z. LINQPad restores your Undo/Redo buffer with unsaved queries.\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"
    1      1      0   ESCAPED: "(click anywhere or press any key to close)\0by Joseph Albahari\0\0\0"
    1      1      0   html,body,div,span,iframe,p,pre,a,abbr,acronym,code,del,em,img,ins,q,strong,var,i,fieldset,form,label,legend,table,caption,tbody,tfoot,thead,tr,th,td,article,aside,canvas,details,figure,figcaption,footer,header,hgroup,nav,output,section,summary,time,mark,audio,video{margin:0;padding:0;border:0;vertical-align:baseline;font:inherit;font-size:100%;}h1,h2,h3,h4,h5,h6{margin:.2em 0 .05em 0;padding:0;border:0;vertical-align:baseline;}i,em{font-style:italic}body{margin:0.5em;font-family:Segoe UI,Verdana,sans-serif;font-size:82%;background:white}pre,code,.fixedfont{font-family:Consolas,monospace;font-size:10pt;}a,a:visited{text-decoration:none;font-family:Segoe UI Semibold,sans-serif;font-weight:bold;cursor:pointer;}a:hover,a:visited:hover{text-decoration:underline;}table{border-collapse:collapse;border-spacing:0;border:2px solid #4C74B2;margin:0.3em 0.1em 0.2em 0.1em;}table.limit{border-bottom-color:#B56172;}table.expandable{border-bottom-style:dashed;}table.error{border-bottom-width:4px;}td,th{vertical-align:top;border:1px solid #bbb;margin:0;position:-webkit-sticky;position:sticky;top:0;z-index:2;}th{padding:0.05em 0.3em 0.15em 0.3em;text-align:left;background-color:#ddd;border:1px solid #777;font-size:.95em;font-family:Segoe UI Semibold,sans-serif;font-weight:bold;}th.private{font-family:Segoe UI;font-weight:normal;font-style:italic;}td.private{background:#f4f4ee}td.private table{background:white}td,th.member{line-height:1.25;padding:0.1em 0.3em 0.2em 0.3em;position:initial;}tr.repeat>th{font-size:90%;font-family:Segoe UI Semibold,sans-serif;border:none;background-color:#eee;color:#999;padding:0.0em 0.2em 0.15em 0.3em}td.typeheader{font-size:.95em;background-color:#4C74B2;color:white;padding:0 0.3em 0.25em 0.2em;}td.n{text-align:right}a.typeheader,a:link.typeheader,a:visited.typeheader,a:link.extenser,a:visited.extenser{font-family:Segoe UI Semibold,sans-serif;font-size:.95em;font-weight:bold;text-decoration:none;color:white;margin-bottom:-0.1em;float:left;}a.difheader,a:link.difheader,a:visited.difheader{color:#ff8}
    1      1      0   ESCAPED: "html,body,div,span,iframe,p,pre,a,abbr,acronym,code,del,em,img,ins,q,strong,var,i,fieldset,form,label,legend,table,caption,tbody,tfoot,thead,tr,th,td,article,aside,canvas,details,figure,figcaption,footer,header,hgroup,nav,output,section,summary,time,mark,audio,video{margin:0;padding:0;border:0;vertical-align:baseline;font:inherit;font-size:100%}h1,h2,h3,h4,h5,h6{margin:.2em 0 .05em 0;padding:0;border:0;vertical-align:baseline}i,em{font-style:italic}body{margin:0.5em;font-family:Segoe UI,Verdana,sans-serif;font-size:82%;background:white}pre,code,.fixedfont{font-family:Consolas,monospace;font-size:10pt}a,a:visited{text-decoration:none;font-family:Segoe UI Semibold,sans-serif;font-weight:bold;cursor:pointer}a:hover,a:visited:hover{text-decoration:underline}table{border-collapse:collapse;border-spacing:0;border:2px solid #4C74B2;margin:0.3em 0.1em 0.2em 0.1em}table.limit{border-bottom-color:#B56172}table.expandable{border-bottom-style:dashed}table.error{border-bottom-width:4px}td,th{vertical-align:top;border:1px solid #bbb;margin:0;position:-webkit-sticky;position:sticky;top:0;z-index:2}th{padding:0.05em 0.3em 0.15em 0.3em;text-align:left;background-color:#ddd;border:1px solid #777;font-size:.95em;font-family:Segoe UI Semibold,sans-serif;font-weight:bold}th.private{font-family:Segoe UI;font-weight:normal;font-style:italic}td.private{background:#f4f4ee}td.private table{background:white}td,th.member{line-height:1.25;padding:0.1em 0.3em 0.2em 0.3em;position:initial}tr.repeat>th{font-size:90%;font-family:Segoe UI Semibold,sans-serif;border:none;background-color:#eee;color:#999;padding:0.0em 0.2em 0.15em 0.3em}td.typeheader{font-size:.95em;background-color:#4C74B2;color:white;padding:0 0.3em 0.25em 0.2em}td.n{text-align:right}a.typeheader,a:link.typeheader,a:visited.typeheader,a:link.extenser,a:visited.extenser{font-family:Segoe UI Semibold,sans-serif;font-size:.95em;font-weight:bold;text-decoration:none;color:white;margin-bottom:-0.1em;float:left}a.difheader,a:link.difheader,a:visited.difheader{color:#ff8}a.extenser,a:link.extenser,a:visited.extenser{margin:0 0 0 0.3em;padding-left:0.5em;padding-right:0.3em}a:hover.extenser{text-decoration:none}span.extenser{font-size:1.1em;line-height:0.8}span.cyclic{padding:0 0.2em 0 0;margin:0;font-family:Arial,sans-serif;font-weight:bold;margin:2px;font-size:1.5em;line-height:0;vertical-align:middle}.arrow-up,.arrow-down{display:inline-block;margin:0 0.3em 0.15em 0.1em;width:0;height:0;cursor:pointer}.arrow-up{border-left:0.35em solid transparent;border-right:0.35em solid transparent;border-bottom:0.35em solid white}.arrow-down{border-left:0.35em solid transparent;border-right:0.35em solid transparent;border-top:0.35em solid white}table.group{border:none;margin:0}td.group{border:none;padding:0 0.1em}div.spacer{margin:0.6em 0}div.headingpresenter{border:none;border-left:0.2em dotted #1a5;margin:.8em 0otted #1a5;margin:.8em 0em 1em 0.1em;padding-left:.5em;}h1.headingpresenter{border:none;padding:0 0 0.35em 0;margin:0;font-family:Segoe UI Semibold,Arial;font-weight:bold;background-color:white;color:#209020;font-size:1.1em;line-height:0.8;}td.summary{background-color:#DAEAFA;color:black;font-size:.95em;padding:0.05em 0.3em 0.2em 0.3em;}tr.columntotal>td{background-color:#eee;font-family:Segoe UI Semibold;font-weight:bold;font-size:.95em;color:#4C74B2;text-align:right;}span.graphbar{background:#DAEAFA;color:#DAEAFA;padding-bottom:1px;margin-left:-0.2em;margin-right:0.2em;}a.graphcolumn,a:link.graphcolumn,a:visited.graphcolumn{color:#4C74B2;text-decoration:none;font-weight:bold;font-family:Arial;font-size:1em;line-height:1;letter-spacing:-0.2em;margin-left:0.15em;margin-right:0.2em;cursor:pointer;}a.collection,a:link.collection,a:visited.collection{color:#209020}a.reference,a:link.reference,a:visited.reference{color:#0080D1}span.meta,span.null{color:#209020}span.warning{color:red}span.false{color:#888}span.true{font-weight:bold}.highlight{background:#ff8}code.xml b{color:blue;font-weight:normal}code.xml i{color:brown;font-weight:normal;font-style:normal}code.xml em{color:red;font-weight:normal;\ubc9e\u02a8\0"
    1      1      0   ESCAPED: "html,body,div,span,iframe,p,pre,a,abbr,acronym,code,del,em,img,ins,q,strong,var,i,fieldset,form,label,legend,table,caption,tbody,tfoot,thead,tr,th,td,article,aside,canvas,details,figure,figcaption,footer,header,hgroup,nav,output,section,summary,time,mark,audio,video{margin:0;padding:0;border:0;vertical-align:baseline;font:inherit;font-size:100%}h1,h2,h3,h4,h5,h6{margin:.2em 0 .05em 0;padding:0;border:0;vertical-align:baseline}i,em{font-style:italic}body{margin:0.5em;font-family:Segoe UI,Verdana,sans-serif;font-size:82%;background:white}pre,code,.fixedfont{font-family:Consolas,monospace;font-size:10pt}a,a:visited{text-decoration:none;font-family:Segoe UI Semibold,sans-serif;font-weight:bold;cursor:pointer}a:hover,a:visited:hover{text-decoration:underline}table{border-collapse:collapse;border-spacing:0;border:2px solid #4C74B2;margin:0.3em 0.1em 0.2em 0.1em}table.limit{border-bottom-color:#B56172}table.expandable{border-bottom-style:dashed}table.error{border-bottom-width:4px}td,th{vertical-align:top;border:1px solid #bbb;margin:0;position:-webkit-sticky;position:sticky;top:0;z-index:2}th{padding:0.05em 0.3em 0.15em 0.3em;text-align:left;background-color:#ddd;border:1px solid #777;font-size:.95em;font-family:Segoe UI Semibold,sans-serif;font-weight:bold}th.private{font-family:Segoe UI;font-weight:normal;font-style:italic}td.private{background:#f4f4ee}td.private table{background:white}td,th.member{line-height:1.25;padding:0.1em 0.3em 0.2em 0.3em;position:initial}tr.repeat>th{font-size:90%;font-family:Segoe UI Semibold,sans-serif;border:none;background-color:#eee;color:#999;padding:0.0em 0.2em 0.15em 0.3em}td.typeheader{font-size:.95em;background-color:#4C74B2;color:white;padding:0 0.3em 0.25em 0.2em}td.n{text-align:right}a.typeheader,a:link.typeheader,a:visited.typeheader,a:link.extenser,a:visited.extenser{font-family:Segoe UI Semibold,sans-serif;font-size:.95em;font-weight:bold;text-decoration:none;color:white;margin-bottom:-0.1em;float:left}a.difheader,a:link.difheader,a:visited.difheader{color:#ff8}a.extenser,a:link.extenser,a:visited.extenser{margin:0 0 0 0.3em;padding-left:0.5em;padding-right:0.3em}a:hover.extenser{text-decoration:none}span.extenser{font-size:1.1em;line-height:0.8}span.cyclic{padding:0 0.2em 0 0;margin:0;font-family:Arial,sans-serif;font-weight:bold;margin:2px;font-size:1.5em;line-height:0;vertical-align:middle}.arrow-up,.arrow-down{display:inline-block;margin:0 0.3em 0.15em 0.1em;width:0;height:0;cursor:pointer}.arrow-up{border-left:0.35em solid transparent;border-right:0.35em solid transparent;border-bottom:0.35em solid white}.arrow-down{border-left:0.35em solid transparent;border-right:0.35em solid transparent;border-top:0.35em solid white}table.group{border:none;margin:0}td.group{border:none;padding:0 0.1em}div.spacer{margin:0.6em 0}div.headingpresenter{border:none;border-left:0.2em dotted #1a5;margin:.8em 0 1em 0.1em;padding-left:.5em}h1.headingpresenter{border:none;padding:0 0 0.35em 0;margin:0;font-family:Segoe UI Semibold,Arial;font-weight:bold;background-color:white;color:#209020;font-size:1.1em;line-height:0.8}td.summary{background-color:#DAEAFA;color:black;font-size:.95em;padding:0.05em 0.3em 0.2em 0.3em}tr.columntotal>td{background-color:#eee;font-family:Segoe UI Semibold;font-weight:bold;font-size:.95em;color:#4C74B2;text-align:right}span.graphbar{background:#DAEAFA;color:#DAEAFA;padding-bottom:1px;margin-left:-0.2em;margin-right:0.2em}a.graphcolumn,a:link.graphcolumn,a:visited.graphcolumn{color:#4C74B2;text-decoration:none;font-weight:bold;font-family:Arial;font-size:1em;line-height:1;letter-spacing:-0.2em;margin-left:0.15em;margin-right:0.2em;cursor:pointer}a.collection,a:link.collection,a:visited.collection{color:#209020}a.reference,a:link.reference,a:visited.reference{color:#0080D1}span.meta,span.null{color:#209020}span.warning{color:red}span.false{color:#888}span.true{font-weight:bold}.highlight{background:#ff8}code.xml b{color:blue;font-weight:normal}code.xml i{color:brown;font-weight:normal;font-style:normal}code.xml em{color:red;font-weight:normal;font-style:normal}span.cc{background:#666;color:white;margin:0 1.5px;padding:0 1px;font-family:Consolas,monospace;border-radius:3px}ol,ul{margin:0.7em 0.3em;padding-left:2.5em}li{margin:0.3em 0}.difadd{background:#d3f3d3}.difremove{background:#f3d8d8}.rendering{font-style:italic;color:brown}p.scriptLog{color:#a77;background:#f8f6f6;font-family:Consolas,monospace;font-size:9pt;padding:.1em .3em}::-ms-clear{display:none}input,textarea,button,select{font-family:Segoe UI;font-size:1em;padding:.2em}button{padding:.2em .4em}input,textarea,select{margin:.15em 0}input[type=\"checkbox\"],input[type=\"radio\"]{margin:0 0.4em 0 0;height:0.9em;width:0.9em}input[type=\"radio\"]:focus,input[type=\"checkbox\"]:focus{outline:thin dotted red}.checkbox-label{vertical-align:middle;position:relative;bottom:.07em;margin-right:.5em}fieldset{margin:0 .2em .4em .1em;border:1pt solid #aaa;padding:.1em .6em .4em .6em}legend{padding:.2em .1em}g:.1em .6em .4em .6em;}legend{padding:.2em .1em}adio\"] {margin:0 0.4em 0 0;height:0.9em;width:0.9em;}input[type=\"radio\"]:focus,input[type=\"checkbox\"]:focus {outline: thin dotted red;}.checkbox-label {vertical-align:middle;position:relative;bottom:.07em;margin-right:.5em;}fieldset {margin: 0 .2em .4em .1em;border: 1pt solid #aaa;padding: .1em .6em .4em .6em;}legend { padding:.2em .1em } { padding:.2em .1em }\ubc9b\u02a8\0\0\0\0\0\u8160\ub061\u7ff9\0\u6dc0\ube0b\u02a8\0\0\0\0\0x\0\0\0\uc9c2\uffc0\uffff\uffff\0\0\u02a8\0\0\0\0\0\u4110\uafaf\u7ff9\0\ub2c0\ubdc5\u02a8\0\0\0\0\0\u6b18\ub026\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ubd40\ub05b\u7ff9\0\0\0\0\0\u0890\ubca0\u02a8\0\u46c0\ubdcc\u02a8\0\0\0\0\0\ud020\ub061\u7ff9\0\ub2c0\ubdc5\u02a8\0\0\0\0\0\u6b10\ub026\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ubd40\ub05b\u7ff9\0\u4700\ubdcc\u02a8\0\u08a8\ubca0\u02a8\0\u4728\ubdcc\u02a8\0\0\0\0\0\ud568\ub061\u7ff9\0\ub2c0\ubdc5\u02a8\0\0\0\0\0\u6b20\ub026\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ubd40\ub05b\u7ff9\0\u4768\ubdcc\u02a8\0\uae28\ubc9e\u02a8\0\u4790\ubdcc\u02a8\0\0\0\0\0\u6a58\ub04e\u7ff9\0\u9440\uda83\u02a8\0\0\0\0\0\u7a80\ub061\u7ff9\0\u6160\ube09\u02a8\0\0\0\0\0\u2a38\uafce\u7ff9\0\u1ac0\ube0b\u02a8\0H\0\0\0\uc97a\uffc0\uffff\uffff\ufaa8\u0100\0\0\0\0\0\0\u9e20\ub061\u7ff9\0\ub540\ubdc5\u02a8\0\0\0\0\0\ua410\ub049\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u6618\uaf9c\u7ff9\0\u0002\0\0\0\u6cf8\ube0b\u02a8\0\u7e98\ube0b\u02a8\0\0\0\0\0\u9e20\ub061\u7ff9\0\u7f00\ube0b\u02a8\0\0\0\0\0\uc1a0\ub049\u7ff9\0\u9db0\ub061\u7ff9\0\u7ed8\ube0b\u02a8\0\u0002\0\0\0\u0080\0\0\0\uc8fa\uffc0\uffff\uffff\0\0\0\0\0\0\0\0\uae68\ub035\u7ff9\0\0\0\0\0\ub540\ubdc5\u02a8\0\0\0\0\0 \0\0\0\uc8da\uffc0\uffff\uffff\uffc0\0\u02a8\0\0\0\0\0\u6a58\ub04e\u7ff9\0\u9438\uda83\u02a8\0\0\0\u78bb\u0e51\u0f68\ub061\u7ff9\0\0\0\0\0\u4d60\ubdcc\u02a8\0\0\0\0\0\u4c88\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u4d80\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u5d48\ubdcc\u02a8\0\0\0\0\0\u4c68\ubdcc\u02a8\0\u4c50\ubdcc\u02a8\0\0\0\0\0d\0\u0019\0d\0\u0019\0\u0224\b\u0858\0\u3913\u0006\uffff\uffff\u0090\0\0\0\0\u1f00\0\0\0\0\0\0\u4e08\ubdcc\u02a8\0\u4dd0\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u5338\ubdcc\u02a8\0\0\0\0\0\u53d0\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u1210\ubca0\u02a8\0\u5d68\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u4c10\ubdcc\u02a8\0\0\0\0\0\ud418\ubc97\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0003\0\0\0\u00c8\0\0\0\a\0\u0001\0\0\0\0\0\0\0\0\0\0\0\0\0\u0018\0\u0018\0\0\0\0\0\0\0\u0002\0\0\0\0\0\u0001\0\u0003\0\u0003\0\u0003\0\u0003\0\0\0\uffff\u7fff\uffff\u7fff\ub540\ubdc5\u02a8\0\u0001\0\a\0\0\0\u0101\u0101\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0003\0\u0002\0\u0002\0\u0003\0\0\0\0\0\0\0\0\0\0\0\0\0\uffff\uffff\0\0\uffff\uffff%\0F\0\u000f\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0001\0\0\0\u0018\0\u0018\0\u0001\0\u0003\0\u0003\0\u0003\0\u0003\0\0\0\0\0\u0002\0\f\0\u000e\0\u0002\0\0\0\0\0\u0003\0\b\0\u0003\0\u0003\0\0\0\0\0\0\0\0\0\f\0\0\0\0\0\0\0\0\0\u22b8\uafce\u7ff9\0\u4c30\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u6618\uaf9c\u7ff9\0\u0001\0\0\0\0\0\0\0\0\0\0\0\u5ec8\ub035\u7ff9\0\u9430\uda83\u02a8\0\0\0\0\0\u49d0\uaffb\u7ff9\0\u5d80\ubdcc\u02a8\0\u52a8\ubdcc\u02a8\0\0\0\0\0\u1918\ub035\u7ff9\0\0\0\0\0\0\0\0\0\u4cf0\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u9011\0\0\0\u4888\ubdcc\u02a8\0\u4c88\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u1640\uafaf\u7ff9\0\u9428\uda83\u02a8\0\0\0\0\0\u1e78\ub036\u7ff9\0\u0001\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u81a0\ub035\u7ff9\0\u0001\0\0\0\0\0\0\0\u8430\ube0b\u02a8\0@\0\0\0\uc8b0\uffc0\uffff\uffff\0\0\0\0\0\0\0\0\u7fa0\ub035\u7ff9\0\0\u8000\0\u8000\0\0\0\0\u81a0\ub035\u7ff9\0\u0002\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u8498\ube0b\u02a8\0$\b\0\0\0\0\0\0\u8430\ube0b\u02a8\0h\0\0\0\uc84a\uffc0\uffff\uffff\uff80\u0188\0\0\0\0\0\0\uae68\ub035\u7ff9\0\u55b0\ubdcc\u02a8\0\u4888\ubdcc\u02a8\0\0\0\0\0\u8850\ub035\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u5601\0\u0001\b\u0002\0\0\0\0d\0\u0019\0\0\0\0\0\0\0\u9958\ub035\u7ff9\0\u4888\ubdcc\u02a8\0\0\0d\0\u0001\0\n\0\0\0\0\0\u0001\0\0\0\0\0\0\0\u9a80\ub035\u7ff9\0\u4888\ubdcc\u02a8\0\0\0d\0\u0001\0\n\0\0\0\0\0\u0001\0\0\0\0\0\0\0\ue5f8\ub03b\u7ff9\0\u4e60\ubdcc\u02a8\0\u78bb\u0251\0\0\0\0\0\0\u1640\uafaf\u7ff9\0\u9420\uda83\u02a8\0\0\0\0\0\u1c00\ub061\u7ff9\0\u7fb0\ube0b\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u2048\ub036\u7ff9\0\u0001\0\0\0p\0\0\0\uc8b8\uffc0\uffff\uffff\0\0\0\0\0\0\0\0\u6948\ub04e\u7ff9\0\0\0\0\0\0\0\0\0\u5018\ubdcc\u02a8\0\u50c0\ubdcc\u02a8\0\u5238\ubdcc\u02a8\0\0\0\0\0\u1360\ubc97\u02a8\0\0\0\0\0\u50e8\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u5190\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u0001\u0001\0\u0101\0\0\0\0\0\0\0\0\0\0\0\0\u0013\u0001\u02a8\0\0\0\0\0\0\0\0\0\u0014\u0001\u02a8\0\0\0\0\0\u88b0\uafc6\u7ff9\0\u5060\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0002\0\u51ec\u3f38\0\0\0\0\0\0\0\0\u8b78\uafc6\u7ff9\0\u0003\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\uf090\uafa9\u7ff9\0\u0004\0\0\0\u01f4\0d\0\u1388\0\u01f4\0\0\0\0\0\u88b0\uafc6\u7ff9\0\u5130\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0002\0\u51ec\u3f38\0\0\0\0\0\0\0\0\u8b78\uafc6\u7ff9\0\u0003\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u88b0\uafc6\u7ff9\0\u51d8\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0002\0\u51ec\u3f38\0\0\0\0\0\0\0\0\u8b78\uafc6\u7ff9\0\u0003\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u7fd8\ub061\u7ff9\0\0\0\0\0\0\0\0\0\u5290\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u9011\0\0\0\u4f58\ubdcc\u02a8\0\0\0\0\0\u1640\uafaf\u7ff9\0\u9418\uda83\u02a8\0\0\0\0\0\u81a0\ub035\u7ff9\0\u0003\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u4d48\ubdcc\u02a8\0$\b\0\0\0\0\0\0\u4d08\ubdcc\u02a8\0\0\0\0\0\0\0\0\0(\u0002\0\0\u4f58\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0L\u0001\0\0\0\0\0\0\u80b0\ub043\u7ff9\0\u5360\ubdcc\u02a8\0\u4888\ubdcc\u02a8\0\uffff\uffff\u0001\0\0\0\0\0\u22b8\uafce\u7ff9\0\u5380\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u6618\uaf9c\u7ff9\0\u0004\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u2a38\uafce\u7ff9\0\u8aa8\ube0b\u02a8\00\0\0\0\uc8a0\uffc0\uffff\uffff\uf9e8\u02f0\0\0\0\0\0\0\u52d0\ub061\u7ff9\0\0\0\0\0\u5d28\ubdcc\u02a8\0\u5510\ubdcc\u02a8\0\u4888\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ucaf0\ubc9e\u02a8\0\0\0\0\0\0\0\0\0\u0002\0 \0 \0\u0004\0\u0001\0\u0003\0\u0090\0\0\0\0\0\0\0\u0017\0\u0017\0\u8804\v\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u0002\0\0\0\0\0\u0003\0\0\0\0\0\u0003\0\0\0\0\0\0\0\0\0\0\0\"\0\0\0\0\0\0\0\0\0\b\0\u0004\0\b\0\0\0\0\0\0\0\uffff\u7fff\uffff\u7fff\u0001\0\u0003\0\u0003\0\u0003\0\u0003\0\0\0\0\0\0\0\u49d0\uaffb\u7ff9\0\u5cf8\ubdcc\u02a8\0\u5c90\ubdcc\u02a8\0\0\0\0\0\u1e78\ub036\u7ff9\0\u0001\0\u0003\0\u0003\0\u0003\0\u0003\0\0\0\0\0\0\0\u81a0\ub035\u7ff9\0\u0001\0\0\0\0\0\0\0\u8c90\ube0b\u02a8\0@\0\0\0\ucf80\uffc0\uffff\uffff\0\0\0\0\0\0\0\0\u7fa0\ub035\u7ff9\0\0\u8000\0\u8000\0\0\0\0\u81a0\ub035\u7ff9\0\u0002\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u5c78\ubdcc\u02a8\0$\b\0\0\0\0\0\0\u5530\ubdcc\u02a8\0\0\0\0\0\0\0\0\0(\u0002\0\0\0\0\0\0\u2048\ub036\u7ff9\0\u0001\0\0\0$\u0002\0\0\0\0\0\0\0\0\0\0\0\0\0\0\uae68\ub035\u7ff9\0\0\0\0\0\u53d0\ubdcc\u02a8\0\0\0\0\0\ua618\ub035\u7ff9\0\u53d0\ubdcc\u02a8\0\udbd0\ubc9b\u02a8\0\0\0\0\0\ua618\ub035\u7ff9\0\u7fb0\ube0b\u02a8\0\ud9f8\ubc9b\u02a8\08\0\0\0\ucf4a\uffc0\uffff\uffff\ufed8\u00d0\0\0\0\0\0\0\u8160\ub061\u7ff9\0\u4888\ubdcc\u02a8\0\0\0\0\0\u2048\ub036\u7ff9\0\u0002\0\0\0$\u0002\0\0\u0800\0\0\0\0\0h\u0006\0\0\u0001\0\u0001\0\0\0\0\0\0\0\ua618\ub035\u7ff9\0\u7fb0\ube0b\u02a8\0\ud9c8\ubc9b\u02a8\0\0\0\0\0\ua618\ub035\u7ff9\0\u7fb0\ube0b\u02a8\0\ue068\ubc9b\u02a8\0\0\0\0\0\u8160\ub061\u7ff9\0\u7fb0\ube0b\u02a8\0\0\0\0\0x\0\0\0\uced2\uffc0\uffff\uffff\0\0\u02a8\0\0\0\0\0\u4110\uafaf\u7ff9\0\ub540\ubdc5\u02a8\0\0\0\0\0\u6b18\ub026\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ubd40\ub05b\u7ff9\0\0\0\0\0\u0890\ubca0\u02a8\0\u5dc0\ubdcc\u02a8\0\0\0\0\0\ud020\ub061\u7ff9\0\ub540\ubdc5\u02a8\0\0\0\0\0\u6b10\ub026\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ubd40\ub05b\u7ff9\0\u5e00\ubdcc\u02a8\0\u08a8\ubca0\u02a8\0\u5e28\ubdcc\u02a8\0\u0018\0\0\0\uc598\uffc0\uffff\uffff\ufb58\0\0\0\0\0\0\0\ud568\ub061\u7ff9\0\ub540\ubdc5\u02a8\0\0\0\0\0\u6b20\ub026\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\ubd40\ub05b\u7ff9\0\u5e68\ubdcc\u02a8\0\uae28\ubc9e\u02a8\0\u5570\ubdcc\u02a8\0\0\0\0\0\u6a58\ub04e\u7ff9\0\u9410\uda83\u02a8\0\0\0\0\0\u7a80\ub061\u7ff9\0\u6448\ube09\u02a8\0\0\0\0\0\u2a38\uafce\u7ff9\0\u1ac0\ube0b\u02a8\0H\0\0\0\uce80\uffc0\uffff\uffff\0\0\0\0\0\0\0\0\u9e20\ub061\u7ff9\0\ub7a8\ubdc5\u02a8\0\0\0\0\0\ua410\ub049\u7ff9\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\u6618\uaf9c\u7ff9\0\u0004\0\0\0\u6cf8\ube0b\u02a8\0\u7e98\ube0b\u02a8\0\u90a0\ube0b\u02a8\0\ua2a0\ube0b\u02a8\0\0\0\0\0\u9e20\ub061\u7ff9\0\u9118\ube0b\u02a8\0\0\0\0\0\uc1a0\ub049\u7ff9\0\u9db0\ub061\u7ff9\0\u90e0\ube0b\u02a8\0\u0003\0\0\0\u0090\0\0\0\uc498\uffc0\uffff\uffff\uff30@\0\0\0\0\0\0\uae68\ub035\u7ff9\0\0\0\0\0\ub7a8\ubdc5\u02a8\0\0\0\0\0 \0\0\0\ue5a8\uffc0\uffff\uffff\0\0\u02a8\0\0\0\0\0\u6a58\ub04e\u7ff9\0\u9408\uda83\u02a8\0\0\0\u3369\u0fc7\u0f68\ub061\u7ff9\0\0\0\0\0\u7f58\ubdcc\u02a8\0\0\0\0\0\u7b70\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u7f78\ubdcc\u02a8\0\0\0\0\0\0\0\0\0\u8740\ubdcc\u02a8\0\0\0\0\0\u7b50\ubdcc\u02a8\0\u7b38\ubdcc\u02a8\0\0\0\0\0d\0\u0019\0d\0\u0019\0\u0224\b\u0858\0\u3913\u0006\uffff\uffff\u0090\0\0\0\0\u1f00\0\0\0\0\0\0\u8000\ubdcc\u02a8\0\u7fc8\ubdcc\u02a8\0\0\0\0\0\0\0\0\0"

...

Async State Machines
====================
Total(bytes)   Dead(bytes)   Live(bytes)   Value
------------------------------------------------
      3(288)          0(0)        3(288)   LINQPad.ExecutionModel.InPipe+<Go>d__6
      2(272)          0(0)        2(272)   Microsoft.Web.WebView2.WinForms.WebView2+<InitCoreWebView2Async>d__13
       1(96)          0(0)         1(96)   Microsoft.CodeAnalysis.SyntaxTree, Microsoft.CodeAnalysis
       1(96)          0(0)         1(96)   LINQPad.UI.WebView2Util+<<GetWeb2EnvironmentBSync>g__GetBSync|12_0>d, LINQPad.GUI
       1(96)          0(0)         1(96)   LINQPad.ExecutionModel.QueryEmitResults, LINQPad.Runtime
       1(96)          0(0)         1(96)   LINQPad.ExecutionModel.QueryParseResults, LINQPad.Runtime

...

Large Async State Machines
==========================

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[Microsoft.Web.WebView2.WinForms.WebView2+<InitCoreWebView2Async>d__13, Microsoft.Web.WebView2.WinForms]] (136 bytes) (6 fields in state)
                Field   Type
----------------------------
           <>1__state   System.Int32
            <>4__this   Microsoft.Web.WebView2.WinForms.WebView2
         <>t__builder   System.Runtime.CompilerServices.AsyncTaskMethodBuilder
               <>u__1   System.Runtime.CompilerServices.TaskAwaiter`1[[Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core]]
               <>u__2   System.Runtime.CompilerServices.TaskAwaiter`1[[Microsoft.Web.WebView2.Core.CoreWebView2Controller, Microsoft.Web.WebView2.Core]]
          environment   Microsoft.Web.WebView2.Core.CoreWebView2Environment

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.ExecutionModel.InPipe+<Go>d__6, LINQPad.Runtime]] (96 bytes) (10 fields in state)
                Field   Type
----------------------------
           <>1__state   System.Int32
            <>4__this   LINQPad.ExecutionModel.InPipe
               <>s__5   System.Boolean
         <>t__builder   System.Runtime.CompilerServices.AsyncVoidMethodBuilder
               <>u__1   System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter[[System.Boolean, System.Private.CoreLib]]
             <ex>5__6   System.Exception
<latestMessageID>5__3   System.Nullable`1[[System.Int32, System.Private.CoreLib]]
            <msg>5__4   System.Byte[]
     <spinCycles>5__1   System.Int32
             <sw>5__2   System.Diagnostics.Stopwatch

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.UI.MainForm+<<OnActivated>g__Continue|113_0>d, LINQPad.GUI]] (96 bytes) (4 fields in state)
                Field   Type
----------------------------
           <>1__state   System.Int32
            <>4__this   LINQPad.UI.MainForm
         <>t__builder   System.Runtime.CompilerServices.AsyncVoidMethodBuilder
               <>u__1   System.Runtime.CompilerServices.TaskAwaiter

System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[[System.Threading.Tasks.VoidTaskResult, System.Private.CoreLib],[LINQPad.LanguageServices.WSAgent+<CheckActivation>d__18, Resources]] (96 bytes) (0 fields in state)
----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
No valid examples to inspect found on heap, reporting only size

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
System.Windows.Forms.Application+ThreadContext.RunMessageLoop(Int32, System.Windows.Forms.ApplicationContext) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 3233]
System.Windows.Forms.Application.Run(System.Windows.Forms.Form) [/_/src/System.Windows.Forms/src/System/Windows/Forms/Application.cs @ 1360]
LINQPad.UIProgram.Run()
LINQPad.UIProgram.Go(System.String[])
LINQPad.UIProgram.Start(System.String[])
LINQPad.UI.Loader.Main(System.String[])
[GCFrame: 000000b1e117e648] 
[GCFrame: 000000b1e117ebe0] 

...

[2021-10-10 22:42:15Z]: Removing dump file

```
