using BenchmarkDotNet.Attributes;
using DumpDiag.Impl;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DumpDiag.Benchmarks.Benchmarks
{
    public class MultiProcessBenchmark
    {
        [Params(1, 2, 4, 6, 8, 10, 12, 14, 16)]
        public int NumProcesses { get; set; }

        internal string DotNetDumpPath { get; set; }
        internal string DumpFile { get; set; }

        internal DumpDiagnoser<DotNetDumpAnalyzerProcess> Diagnoser { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            var trashStrings = new List<string>();
            for (var i = 0; i < 10_000; i++)
            {
                trashStrings.Add(Guid.NewGuid().ToString());
            }

            var duped = Enumerable.Range(0, 10).SelectMany(x => trashStrings).ToArray();

            if (!DotNetToolFinder.TryFind("dotnet-dump", out var path, out var error))
            {
                throw new Exception(error);
            }

            using var proc = Process.GetCurrentProcess();

            DotNetDumpPath = path;

            DumpFile = Path.GetTempFileName();
            File.Delete(DumpFile);

            DumpProcess.TakeDumpAsync(DotNetDumpPath, proc.Id, DumpFile).GetAwaiter().GetResult();

            GC.KeepAlive(trashStrings);
            GC.KeepAlive(duped);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            Thread.Sleep(1_000);
            File.Delete(DumpFile);
        }

        [IterationSetup]
        public void StartDiagnoser()
        {
            Diagnoser = DumpDiagnoser.CreateDotNetDumpAsync(DotNetDumpPath, DumpFile, NumProcesses).AsTask().GetAwaiter().GetResult();
        }

        [IterationCleanup]
        public void StopDiagnoser()
        {
            Thread.Sleep(1_000);
            Diagnoser.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        [Benchmark]
        public void AnalyzeDump()
        {
            Diagnoser.AnalyzeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
