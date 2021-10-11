using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class DumpDiagnoserTests
    {
        private static readonly int PROCESS_COUNT = Environment.ProcessorCount;   // these are pretty expensive, so just do one size (but make it easy to change for debugging)

        [Fact]
        public async Task LoadStringCountsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableDictionary<string, ReferenceStats> res;
            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = await diag.LoadStringCountsAsync().ConfigureAwait(false);
            }

            Assert.NotEmpty(res);
        }

        [Fact]
        public async Task LoadDelegateCountsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableDictionary<string, ReferenceStats> res;
            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = await diag.LoadDelegateCountsAsync().ConfigureAwait(false);
            }

            Assert.NotEmpty(res);
        }

        [Fact]
        public async Task LoadCharacterArrayCountsAsync()
        {
            Action del = () => { Console.WriteLine(); };

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableDictionary<string, ReferenceStats> res;
            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = await diag.LoadCharacterArrayCountsAsync().ConfigureAwait(false);
            }

            Assert.NotEmpty(res);

            GC.KeepAlive(del);
        }

        [Fact]
        public async Task LoadThreadDetailsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ThreadAnalysis res;
            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = await diag.LoadThreadDetailsAsync().ConfigureAwait(false);
            }

            Assert.NotEmpty(res.StackFrameCounts);
            Assert.NotEmpty(res.ThreadStacks);
        }

        [Fact]
        public async Task GetAsyncMachineBreakdownsAsync()
        {
            var task = Task.Delay(1_000);

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableList<AsyncMachineBreakdown> res;
            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = await diag.GetAsyncMachineBreakdownsAsync().ConfigureAwait(false);
            }

            Assert.NotEmpty(res);

            await task.ConfigureAwait(false);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public async Task CreateAsync(int numProcs)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, numProcs).ConfigureAwait(false);
            Assert.NotNull(diag);
        }

        [Fact]
        public async Task AnalyzeAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            string written;
            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var res = await diag.AnalyzeAsync().ConfigureAwait(false);

                using (var writer = new StringWriter())
                {
                    await res.WriteToAsync(writer, 1, 1).ConfigureAwait(false);

                    written = writer.ToString();
                }
            }

            Assert.NotEmpty(written);
        }
    }
}
