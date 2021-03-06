using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
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
            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = (await diag.LoadStringCountsAsync().ConfigureAwait(false)).Value.ToImmutableDictionary(kv => kv.Key.Value, kv => kv.Value);
            }

            Assert.NotEmpty(res);
        }

        [Fact]
        public async Task LoadDelegateCountsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableDictionary<string, ReferenceStats> res;
            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = (await diag.LoadDelegateCountsAsync().ConfigureAwait(false)).Value.ToImmutableDictionary(kv => kv.Key.Value, kv => kv.Value);
            }

            Assert.NotEmpty(res);
        }

        [Fact]
        public async Task LoadCharacterArrayCountsAsync()
        {
            Action del = () => { Console.WriteLine(); };

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableDictionary<string, ReferenceStats> res;
            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = (await diag.LoadCharacterArrayCountsAsync().ConfigureAwait(false)).Value.ToImmutableDictionary(kv => kv.Key.Value, kv => kv.Value);
            }

            Assert.NotEmpty(res);

            GC.KeepAlive(del);
        }

        [Fact]
        public async Task LoadThreadDetailsAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ThreadAnalysis res;
            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = await diag.LoadThreadDetailsAsync().ConfigureAwait(false);
            }

            Assert.NotEmpty(res.StackFrameCounts);
            Assert.NotEmpty(res.ThreadStacks);
        }

        [Fact]
        public async Task GetAsyncMachineBreakdownsAsync()
        {
            var semaphore = new SemaphoreSlim(0);
            var t1 = FooAsync(() => "hello", 123, semaphore);
            var t2 = BarAsync((string x) => Console.WriteLine(x + x), "world", semaphore);
            var t3 = FizzAsync(1, '2', semaphore);
            var t4 = BuzzAsync(new object(), Guid.NewGuid(), semaphore);

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            ImmutableList<AsyncMachineBreakdown> res;
            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                res = (await diag.GetAsyncMachineBreakdownsAsync().ConfigureAwait(false)).Value;
            }

            Assert.NotEmpty(res);

            var fooState = Assert.Single(res, x => x.Type.TypeName.Contains(nameof(FooAsync)) && x.Type.TypeName.Contains(nameof(GetAsyncMachineBreakdownsAsync)) && x.Type.TypeName.Contains(nameof(DumpDiagnoserTests)));
            var barState = Assert.Single(res, x => x.Type.TypeName.Contains(nameof(BarAsync)) && x.Type.TypeName.Contains(nameof(GetAsyncMachineBreakdownsAsync)) && x.Type.TypeName.Contains(nameof(DumpDiagnoserTests)));
            var fizzState = Assert.Single(res, x => x.Type.TypeName.Contains(nameof(FizzAsync)) && x.Type.TypeName.Contains(nameof(GetAsyncMachineBreakdownsAsync)) && x.Type.TypeName.Contains(nameof(DumpDiagnoserTests)));
            var buzzState = Assert.Single(res, x => x.Type.TypeName.Contains(nameof(BuzzAsync)) && x.Type.TypeName.Contains(nameof(GetAsyncMachineBreakdownsAsync)) && x.Type.TypeName.Contains(nameof(DumpDiagnoserTests)));

            Assert.Contains("func", fooState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("hello", fooState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("semaphore", fooState.StateMachineFields.Select(x => x.InstanceField.Name));

            Assert.Contains("act", barState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("world", barState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("semaphore", barState.StateMachineFields.Select(x => x.InstanceField.Name));

            Assert.Contains("x", fizzState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("y", fizzState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("semaphore", fizzState.StateMachineFields.Select(x => x.InstanceField.Name));

            Assert.Contains("a", buzzState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("b", buzzState.StateMachineFields.Select(x => x.InstanceField.Name));
            Assert.Contains("semaphore", buzzState.StateMachineFields.Select(x => x.InstanceField.Name));

            semaphore.Release(4);

            await t1.ConfigureAwait(false);
            await t2.ConfigureAwait(false);
            await t3.ConfigureAwait(false);
            await t4.ConfigureAwait(false);

            static async ValueTask<string> FooAsync(Func<string> func, int hello, SemaphoreSlim semaphore)
            {
                await semaphore.WaitAsync();

                return func() + hello;
            }

            static async Task<int> BarAsync<T>(Action<T> act, string world, SemaphoreSlim semaphore)
            {
                await semaphore.WaitAsync();

                act((T)(object)world);

                return 15;
            }

            static async ValueTask FizzAsync(int x, char y, SemaphoreSlim semaphore)
            {
                await semaphore.WaitAsync();

                Console.WriteLine(x + y);
            }

            static async Task BuzzAsync(object a, Guid b, SemaphoreSlim semaphore)
            {
                await semaphore.WaitAsync();

                Console.WriteLine(a.ToString() + b);
            }
        }

        private sealed class _Ref1<T> { }
        private sealed class _Ref2<T, V> { }
        private readonly struct _Struct1<T> { }
        private readonly struct _Struct2<T, V> { }

        [Fact]
        public async Task Simple_GetGenericTypeParametersAsync()
        {
            object i = 0;
            object d = 0.0;
            var refOneParamRef = new _Ref1<string>();
            var refOneParamValue = new _Ref1<int>();
            var refTwoParamsRef = new _Ref2<string, object>();
            var refTwoParamValue = new _Ref2<int, double>();

            var exc = new Exception();
            var fi = new FileInfo("helloworld");
            object l = 0L;
            object f = 0.0f;
            object valueOneParamRef = new _Struct1<Exception>();
            object valueOneParamValue = new _Struct1<long>();
            object valueTwoParamsRef = new _Struct2<Exception, FileInfo>();
            object valueTwoParamValue = new _Struct2<long, float>();

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var stringType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.String");
                var objectType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.Object");
                var intType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.Int32");
                var doubleType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.Double");
                var exceptionType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.Exception");
                var fileInfoType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.IO.FileInfo");
                var longType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.Int64");
                var floatType = diag.typeDetails.Keys.Single(t => t.TypeName == "System.Single");

                var r1rType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Ref1`1") && t.TypeName.Contains("System.String"));
                var r1vType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Ref1`1") && t.TypeName.Contains("System.Int32"));
                var r2rType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Ref2`2") && t.TypeName.Contains("System.String"));
                var r2vType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Ref2`2") && t.TypeName.Contains("System.Int32"));

                var v1rType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Struct1`1") && t.TypeName.Contains("Exception"));
                var v1vType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Struct1`1") && t.TypeName.Contains("System.Int64"));
                var v2rType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Struct2`2") && t.TypeName.Contains("Exception"));
                var v2vType = diag.typeDetails.Keys.Single(t => t.TypeName.StartsWith("DumpDiag.Tests.DumpDiagnoserTests") && t.TypeName.Contains("_Struct2`2") && t.TypeName.Contains("System.Int64"));

                var args =
                    await diag.GetGenericTypeParametersAsync(
                        new[]
                        {
                            r1rType,
                            r1vType,
                            r2rType,
                            r2vType,
                            v1rType,
                            v1vType,
                            v2rType,
                            v2vType
                        }
                    )
                    .ConfigureAwait(false);

                var r1rArgs = args[r1rType];
                var r1vArgs = args[r1vType];
                var r2rArgs = args[r2rType];
                var r2vArgs = args[r2vType];

                var v1rArgs = args[v1rType];
                var v1vArgs = args[v1vType];
                var v2rArgs = args[v2rType];
                var v2vArgs = args[v2vType];

                Assert.Equal(r1rArgs, new[] { stringType });
                Assert.Equal(r1vArgs, new[] { intType });
                Assert.Equal(r2rArgs, new[] { stringType, objectType });
                Assert.Equal(r2vArgs, new[] { intType, doubleType });

                Assert.Equal(v1rArgs, new[] { exceptionType });
                Assert.Equal(v1vArgs, new[] { longType });
                Assert.Equal(v2rArgs, new[] { exceptionType, fileInfoType });
                Assert.Equal(v2vArgs, new[] { longType, floatType });
            }

            GC.KeepAlive(i);
            GC.KeepAlive(d);
            GC.KeepAlive(refOneParamRef);
            GC.KeepAlive(refOneParamValue);
            GC.KeepAlive(refTwoParamsRef);
            GC.KeepAlive(refTwoParamValue);

            GC.KeepAlive(l);
            GC.KeepAlive(f);
            GC.KeepAlive(exc);
            GC.KeepAlive(fi);
            GC.KeepAlive(valueOneParamRef);
            GC.KeepAlive(valueOneParamValue);
            GC.KeepAlive(valueTwoParamsRef);
            GC.KeepAlive(valueTwoParamValue);
        }

        private sealed class _Big<A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z> { }

        private sealed class _Nested<A> { internal sealed class _Inner<B> { } }

        [Fact]
        public async Task Complex_GetGenericTypeParametersAsync()
        {
            var b = new _Big<byte, sbyte, short, ushort, int, uint, long, ulong, float, double, decimal, byte?, sbyte?, short?, ushort?, int?, uint?, long?, ulong?, float?, double?, decimal?, string, object, Type, Guid>();
            var n = new _Nested<string>._Inner<int>();
            var d = (Func<Action<int>>)(() => a => Console.WriteLine(a));

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                var bigType = diag.typeDetails.Keys.Single(x => x.TypeName.Contains("_Big`26"));
                var bArgs = (await diag.GetGenericTypeParametersAsync(new[] { bigType }).ConfigureAwait(false)).Single().Value;

                Assert.Collection(
                    bArgs,
                    a => a.TypeName.StartsWith("System.UInt8"),
                    a => a.TypeName.StartsWith("System.Int8"),
                    a => a.TypeName.StartsWith("System.Int16"),
                    a => a.TypeName.StartsWith("System.UInt16"),
                    a => a.TypeName.StartsWith("System.Int32"),
                    a => a.TypeName.StartsWith("System.UInt32"),
                    a => a.TypeName.StartsWith("System.Int64"),
                    a => a.TypeName.StartsWith("System.UInt64"),
                    a => a.TypeName.StartsWith("System.Single"),
                    a => a.TypeName.StartsWith("System.Double"),
                    a => a.TypeName.StartsWith("System.Decimal"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.UInt8"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Int8"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Int16"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.UInt16"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Int32"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.UInt32"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Int64"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.UInt64"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Single"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Double"),
                    a => a.TypeName.StartsWith("System.Nullable`1[[System.Decimal"),
                    a => a.TypeName.StartsWith("System.String"),
                    a => a.TypeName.StartsWith("System.Object"),
                    a => a.TypeName.StartsWith("System.Type"),
                    a => a.TypeName.StartsWith("System.Guid")
                );

                var nestedType = diag.typeDetails.Keys.Single(x => x.TypeName.Contains("_Nested`1") && x.TypeName.Contains("_Inner`1"));
                var nArgs = (await diag.GetGenericTypeParametersAsync(new[] { nestedType }).ConfigureAwait(false)).Single().Value;
                Assert.Collection(
                    nArgs,
                    a => a.TypeName.StartsWith("System.String"),
                    a => a.TypeName.StartsWith("System.Int32")
                );
            }

            GC.KeepAlive(d);
            GC.KeepAlive(b);
            GC.KeepAlive(n);
        }

        [Fact]
        public async Task Exhaustive_GetGenericTypeParametersAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT).ConfigureAwait(false))
            {
                // only looking at the TOP level type if it has generic args
                var targetTypes =
                    diag.typeDetails.Keys.Where(
                        x =>
                        {
                            var ix = x.TypeName.IndexOf('`');
                            if (ix == -1)
                            {
                                return false;
                            }

                            if (IsArray(x.TypeName))
                            {
                                return false;
                            }

                            int firstNonDigitCharIndex;
                            for (firstNonDigitCharIndex = ix + 1; firstNonDigitCharIndex < x.TypeName.Length; firstNonDigitCharIndex++)
                            {
                                if (x.TypeName[firstNonDigitCharIndex] < '0' || x.TypeName[firstNonDigitCharIndex] > '9')
                                {
                                    break;
                                }
                            }

                            if (firstNonDigitCharIndex == x.TypeName.Length)
                            {
                                return false;
                            }

                            var c = x.TypeName[firstNonDigitCharIndex];
                            return c == '[';
                        }
                    )
                    .ToImmutableArray();

                var genTypes = await diag.GetGenericTypeParametersAsync(targetTypes).ConfigureAwait(false);
                Assert.NotEmpty(genTypes);

                foreach (var kv in genTypes)
                {
                    var genType = kv.Key;
                    var genArgs = kv.Value;

                    var expected = ParseGenericTypesFromName(genType.TypeName);

                    Assert.Equal(expected, genArgs.Select(g => g.TypeName));
                }
            }

            // type names look like System.Tuple`2[[Xunit.Abstractions.ITestCollection, xunit.abstractions],[System.Collections.Generic.List`1[[Xunit.Sdk.IXunitTestCase, xunit.core]], System.Private.CoreLib]]
            static ImmutableList<string> ParseGenericTypesFromName(string name)
            {
                var startIx = name.IndexOf('`');
                var startCount = startIx + 1;
                var endCount = startCount;
                while (char.IsDigit(name[endCount]))
                {
                    endCount++;
                }

                var numGenArgs = int.Parse(name[startCount..endCount]);

                var ret = ImmutableList.CreateBuilder<string>();

                var startType = endCount + 1;
                var ix = startType;
                var depth = 0;

                while (ret.Count < numGenArgs)
                {
                    var c = name[ix];
                    if (c == '[')
                    {
                        if (depth == 0)
                        {
                            startType = ix + 1;
                        }

                        depth++;
                    }
                    else if (c == ']')
                    {
                        depth--;

                        if (depth == 0)
                        {
                            var argWithModule = name[startType..ix];
                            var arg = argWithModule.Substring(0, argWithModule.LastIndexOf(','));
                            ret.Add(arg);
                        }
                    }

                    ix++;
                }

                return ret.ToImmutable();
            }

            // arrays end with [,,,,,,] (with one , for each additional dimension; zero , for one dimension arrays)
            static bool IsArray(string name)
            {
                if (!name.EndsWith("]"))
                {
                    return false;
                }

                var ix = name.Length - 2;
                while (ix >= 0)
                {
                    var c = name[ix];
                    if (c == ',')
                    {
                        ix--;
                        continue;
                    }

                    if (c == '[')
                    {
                        return true;
                    }

                    break;
                }

                return false;
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

            await using var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, numProcs).ConfigureAwait(false);
            Assert.NotNull(diag);
        }

        [Fact]
        public async Task AnalyzeAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            AnalyzeResult dotNetDumpRes;
            {
                using (var progressWriter = new StringWriter())
                {
                    var progress = new CommandLine.ProgressWrapper(false, progressWriter);

                    string written;
                    await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT, progress).ConfigureAwait(false))
                    {
                        dotNetDumpRes = await diag.AnalyzeAsync().ConfigureAwait(false);

                        using (var writer = new StringWriter())
                        {
                            await dotNetDumpRes.WriteToAsync(writer, 1, 1).ConfigureAwait(false);

                            written = writer.ToString();
                        }
                    }

                    Assert.NotEmpty(written);

                    progressWriter.Flush();
                    var progressText = progressWriter.ToString();

                    Assert.NotEmpty(progressText);
                }
            }

            if (OperatingSystem.IsWindows())
            {
                // WinDbg, one proc
                {
                    AnalyzeResult winDbgRes;
                    using (var progressWriter = new StringWriter())
                    {
                        var progress = new CommandLine.ProgressWrapper(false, progressWriter);

                        await using (var winDbg = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump).ConfigureAwait(false))
                        {
                            Assert.True(NativeLibrary.TryLoad(winDbg.DbgEngDllPath, out var libHandle));
                            Assert.True(DebugConnectWideThunk.TryCreate(libHandle, out var thunk, out _));

                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg.LocalPort, out var addr, out _));

                            string written;
                            await using (var diag = await DumpDiagnoser.CreateRemoteWinDbgAsync(thunk, new[] { addr }, TimeSpan.FromSeconds(30), progress).ConfigureAwait(false))
                            {
                                winDbgRes = await diag.AnalyzeAsync().ConfigureAwait(false);

                                using (var writer = new StringWriter())
                                {
                                    await winDbgRes.WriteToAsync(writer, 1, 1).ConfigureAwait(false);

                                    written = writer.ToString();
                                }
                            }

                            Assert.NotEmpty(written);

                            progressWriter.Flush();
                            var progressText = progressWriter.ToString();

                            Assert.NotEmpty(progressText);
                        }
                    }

                    CheckEquals(dotNetDumpRes, winDbgRes);
                }

                // WinDbg, a lot of procs
                {
                    AnalyzeResult winDbgMultiRes;
                    using (var progressWriter = new StringWriter())
                    {
                        var progress = new CommandLine.ProgressWrapper(false, progressWriter);

                        await using (var winDbg1 = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump, chosenPort: 10_001).ConfigureAwait(false))
                        await using (var winDbg2 = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump, chosenPort: 10_002).ConfigureAwait(false))
                        await using (var winDbg3 = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump, chosenPort: 10_003).ConfigureAwait(false))
                        await using (var winDbg4 = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump, chosenPort: 10_004).ConfigureAwait(false))
                        await using (var winDbg5 = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump, chosenPort: 10_005).ConfigureAwait(false))
                        await using (var winDbg6 = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump, chosenPort: 10_006).ConfigureAwait(false))
                        {
                            Assert.True(NativeLibrary.TryLoad(winDbg1.DbgEngDllPath, out var libHandle));
                            Assert.True(DebugConnectWideThunk.TryCreate(libHandle, out var thunk, out _));

                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg1.LocalPort, out var addr1, out _));
                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg2.LocalPort, out var addr2, out _));
                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg3.LocalPort, out var addr3, out _));
                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg4.LocalPort, out var addr4, out _));
                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg5.LocalPort, out var addr5, out _));
                            Assert.True(RemoteWinDbgAddress.TryCreate(IPAddress.Loopback.ToString(), winDbg6.LocalPort, out var addr6, out _));

                            string written;
                            await using (var diag = await DumpDiagnoser.CreateRemoteWinDbgAsync(thunk, new[] { addr1, addr2, addr3, addr4, addr5, addr6 }, TimeSpan.FromSeconds(30), progress).ConfigureAwait(false))
                            {
                                winDbgMultiRes = await diag.AnalyzeAsync().ConfigureAwait(false);

                                using (var writer = new StringWriter())
                                {
                                    await winDbgMultiRes.WriteToAsync(writer, 1, 1).ConfigureAwait(false);

                                    written = writer.ToString();
                                }
                            }

                            Assert.NotEmpty(written);

                            progressWriter.Flush();
                            var progressText = progressWriter.ToString();

                            Assert.NotEmpty(progressText);
                        }
                    }

                    CheckEquals(dotNetDumpRes, winDbgMultiRes);
                }
            }
        }



        // check the results are the same
        static void CheckEquals(AnalyzeResult a, AnalyzeResult b)
        {
            CheckEqualsSequential(a.AsyncStateMachineBreakdown.OrderBy(static x => x.Type).AsEnumerable(), b.AsyncStateMachineBreakdown.OrderBy(static x => x.Type).AsEnumerable());

            CheckEqualsSequential(a.AsyncStateMatchineStats.OrderBy(static x => (x.Key, x.Value)), b.AsyncStateMatchineStats.OrderBy(static x => (x.Key, x.Value)));

            CheckEqualsSequential(a.CharacterArrayReferenceStats.OrderBy(static x => (x.Key, x.Value)), b.CharacterArrayReferenceStats.OrderBy(static x => (x.Key, x.Value)));

            CheckEqualsSequential(a.DelegateReferenceStats.OrderBy(static x => (x.Key, x.Value)), b.DelegateReferenceStats.OrderBy(static x => (x.Key, x.Value)));

            Assert.Equal(a.HeapFragmentation, b.HeapFragmentation);

            Assert.Equal(a.PinAnalysis, b.PinAnalysis);

            CheckEqualsSequential(a.StringReferenceStats.OrderBy(static x => (x.Key, x.Value)), b.StringReferenceStats.OrderBy(static x => (x.Key, x.Value)));

            Assert.Equal(a.ThreadDetails, b.ThreadDetails);

            CheckEqualsSequential(a.TypeReferenceStats.OrderBy(static x => (x.Key, x.Value)), b.TypeReferenceStats.OrderBy(static x => (x.Key, x.Value)));

            // go item by item so we get better errors
            static void CheckEqualsSequential<T>(IEnumerable<T> x, IEnumerable<T> y)
            {
                var count = Math.Max(x.Count(), y.Count());

                using (var xE = x.GetEnumerator())
                using (var yE = y.GetEnumerator())
                {
                    var xRes = xE.MoveNext();
                    var yRes = yE.MoveNext();

                    Assert.Equal(xRes, yRes);

                    if (!xRes)
                    {
                        return;
                    }

                    Assert.Equal(xE.Current, yE.Current);
                }
            }
        }

        private sealed class _ResumableAsync: IResumableDiagnosisStorage
        {
            private readonly IResumableDiagnosisStorage inner;

            internal _ResumableAsync(IResumableDiagnosisStorage inner)
            {
                this.inner = inner;
            }

            public ValueTask DisposeAsync()
            => inner.DisposeAsync();

            public ValueTask IntializeWithVersionAsync(Version thisVersion)
            => inner.IntializeWithVersionAsync(thisVersion);

            public async ValueTask<(bool HasData, T Data)> LoadDataAsync<T>(string name) where T : struct, IDiagnosisSerializable<T>
            {
                var res = await inner.LoadDataAsync<T>(name).ConfigureAwait(false);
                Assert.True(res.HasData);

                return res;
            }

            public ValueTask StoreDataAsync<T>(string name, T data) where T : struct, IDiagnosisSerializable<T>
            => throw new Exception("No new data should be stored");
        }


        [Fact]
        public async Task ResumableAsync()
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            var resumeFile = Path.GetTempFileName();

            AnalyzeResult firstRun;
            {
                File.Delete(resumeFile);
                await using var resumeStorage = await FileBackedDiagnosisStorage.CreateAsync(new FileInfo(resumeFile)).ConfigureAwait(false);

                using (var progressWriter = new StringWriter())
                {
                    var progress = new CommandLine.ProgressWrapper(false, progressWriter);

                    string written;
                    await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT, progress, resumeStorage).ConfigureAwait(false))
                    {
                        firstRun = await diag.AnalyzeAsync().ConfigureAwait(false);

                        using (var writer = new StringWriter())
                        {
                            await firstRun.WriteToAsync(writer, 1, 1).ConfigureAwait(false);

                            written = writer.ToString();
                        }
                    }

                    Assert.NotEmpty(written);

                    progressWriter.Flush();
                    var progressText = progressWriter.ToString();

                    Assert.NotEmpty(progressText);
                }

                Assert.True(File.Exists(resumeFile));
            }

            AnalyzeResult secondRun;
            {
                var innerStorage = await FileBackedDiagnosisStorage.CreateAsync(new FileInfo(resumeFile)).ConfigureAwait(false);

                await using var resumeStorage = new _ResumableAsync(innerStorage);

                using (var progressWriter = new StringWriter())
                {
                    var progress = new CommandLine.ProgressWrapper(false, progressWriter);

                    string written;
                    await using (var diag = await DumpDiagnoser.CreateDotNetDumpAsync(dump.DotNetDumpPath, dump.DumpFile, PROCESS_COUNT, progress, resumeStorage).ConfigureAwait(false))
                    {
                        secondRun = await diag.AnalyzeAsync().ConfigureAwait(false);

                        using (var writer = new StringWriter())
                        {
                            await firstRun.WriteToAsync(writer, 1, 1).ConfigureAwait(false);

                            written = writer.ToString();
                        }
                    }

                    Assert.NotEmpty(written);

                    progressWriter.Flush();
                    var progressText = progressWriter.ToString();

                    Assert.NotEmpty(progressText);
                }
            }

            CheckEquals(firstRun, secondRun);
        }
    }
}
