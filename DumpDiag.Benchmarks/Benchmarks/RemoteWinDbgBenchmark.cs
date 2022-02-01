using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace DumpDiag.Benchmarks.Benchmarks
{
    [SupportedOSPlatform("windows")]
    [Config(typeof(Config))]
    public class RemoteWinDbgBenchmark
    {
        internal class Config : ManualConfig
        {
            public Config()
            {
                AddJob(
                    BenchmarkDotNet.Jobs.Job.Default
                        .WithToolchain(new InProcessEmitToolchain(timeout: TimeSpan.FromMinutes(30), logOutput: true))
                );
            }
        }

        private WinDbgHelper helper;
        private RemoteWinDbg remote;

        [GlobalSetup]
        public void Setup()
        {
            var trashStrings = new List<string>();
            for (var i = 0; i < 10_000; i++)
            {
                trashStrings.Add(Guid.NewGuid().ToString());
            }

            var duped = Enumerable.Range(0, 10).SelectMany(x => trashStrings).ToArray();

            var dir = WinDbgHelper.WinDbgLocations.First();
            helper = WinDbgHelper.CreateWinDbgInstanceAsync(dir).GetAwaiter().GetResult();

            var libHandle = NativeLibrary.Load(helper.DbgEngDllPath);
            if (!DebugConnectWideThunk.TryCreate(libHandle, out var thunk, out var error))
            {
                throw new Exception(error);
            }

            remote = RemoteWinDbg.CreateAsync(ArrayPool<char>.Shared, thunk, "127.0.0.1", helper.LocalPort, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

            GC.KeepAlive(trashStrings);
            GC.KeepAlive(duped);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            remote.DisposeAsync().GetAwaiter().GetResult();
            helper.DisposeAsync().GetAwaiter().GetResult();
        }

        [Benchmark]
        public async Task Loop()
        {
            for (var i = 0; i < 10; i++)
            {
                var resp = remote.SendCommand(Command.CreateCommand("dd 123"));
                await foreach (var line in resp.ConfigureAwait(false))
                {
                    line.Dispose();
                }
            }
        }
    }
}
