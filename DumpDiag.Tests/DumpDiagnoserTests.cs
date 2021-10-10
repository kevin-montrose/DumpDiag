using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
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

            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var res = await diag.LoadStringCountsAsync().ConfigureAwait(false);

                Assert.NotEmpty(res);
            }
        }

        [Fact]
        public async Task LoadDelegateCountsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var res = await diag.LoadDelegateCountsAsync().ConfigureAwait(false);

                Assert.NotEmpty(res);
            }
        }

        [Fact]
        public async Task LoadCharacterArrayCountsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var res = await diag.LoadCharacterArrayCountsAsync().ConfigureAwait(false);

                Assert.NotEmpty(res);
            }
        }

        [Fact]
        public async Task LoadThreadDetailsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var res = await diag.LoadThreadDetailsAsync().ConfigureAwait(false);

                Assert.NotEmpty(res.StackFrameCounts);
                Assert.NotEmpty(res.ThreadStacks);
            }
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

            await using var diag = await DumpDiagnoser.CreateAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false);

            var res = await diag.AnalyzeAsync().ConfigureAwait(false);

            string written;
            using (var writer = new StringWriter())
            {
                await res.WriteToAsync(writer, 1).ConfigureAwait(false);

                written = writer.ToString();
            }

            Assert.NotEmpty(written);
        }
    }
}
