using DumpDiag.Impl;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class DumpProcessTests
    {
        [Fact]
        public async Task TakeDumpAsync()
        {
            Assert.True(DotNetToolFinder.TryFind("dotnet-dump", out var path, out _));

            using var curProc = Process.GetCurrentProcess();
            var pid = curProc.Id;

            var saveTo = Path.GetTempFileName();
            File.Delete(saveTo);
            Assert.False(File.Exists(saveTo));
            try
            {
                var res = await DumpProcess.TakeDumpAsync(path, pid, saveTo).ConfigureAwait(false);
                Assert.True(res.Success, res.Log);

                Assert.True(File.Exists(saveTo));
            }
            finally
            {
                File.Delete(saveTo);
            }
        }

        [Fact]
        public async Task FailedDumpAsync()
        {
            Assert.True(DotNetToolFinder.TryFind("dotnet-dump", out var path, out _));

            var random = new Random();
            var pid = random.Next();
            while (true)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);

                    pid = random.Next();
                }
                catch
                {
                    break;
                }
            }

            var saveTo = Path.GetTempFileName();
            File.Delete(saveTo);
            Assert.False(File.Exists(saveTo));
            try
            {
                var res = await DumpProcess.TakeDumpAsync(path, pid, saveTo).ConfigureAwait(false);
                Assert.False(res.Success, res.Log);

                Assert.False(File.Exists(saveTo));
            }
            finally
            {
                if (File.Exists(saveTo))
                {
                    File.Delete(saveTo);
                }
            }
        }
    }
}
