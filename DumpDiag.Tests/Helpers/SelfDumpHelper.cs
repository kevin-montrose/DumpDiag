using DumpDiag.Impl;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests.Helpers
{
    internal sealed class SelfDumpHelper : IAsyncDisposable
    {
        internal string DotNetDumpPath { get; }
        internal string DumpFile { get; }

        private SelfDumpHelper(string dotNetDumpPath, string dumpFile)
        {
            DotNetDumpPath = dotNetDumpPath;
            DumpFile = dumpFile;
        }

        private static async ValueTask AttemptDeleteAsync(string file)
        {
            const int RETRY_LIMIT = 3;

            var attempt = 0;

            while (attempt < RETRY_LIMIT)
            {
                if (!File.Exists(file))
                {
                    break;
                }

                try
                {
                    File.Delete(file);
                }
                catch
                {
                    await Task.Delay(10);
                }

                attempt++;
            }
        }

        public ValueTask DisposeAsync()
        => AttemptDeleteAsync(DumpFile);
        
        internal static async ValueTask<SelfDumpHelper> TakeSelfDumpAsync()
        {
            using var curProc = Process.GetCurrentProcess();

            var pid = curProc.Id;
            Assert.True(DotNetToolFinder.TryFind("dotnet-dump", out var path, out _));

            var dumpFile = Path.GetTempFileName();
            File.Delete(dumpFile);
            Assert.False(File.Exists(dumpFile));

            try
            {
                var dumpRes = await DumpProcess.TakeDumpAsync(path, pid, dumpFile).ConfigureAwait(false);
                Assert.True(dumpRes.Success, dumpRes.Log);
            }
            catch
            {
                if(File.Exists(dumpFile))
                {
                    await AttemptDeleteAsync(dumpFile);
                }

                throw;
            }

            return new SelfDumpHelper(path, dumpFile);
        }
    }
}
