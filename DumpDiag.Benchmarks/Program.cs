using BenchmarkDotNet.Running;
using DumpDiag.Benchmarks.Benchmarks;
using System;

namespace DumpDiag.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            //var x = new MultiProcessBenchmark();
            //x.Setup();

            //var incr = 0;

            //while (true)
            //{
            //    for (var i = Environment.ProcessorCount; i >= 1; i--)
            //    {
            //        x.NumProcesses = i;

            //        Console.WriteLine($"{incr:N0} ==> {x.NumProcesses}");

            //        x.StartDiagnoser();
            //        x.AnalyzeDump();
            //        x.StopDiagnoser();

            //        incr++;
            //    }
            //}

            //x.Cleanup();

            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
