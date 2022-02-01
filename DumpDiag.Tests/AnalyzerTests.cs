using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace DumpDiag.Tests
{
    internal delegate void WellKnownDelegate(string x, int y);

    [CollectionDefinition(nameof(MustRunInIsolationTestCollection), DisableParallelization = true)]
    public class MustRunInIsolationTestCollection { }

    // because we care a lot about the state of _this specific process_ we only want to run code in this class
    // any other tests shouldn't run at the same time, so we can muck about with process state without worrying about
    // conflicts
    [Collection(nameof(MustRunInIsolationTestCollection))]
    public class AnalyzerTests : IDisposable
    {
        // todo: de-dupe
        private static ArrayPool<char>[] CharArrayPools =
            new[]
            {
                ArrayPool<char>.Shared,
                new PoisonedArrayPool<char>(),
                new LeakTrackingArrayPool<char>(ArrayPool<char>.Shared),
                new LeakTrackingArrayPool<char>(new PoisonedArrayPool<char>()),
            };

        private static string[] AnalyzerTypeNames =
            new[]
            {
                nameof(DotNetDumpAnalyzerProcess), 
                // todo: make this conditional on OS
                nameof(RemoteWinDbg),
            };

        public static IEnumerable<object[]> ArrayPoolParameters
        {
            get
            {
                var ret = new List<object[]>();
                foreach (var pool in CharArrayPools)
                {
                    ret.Add(new object[] { pool });
                }

                return ret;
            }
        }

        public object GCHAndle { get; private set; }

        public ITestOutputHelper Output { get; }

        public AnalyzerTests(ITestOutputHelper output)
        {
            Output = output;
        }

        public void Dispose()
        {
            // after each test, check that we haven't leaked anything
            foreach (var pool in CharArrayPools.OfType<LeakTrackingArrayPool<char>>())
            {
                pool.AssertEmpty();
            }
        }

        // helper for creating all the IAnalyzers implemented
        private async ValueTask RunForAllAnalyzersAsync<T>(ArrayPool<char> pool, SelfDumpHelper dump, T parameters, Func<IAnalyzer, T, ValueTask> run)
        {
            foreach (var analyzerName in AnalyzerTypeNames)
            {
                Output.WriteLine($"Creating Analyzer: {analyzerName}");
                var (analyzer, toDispose) = await CreateAnalyzerAsync(analyzerName, pool, dump);
                var completed = false;
                try
                {
                    Output.WriteLine($"Running For Analyzer: {analyzerName}");
                    await run(analyzer, parameters).ConfigureAwait(false);
                    completed = true;
                }
                finally
                {
                    if (completed)
                    {
                        Output.WriteLine($"Analyzer Completed: {analyzerName}");
                    }

                    Output.WriteLine($"Disposing After Analyzer: {analyzerName}");

                    await analyzer.DisposeAsync().ConfigureAwait(false);
                    if (toDispose != null)
                    {
                        await toDispose.DisposeAsync().ConfigureAwait(false);
                    }

                    Output.WriteLine($"Disposed Analyzer: {analyzerName}");
                }
            }

            // create appropriate analyzer
            static async ValueTask<(IAnalyzer Analyzer, IAsyncDisposable OtherContext)> CreateAnalyzerAsync(string analyzerName, ArrayPool<char> pool, SelfDumpHelper dump)
            {
                switch (analyzerName)
                {
                    case nameof(DotNetDumpAnalyzerProcess):
                        return (await DotNetDumpAnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false), null);
                    case nameof(RemoteWinDbg):
                        if (!OperatingSystem.IsWindows())
                        {
                            throw new InvalidOperation("Test only valid on Windows");
                        }
                        var helper = await WinDbgHelper.CreateWinDbgInstanceAsync(WinDbgHelper.WinDbgLocations.First(), dump).ConfigureAwait(false);

                        Assert.True(NativeLibrary.TryLoad(helper.DbgEngDllPath, out var libHandle));

                        var loaded = DebugConnectWideThunk.TryCreate(libHandle, out var thunk, out var error);
                        Assert.True(loaded, error);

                        return (await RemoteWinDbg.CreateAsync(pool, thunk, IPAddress.Loopback.ToString(), helper.LocalPort, TimeSpan.FromSeconds(30)), helper);
                    default:
                        throw new Exception($"Unexpected {analyzerName}");

                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task DumpHeapLiveAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            Func<IAsyncEnumerable<HeapEntry>> shouldMatch = () => GetShouldMatchAsync(dump);

            await RunForAllAnalyzersAsync(pool, dump, shouldMatch, static (analyzer, data) => DumpHeapLiveCommonAsync(analyzer, data)).ConfigureAwait(false);

            // stuff that should match between two runs - uses whatever analyzer, not a test run specific run
            static async IAsyncEnumerable<HeapEntry> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var shouldMatch = ImmutableList.CreateBuilder<HeapEntry>();

                    var skipTheRest = false;

                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("dumpheap -live")).ConfigureAwait(false))
                    {
                        using var lineRef = line;

                        if (skipTheRest)
                        {
                            continue;
                        }

                        var str = lineRef.ToString();
                        if (str.Contains("Free"))
                        {
                            continue;
                        }

                        if (str.Trim().StartsWith("MT "))
                        {
                            skipTheRest = true;
                            continue;
                        }

                        var match = Regex.Match(str, @"^ ([0-9a-f]+) \s+ ([0-9a-f]+) \s+ ([0-9]+) \s* $", RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var addr = long.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
                        var mt = long.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
                        var size = int.Parse(match.Groups[3].Value);

                        yield return new HeapEntry(addr, mt, size, true);
                    }
                }
            }

            // common bits for all analyzers
            static async ValueTask DumpHeapLiveCommonAsync(IAnalyzer analyze, Func<IAsyncEnumerable<HeapEntry>> shouldMatchDel)
            {
                await using (var shouldMatchE = shouldMatchDel().GetAsyncEnumerator())
                await using (var actualE = analyze.LoadHeapAsync(LoadHeapMode.Live).GetAsyncEnumerator())
                {
                    while (true)
                    {
                        var shouldMatchRes = await shouldMatchE.MoveNextAsync().ConfigureAwait(false);
                        var actualRes = await actualE.MoveNextAsync().ConfigureAwait(false);

                        Assert.Equal(shouldMatchRes, actualRes);

                        if (!shouldMatchRes)
                        {
                            break;
                        }

                        var shouldBe = shouldMatchE.Current;
                        var actual = actualE.Current;

                        Assert.Equal(shouldBe, actual);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task DumpHeapDeadAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            Func<IAsyncEnumerable<HeapEntry>> shouldMatch = () => GetShouldMatchAsync(dump);

            await RunForAllAnalyzersAsync(pool, dump, shouldMatch, static (analyzer, data) => DumpHeapDeadCommonAsync(analyzer, data)).ConfigureAwait(false);

            static async IAsyncEnumerable<HeapEntry> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                var shouldMatchBuilder = ImmutableList.CreateBuilder<HeapEntry>(); ;

                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var skipRest = false;

                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("dumpheap -dead")).ConfigureAwait(false))
                    {
                        using var lineRef = line;

                        if (skipRest)
                        {
                            continue;
                        }

                        var str = lineRef.ToString();
                        if (str.Contains("Free"))
                        {
                            continue;
                        }

                        if (str.Trim().StartsWith("MT "))
                        {
                            skipRest = true;
                            continue;
                        }

                        var match = Regex.Match(str, @"^ ([0-9a-f]+) \s+ ([0-9a-f]+) \s+ ([0-9]+) \s* $", RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var addr = long.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
                        var mt = long.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
                        var size = int.Parse(match.Groups[3].Value);

                        yield return new HeapEntry(addr, mt, size, false);
                    }
                }
            }

            // common bits for all analyzers
            static async ValueTask DumpHeapDeadCommonAsync(IAnalyzer analyzer, Func<IAsyncEnumerable<HeapEntry>> shouldMatchDel)
            {
                await using (var shouldMatchE = shouldMatchDel().GetAsyncEnumerator())
                await using (var actualE = analyzer.LoadHeapAsync(LoadHeapMode.Dead).GetAsyncEnumerator())
                {
                    while (true)
                    {
                        var shouldMatchRes = await shouldMatchE.MoveNextAsync().ConfigureAwait(false);
                        var actualRes = await actualE.MoveNextAsync().ConfigureAwait(false);

                        Assert.Equal(shouldMatchRes, actualRes);

                        if (!shouldMatchRes)
                        {
                            break;
                        }

                        var shouldBe = shouldMatchE.Current;
                        var actual = actualE.Current;

                        Assert.Equal(shouldBe, actual);
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadStringTypeDetailsAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            var shouldMatch = await GetCommonHeapEntryAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(pool, dump, shouldMatch, static (analyzer, data) => LoadStringTypeDetailsCommonAsync(analyzer, data)).ConfigureAwait(false);

            // common bits for all analyzers
            static async ValueTask LoadStringTypeDetailsCommonAsync(IAnalyzer analyzer, HeapEntry stringRef)
            {
                var stringDetails = await analyzer.LoadStringDetailsAsync(stringRef).ConfigureAwait(false);

                // this has been constant since like... 2003?  Just hard code the check.
                Assert.Equal(8, stringDetails.LengthOffset);
                Assert.Equal(12, stringDetails.FirstCharOffset);
            }

            static async ValueTask<HeapEntry> GetCommonHeapEntryAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                    var stringType = typeDetails.Single(x => x.Key.TypeName == "System.String").Value.Single();
                    HeapEntry? stringRef = null;

                    await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (stringRef == null && stringType == entry.MethodTable)
                        {
                            stringRef = entry;
                        }
                    }

                    Assert.NotNull(stringRef);

                    return stringRef.Value;
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadStackTraceForThreadAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(pool, dump, shouldMatch, static (analyzer, data) => LoadStackTraceForThreadCommonAsync(analyzer, data)).ConfigureAwait(false);

            // common bits for all analyzers
            static async ValueTask LoadStackTraceForThreadCommonAsync(IAnalyzer analyze, (int ThreadCount, ImmutableArray<ImmutableList<AnalyzerStackFrame>> StackFrames) shouldMatch)
            {
                var actualThreadCount = await analyze.CountActiveThreadsAsync().ConfigureAwait(false);

                Assert.Equal(shouldMatch.ThreadCount, actualThreadCount);

                for (var i = 0; i < actualThreadCount; i++)
                {
                    var actualStackFrameBuilder = ImmutableList.CreateBuilder<AnalyzerStackFrame>();
                    foreach (var frame in await analyze.LoadStackTraceForThreadAsync(i).ConfigureAwait(false))
                    {
                        actualStackFrameBuilder.Add(frame);
                    }

                    var actualStackFrame = actualStackFrameBuilder.ToImmutable();
                    var expectedStackFrame = shouldMatch.StackFrames[i];

                    Assert.Equal(expectedStackFrame.AsEnumerable(), actualStackFrame.AsEnumerable());
                }
            }

            // load the expected results for all analyzers
            static async ValueTask<(int ThreadCount, ImmutableArray<ImmutableList<AnalyzerStackFrame>> StackFrames)> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                var stackFramesByThreadBuilder = ImmutableArray.CreateBuilder<ImmutableList<AnalyzerStackFrame>>();

                int expectedThreadCount;

                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    expectedThreadCount = 0;
                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("threads")).ConfigureAwait(false))
                    {
                        line.Dispose();
                        expectedThreadCount++;
                    }

                    for (var i = 0; i < expectedThreadCount; i++)
                    {
                        // switch to thread
                        await foreach (var line in analyze.SendCommand(Command.CreateCommandWithCount("threads", i)).ConfigureAwait(false))
                        {
                            line.Dispose();
                        }

                        var expectedStackFrameBuilder = ImmutableList.CreateBuilder<AnalyzerStackFrame>();
                        await foreach (var line in analyze.SendCommand(Command.CreateCommand("clrstack")).ConfigureAwait(false))
                        {
                            var str = line.ToString();
                            line.Dispose();

                            var match = Regex.Match(str, @"^ ([0-9a-f]+) \s+ ([0-9a-f]+) \s+ (.*?) $", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
                            if (!match.Success)
                            {
                                continue;
                            }

                            var sp = long.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
                            var ip = long.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
                            var cs = match.Groups[3].Value;

                            expectedStackFrameBuilder.Add(new AnalyzerStackFrame(sp, ip, cs));
                        }

                        stackFramesByThreadBuilder.Add(expectedStackFrameBuilder.ToImmutable());
                    }
                }

                return (expectedThreadCount, stackFramesByThreadBuilder.ToImmutable());
            }
        }

        [Fact]
        public async Task ReadStringAsync() // this is pretty expensive, so don't bother with all the different options
        {
            var poisonTrackingPool = CharArrayPools.OfType<LeakTrackingArrayPool<char>>().Single(x => x.inner is PoisonedArrayPool<char>);

            var uniqueString = Guid.NewGuid().ToString();

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                poisonTrackingPool,
                dump,
                (shouldMatch.KnownDetails, shouldMatch.LiveStrings, uniqueString),
                static (analyzer, data) => ReadStringCommonAsync(analyzer, data)
            )
            .ConfigureAwait(false);

            GC.KeepAlive(uniqueString);

            // common functionality for all analyzers
            static async ValueTask ReadStringCommonAsync(IAnalyzer analyzer, (StringDetails KnownDetails, ImmutableList<string> LiveStrings, string UniqueString) shouldMatch)
            {
                var stringDetails = shouldMatch.KnownDetails;

                var stringRefsBuilder = ImmutableList.CreateBuilder<HeapEntry>();

                await foreach (var entry in analyzer.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                {
                    if (stringDetails.MethodTable == entry.MethodTable)
                    {
                        stringRefsBuilder.Add(entry);
                    }
                }

                var stringRefs = stringRefsBuilder.ToImmutable();

                var stringsBuilder = ImmutableList.CreateBuilder<string>();
                foreach (var stringEntry in stringRefs)
                {
                    var peaked = await analyzer.PeakStringAsync(stringDetails, stringEntry).ConfigureAwait(false);

                    string fromPeaking;
                    if (peaked.ReadFullString)
                    {
                        fromPeaking = peaked.PeakedString;
                    }
                    else
                    {
                        var startAddr = stringEntry.Address + stringDetails.FirstCharOffset + peaked.PeakedString.Length * sizeof(char);
                        var remainingLen = peaked.ActualLength - peaked.PeakedString.Length;
                        var rest = await analyzer.LoadCharsAsync(startAddr, remainingLen).ConfigureAwait(false);

                        fromPeaking = peaked.PeakedString + rest;
                    }

                    stringsBuilder.Add(fromPeaking);
                }

                var strings = stringsBuilder.ToImmutable();

                Assert.Contains(shouldMatch.UniqueString, strings);
                Assert.Equal(shouldMatch.LiveStrings.AsEnumerable(), strings.AsEnumerable());
            }

            // get the expected results
            static async ValueTask<(StringDetails KnownDetails, ImmutableList<string> LiveStrings)> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile))
                {
                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                    var stringType = typeDetails.Single(x => x.Key.TypeName == "System.String").Value.Single();

                    var stringRefsBuilder = ImmutableList.CreateBuilder<HeapEntry>();

                    await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (stringType == entry.MethodTable)
                        {
                            stringRefsBuilder.Add(entry);
                        }
                    }

                    var stringRefs = stringRefsBuilder.ToImmutable();

                    var stringDetails = await analyze.LoadStringDetailsAsync(stringRefs.First()).ConfigureAwait(false);

                    var stringsBuilder = ImmutableList.CreateBuilder<string>();
                    foreach (var stringEntry in stringRefs)
                    {
                        var peaked = await analyze.PeakStringAsync(stringDetails, stringEntry).ConfigureAwait(false);

                        string fromPeaking;
                        if (peaked.ReadFullString)
                        {
                            fromPeaking = peaked.PeakedString;
                        }
                        else
                        {
                            var startAddr = stringEntry.Address + stringDetails.FirstCharOffset + peaked.PeakedString.Length * sizeof(char);
                            var remainingLen = peaked.ActualLength - peaked.PeakedString.Length;
                            var rest = await analyze.LoadCharsAsync(startAddr, remainingLen).ConfigureAwait(false);

                            fromPeaking = peaked.PeakedString + rest;
                        }

                        var stringLen = await analyze.LoadStringLengthAsync(stringDetails, stringEntry).ConfigureAwait(false);
                        string val = await analyze.LoadCharsAsync(stringEntry.Address + stringDetails.FirstCharOffset, stringLen).ConfigureAwait(false);

                        Assert.Equal(val, fromPeaking);

                        stringsBuilder.Add(val);
                    }

                    return (stringDetails, stringsBuilder.ToImmutable());
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadDelegateDetailsAsync(ArrayPool<char> pool)
        {
            static void Foo(string x, int y)
            {
                Console.WriteLine(x + y);
            }

            var q = (new Random()).Next();
            WellKnownDelegate instanceDelegate = (x, y) => { Console.WriteLine(q + x + y); };
            WellKnownDelegate staticDelegate = Foo;
            WellKnownDelegate chainedDelegate = instanceDelegate + staticDelegate;

            Assert.NotEqual(instanceDelegate.Method.Name, staticDelegate.Method.Name);

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump, instanceDelegate, staticDelegate).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                 pool,
                 dump,
                 shouldMatch,
                 static (analyzer, data) => LoadDelegateDetailsCommonAsync(analyzer, data)
            )
            .ConfigureAwait(false);

            GC.KeepAlive(instanceDelegate);
            GC.KeepAlive(staticDelegate);
            GC.KeepAlive(chainedDelegate);

            // run each analyzer
            static async ValueTask LoadDelegateDetailsCommonAsync(
                IAnalyzer analyzer,
                (ImmutableList<HeapEntry> DelegateHeapEntries, ImmutableList<DelegateDetails> DelegateDetails, WellKnownDelegate InstanceDelegate, WellKnownDelegate StaticDelegate) shouldMatch
            )
            {
                var instanceDelegate = shouldMatch.InstanceDelegate;
                var staticDelegate = shouldMatch.StaticDelegate;

                var delegateDetailsBuilder = ImmutableList.CreateBuilder<DelegateDetails>();
                foreach (var he in shouldMatch.DelegateHeapEntries)
                {
                    var del = await analyzer.LoadDelegateDetailsAsync(he).ConfigureAwait(false);
                    delegateDetailsBuilder.Add(del);
                }
                var delegateDetails = delegateDetailsBuilder.ToImmutable();

                // corresponding entries...
                var instanceDelegateDetails = Assert.Single(delegateDetails, x => x.MethodDetails.Length == 1 && x.MethodDetails[0].BackingMethodName.Contains(instanceDelegate.Method.Name));
                var staticDelegateDetails = Assert.Single(delegateDetails, x => x.MethodDetails.Length == 1 && x.MethodDetails[0].BackingMethodName.Contains(staticDelegate.Method.Name));
                var chainedDelegateDetails = Assert.Single(delegateDetails, x => x.MethodDetails.Length == 2);

                // they should all be different
                Assert.NotEqual(instanceDelegateDetails, staticDelegateDetails);
                Assert.NotEqual(instanceDelegateDetails, chainedDelegateDetails);
                Assert.NotEqual(staticDelegateDetails, chainedDelegateDetails);

                // instance should have an object, static might or might not
                Assert.NotEqual(0, instanceDelegateDetails.MethodDetails.Single().TargetAddress);

                // chained should have the methods for the other delegates
                Assert.Single(chainedDelegateDetails.MethodDetails, x => x.BackingMethodName.Contains(instanceDelegate.Method.Name));
                Assert.Single(chainedDelegateDetails.MethodDetails, x => x.BackingMethodName.Contains(staticDelegate.Method.Name));
            }

            // load common details between analyzers
            static async
                ValueTask<(ImmutableList<HeapEntry> DelegateHeapEntries, ImmutableList<DelegateDetails> DelegateDetails, WellKnownDelegate InstanceDelegate, WellKnownDelegate StaticDelegate)>
                GetShouldMatchAsync(
                    SelfDumpHelper dump,
                    WellKnownDelegate instanceDelegate,
                    WellKnownDelegate staticDelegate
                )
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                    var wellKnownDelegateType = typeDetails.Single(x => x.Key.TypeName == typeof(WellKnownDelegate).FullName).Value.Single();

                    var instancesBuilder = ImmutableList.CreateBuilder<HeapEntry>();

                    await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (wellKnownDelegateType == entry.MethodTable)
                        {
                            instancesBuilder.Add(entry);
                        }
                    }
                    var instances = instancesBuilder.ToImmutable();

                    var delegateDetailsBuilder = ImmutableList.CreateBuilder<DelegateDetails>();
                    foreach (var he in instances)
                    {
                        var del = await analyze.LoadDelegateDetailsAsync(he).ConfigureAwait(false);
                        delegateDetailsBuilder.Add(del);
                    }
                    var delegateDetails = delegateDetailsBuilder.ToImmutable();

                    return (instances, delegateDetails, instanceDelegate, staticDelegate);
                }
            }
        }

        private abstract class Foo { }

        private sealed class Bar : Foo { }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task ReadClassHiearchyAsync(ArrayPool<char> pool)
        {
            var q = (new Random()).Next();
            WellKnownDelegate instanceDelegate = (x, y) => { Console.WriteLine(q + x + y); };
            var knownTypeInst = new Bar();
            var objInst = new object();

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                 pool,
                 dump,
                 shouldMatch,
                 static (analyzer, data) => ReadClassHierarchyCommonAsync(analyzer, data)
            )
            .ConfigureAwait(false);

            GC.KeepAlive(instanceDelegate);
            GC.KeepAlive(knownTypeInst);
            GC.KeepAlive(objInst);

            // run the common parts for each analyzer
            static async ValueTask ReadClassHierarchyCommonAsync(
                IAnalyzer analyzer,
                (HeapEntry Del, HeapEntry Bar, HeapEntry Obj) shouldMatch
            )
            {
                var delEntry = shouldMatch.Del;
                var barEntry = shouldMatch.Bar;
                var objEntry = shouldMatch.Obj;

                var delTypePath = await ConcatAsync(ReadClassHiearchyAsync(analyzer, delEntry.MethodTable)).ConfigureAwait(false);
                var barTypePath = await ConcatAsync(ReadClassHiearchyAsync(analyzer, barEntry.MethodTable)).ConfigureAwait(false);
                var objTypePath = await ConcatAsync(ReadClassHiearchyAsync(analyzer, objEntry.MethodTable)).ConfigureAwait(false);

                Assert.Equal("DumpDiag.Tests.WellKnownDelegate -> System.MulticastDelegate -> System.Delegate -> System.Object", delTypePath);
                Assert.Equal("DumpDiag.Tests.AnalyzerTests+Bar -> DumpDiag.Tests.AnalyzerTests+Foo -> System.Object", barTypePath);
                Assert.Equal("System.Object", objTypePath);
            }

            // get what we expect to find regardless of analyzer
            static async
                ValueTask<(HeapEntry Del, HeapEntry Bar, HeapEntry Obj)>
                GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile))
                {
                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                    var wellKnownDelegateType = typeDetails.Single(x => x.Key.TypeName == typeof(WellKnownDelegate).FullName).Value.Single();
                    var barType = typeDetails.Single(x => x.Key.TypeName == typeof(Bar).FullName).Value.Single();
                    var objType = typeDetails.Single(x => x.Key.TypeName == typeof(object).FullName).Value.Single();

                    HeapEntry? delEntry = null;
                    HeapEntry? barEntry = null;
                    HeapEntry? objEntry = null;

                    await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (delEntry == null && wellKnownDelegateType == entry.MethodTable)
                        {
                            delEntry = entry;
                        }
                        if (barEntry == null && barType == entry.MethodTable)
                        {
                            barEntry = entry;
                        }
                        if (objEntry == null && objType == entry.MethodTable)
                        {
                            objEntry = entry;
                        }
                    }

                    Assert.NotNull(delEntry);
                    Assert.NotNull(barEntry);
                    Assert.NotNull(objEntry);

                    return (delEntry.Value, barEntry.Value, objEntry.Value);
                }
            }

            // get class hierarchy, which takes multiple calls
            static async IAsyncEnumerable<string> ReadClassHiearchyAsync(IAnalyzer analyze, long mt)
            {
                var eeClass = await analyze.LoadEEClassAsync(mt).ConfigureAwait(false);

                var curClass = await analyze.LoadEEClassDetailsAsync(eeClass).ConfigureAwait(false);

                while (true)
                {
                    yield return curClass.ClassName;

                    if (curClass.ParentEEClass == 0)
                    {
                        yield break;
                    }

                    curClass = await analyze.LoadEEClassDetailsAsync(curClass.ParentEEClass).ConfigureAwait(false);
                }
            }

            // helper
            static async ValueTask<string> ConcatAsync(IAsyncEnumerable<string> toJoin)
            {
                var ret = "";
                await foreach (var item in toJoin.ConfigureAwait(false))
                {
                    if (ret.Length != 0)
                    {
                        ret += " -> ";
                    }

                    ret += item;
                }

                return ret;
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task ReadCharacterArrayAsync(ArrayPool<char> pool)
        {
            var charArr = $"abcd_{Guid.NewGuid()}_efgh".ToArray();

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                 pool,
                 dump,
                 (shouldMatch.LiveCharArrays, shouldMatch.LiveCharArrayValues, new string(charArr)),
                 static (analyzer, data) => ReadCharacterArrayCommonAsync(analyzer, data)
            )
            .ConfigureAwait(false);

            GC.KeepAlive(charArr);

            // common bits for all analyzers
            static async ValueTask ReadCharacterArrayCommonAsync(IAnalyzer analyzer, (ImmutableList<HeapEntry> LiveCharArrays, ImmutableList<string> LiveCharArrayValues, string ShouldContain) shouldMatch)
            {
                var charArrsBuilder = ImmutableList.CreateBuilder<string>();
                foreach (var entry in shouldMatch.LiveCharArrays)
                {
                    var details = await analyzer.LoadArrayDetailsAsync(entry).ConfigureAwait(false);
                    var value = details.Length == 0 ? "" : await analyzer.LoadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false);
                    charArrsBuilder.Add(value);
                }

                var charArrs = charArrsBuilder.ToImmutable();

                Assert.Equal(shouldMatch.LiveCharArrayValues.AsEnumerable(), charArrs.AsEnumerable());
                Assert.Contains(shouldMatch.ShouldContain, charArrs);
            }

            // get expected common bits
            static async ValueTask<(ImmutableList<HeapEntry> LiveCharArrays, ImmutableList<string> LiveCharArrayValues)> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile))
                {
                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                    var charArrayType = typeDetails.Single(x => x.Key.TypeName == "System.Char[]").Value.Single();

                    var charArrayHeapEntriesBuilder = ImmutableList.CreateBuilder<HeapEntry>();
                    await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (charArrayType == entry.MethodTable)
                        {
                            charArrayHeapEntriesBuilder.Add(entry);
                        }
                    }
                    var charArrayHeapEntries = charArrayHeapEntriesBuilder.ToImmutable();

                    var charArrsBuilder = ImmutableList.CreateBuilder<string>();
                    foreach (var entry in charArrayHeapEntries)
                    {
                        var details = await analyze.LoadArrayDetailsAsync(entry).ConfigureAwait(false);
                        var value = details.Length == 0 ? "" : await analyze.LoadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false);
                        charArrsBuilder.Add(value);
                    }

                    return (charArrayHeapEntries, charArrsBuilder.ToImmutable());
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadAsyncStateMachinesAsync(ArrayPool<char> pool)
        {
            var completeSignal = new SemaphoreSlim(0);

            var incompleteTask = LoadAsyncStateMachinesAsync_IncompleteTaskAsync(completeSignal);
            var incompleteValueTask = LoadAsyncStateMachinesAsync_IncompleteValueTaskAsync(completeSignal);
            var incompleteTaskRun = Task.Run(() => completeSignal.Wait());

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                pool,
                dump,
                shouldMatch,
                static (analyzer, shouldMatch) => LoadAsyncstateMachinesCommonAsync(analyzer, shouldMatch)
            )
            .ConfigureAwait(false);

            completeSignal.Release(3);

            await incompleteTask.ConfigureAwait(false);
            await incompleteValueTask.ConfigureAwait(false);
            await incompleteTaskRun.ConfigureAwait(false);

            GC.KeepAlive(incompleteTask);
            GC.KeepAlive(incompleteTaskRun);

            // run each analyzer
            static async ValueTask LoadAsyncstateMachinesCommonAsync(IAnalyzer analyzer, ImmutableList<AsyncStateMachineDetails> shouldMatch)
            {
                var actualBuilder = ImmutableList.CreateBuilder<AsyncStateMachineDetails>();
                await foreach (var entry in analyzer.LoadAsyncStateMachinesAsync().ConfigureAwait(false))
                {
                    actualBuilder.Add(entry);
                }
                var actual = actualBuilder.ToImmutable();

                Assert.Equal(shouldMatch.AsEnumerable(), actual.AsEnumerable());

                // check that our example tasks were picked up
                Assert.Contains(actual, x => x.Description.Contains("<" + nameof(LoadAsyncStateMachinesAsync_IncompleteTaskAsync) + ">"));
                Assert.Contains(actual, x => x.Description.Contains("<" + nameof(LoadAsyncStateMachinesAsync_IncompleteValueTaskAsync) + ">"));
                Assert.Contains(actual,
                    x =>
                        x.Description.Contains(nameof(LoadAsyncStateMachinesAsync)) &&
                        !x.Description.Contains("<" + nameof(LoadAsyncStateMachinesAsync_IncompleteTaskAsync) + ">") &&
                        !x.Description.Contains("<" + nameof(LoadAsyncStateMachinesAsync_IncompleteValueTaskAsync) + ">")
                );
            }

            // load expected results for all analyzers
            static async ValueTask<ImmutableList<AsyncStateMachineDetails>> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile))
                {
                    var shouldMatchBuilder = ImmutableList.CreateBuilder<AsyncStateMachineDetails>();

                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("dumpasync -completed")).ConfigureAwait(false))
                    {
                        var entryStr = line.ToString();
                        line.Dispose();

                        var match = Regex.Match(entryStr, @"^ (?<addr> [0-9a-f]+) \s+ (?<mt> [0-9a-f]+) \s+ (?<size> \d+) \s+ (?<status> \S+) \s+ (?<state> \-?\d+) \s+ (?<description> .*?) $", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
                        if (match.Success)
                        {
                            var addr = long.Parse(match.Groups["addr"].Value, NumberStyles.HexNumber);
                            var mt = long.Parse(match.Groups["mt"].Value, NumberStyles.HexNumber);
                            var size = int.Parse(match.Groups["size"].Value);
                            var desc = match.Groups["description"].Value;

                            shouldMatchBuilder.Add(new AsyncStateMachineDetails(addr, mt, size, desc));
                        }
                    }

                    var shouldMatch = shouldMatchBuilder.ToImmutable();

                    return shouldMatch;
                }
            }
        }

        private static async Task<long> LoadAsyncStateMachinesAsync_IncompleteTaskAsync(SemaphoreSlim signal)
        {
            var sw = Stopwatch.StartNew();
            await signal.WaitAsync();

            return sw.ElapsedTicks;
        }

        private static async Task<long> LoadAsyncStateMachinesAsync_IncompleteValueTaskAsync(SemaphoreSlim signal)
        {
            var sw = Stopwatch.StartNew();
            await signal.WaitAsync();

            return sw.ElapsedTicks;
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadObjectInstanceFieldsSpecificsAsync(ArrayPool<char> pool)
        {
            using var completeSignal = new SemaphoreSlim(0);

            var incompleteTask = LoadAsyncStateMachinesAsync_IncompleteTaskAsync(completeSignal);

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                pool,
                dump,
                shouldMatch,
                static (analyzer, shouldMatch) => LoadObjectInstanceFieldsSpecificsCommonAsync(analyzer, shouldMatch)
            )
            .ConfigureAwait(false);

            completeSignal.Release(1);
            await incompleteTask.ConfigureAwait(false);

            GC.KeepAlive(incompleteTask);

            // common checks for all analyzers
            static async ValueTask LoadObjectInstanceFieldsSpecificsCommonAsync(IAnalyzer analyzer, (ObjectInstanceDetails Details, long TargetAddress) shouldMatch)
            {
                var actual = await analyzer.LoadObjectInstanceFieldsSpecificsAsync(shouldMatch.TargetAddress).ConfigureAwait(false);

                Assert.NotNull(actual);

                Assert.Equal(shouldMatch.Details, actual.Value);
            }

            // get expected results
            static async ValueTask<(ObjectInstanceDetails Details, long TargetAddress)> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile))
                {
                    var stateMachinesBuilder = ImmutableList.CreateBuilder<AsyncStateMachineDetails>();
                    await foreach (var machine in analyze.LoadAsyncStateMachinesAsync().ConfigureAwait(false))
                    {
                        stateMachinesBuilder.Add(machine);
                    }

                    var stateMachines = stateMachinesBuilder.ToImmutable();
                    var target = stateMachines.First(x => x.Description.Contains("<" + nameof(LoadAsyncStateMachinesAsync_IncompleteTaskAsync) + ">"));

                    long? eeClass = null;
                    long? methodTable = null;

                    var fieldsBuilder = ImmutableList.CreateBuilder<InstanceFieldWithValue>();
                    await foreach (var line in analyze.SendCommand(Command.CreateCommandWithAddress("dumpobj", target.Address)).ConfigureAwait(false))
                    {
                        var entryStr = line.ToString();
                        line.Dispose();

                        if (eeClass == null && entryStr.StartsWith("EEClass: "))
                        {
                            eeClass = long.Parse(entryStr.Substring(entryStr.LastIndexOf(' ') + 1), NumberStyles.HexNumber);
                        }
                        else if (methodTable == null && entryStr.StartsWith("MethodTable: "))
                        {
                            methodTable = long.Parse(entryStr.Substring(entryStr.LastIndexOf(' ') + 1), NumberStyles.HexNumber);
                        }
                        else
                        {
                            var match = Regex.Match(entryStr, @"^ (?<methodTable> [0-9a-f]+) \s+ (?<field> \S+) \s+ (?<offset> [0-9a-f]+) \s+ (?<type> \S+) \s+ (?<vt> \S+) \s+ (?<attr> \S+) \s+ (?<value> [0-9a-f]+) \s+ (?<name> \S+) $", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
                            if (match.Success)
                            {
                                var mt = long.Parse(match.Groups["methodTable"].Value, NumberStyles.HexNumber);
                                var attr = match.Groups["attr"].Value;
                                if (attr != "instance")
                                {
                                    continue;
                                }

                                var name = match.Groups["name"].Value;
                                var val = long.Parse(match.Groups["value"].Value, NumberStyles.HexNumber);

                                fieldsBuilder.Add(new InstanceFieldWithValue(new InstanceField(name, mt), val));
                            }
                        }
                    }
                    var fields = fieldsBuilder.ToImmutable();

                    Assert.NotNull(eeClass);
                    Assert.NotNull(methodTable);
                    Assert.NotEmpty(fields);

                    var ret = new ObjectInstanceDetails(eeClass.Value, methodTable.Value, fields);

                    return (ret, target.Address);
                }
            }
        }

        private readonly struct LoadHeapDetailsAsync_SpecialValue
        {
            internal Guid Id { get; }

            internal LoadHeapDetailsAsync_SpecialValue(Guid id)
            {
                Id = id;
            }

            public override string ToString()
            => Id.ToString();
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadHeapDetailsAsync(ArrayPool<char> pool)
        {
            // pfffft, I do not love this but it's probably the best option I've got to force allocations?
            var pinnedVal = GC.AllocateArray<LoadHeapDetailsAsync_SpecialValue>(1, pinned: true);
            pinnedVal[0] = new LoadHeapDetailsAsync_SpecialValue(Guid.NewGuid());

            // LOH takes anything over 85_000 bytes by default, this should be way more than enough
            var lohVal = new LoadHeapDetailsAsync_SpecialValue[100_000];
            lohVal[0] = new LoadHeapDetailsAsync_SpecialValue(Guid.NewGuid());

            var gen2Val = new LoadHeapDetailsAsync_SpecialValue[] { new LoadHeapDetailsAsync_SpecialValue(Guid.NewGuid()) };
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            var gen1Val = new LoadHeapDetailsAsync_SpecialValue[] { new LoadHeapDetailsAsync_SpecialValue(Guid.NewGuid()) };
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            var gen0Val = new LoadHeapDetailsAsync_SpecialValue[] { new LoadHeapDetailsAsync_SpecialValue(Guid.NewGuid()) };

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync().ConfigureAwait(false);

            var shouldMatch = await GetShouldMatchAsync(dump, pinnedVal, lohVal, gen2Val, gen1Val, gen0Val).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                pool,
                dump,
                shouldMatch,
                static (analyzer, shouldMatch) => LoadHeapDetailsCommonAsync(analyzer, shouldMatch)
            )
            .ConfigureAwait(false);

            GC.KeepAlive(pinnedVal);
            GC.KeepAlive(gen2Val);
            GC.KeepAlive(gen1Val);
            GC.KeepAlive(gen0Val);

            // common check for all analyzers
            static async ValueTask LoadHeapDetailsCommonAsync(IAnalyzer analyzer, ImmutableList<HeapDetails> shouldMatch)
            {
                var details = await analyzer.LoadHeapDetailsAsync().ConfigureAwait(false);

                Assert.Equal(shouldMatch.AsEnumerable(), details.AsEnumerable());
            }

            // expected result for each analyzer
            static async ValueTask<ImmutableList<HeapDetails>> GetShouldMatchAsync(
                SelfDumpHelper dump,
                LoadHeapDetailsAsync_SpecialValue[] pinnedVal,
                LoadHeapDetailsAsync_SpecialValue[] lohVal,
                LoadHeapDetailsAsync_SpecialValue[] gen2Val,
                LoadHeapDetailsAsync_SpecialValue[] gen1Val,
                LoadHeapDetailsAsync_SpecialValue[] gen0Val
            )
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var heapStatLinesBuilder = ImmutableList.CreateBuilder<string>();

                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("eeheap -gc")).ConfigureAwait(false))
                    {
                        heapStatLinesBuilder.Add(line.ToString());

                        line.Dispose();
                    }

                    var heapStatLines = heapStatLinesBuilder.ToImmutable();
                    var rawDetailsBuilder = ImmutableList.CreateBuilder<HeapDetails>();
                    {

                        var started = false;
                        long? gen0 = null;
                        long? gen1 = null;
                        long? gen2 = null;

                        string curSegments = null;
                        var sohSegments = ImmutableArray.CreateBuilder<HeapDetailsBuilder.HeapSegment>();
                        var lohSegments = ImmutableArray.CreateBuilder<HeapDetailsBuilder.HeapSegment>();
                        var pohSegments = ImmutableArray.CreateBuilder<HeapDetailsBuilder.HeapSegment>();
                        foreach (var line in heapStatLines)
                        {
                            if (line.StartsWith("generation 0"))
                            {
                                Assert.False(started);
                                Assert.Null(gen0);
                                var part = line.Substring(line.LastIndexOf('x') + 1);
                                gen0 = long.Parse(part, NumberStyles.HexNumber);

                                started = true;
                            }

                            if (line.StartsWith("generation 1"))
                            {
                                Assert.True(started);
                                Assert.Null(gen1);
                                var part = line.Substring(line.LastIndexOf('x') + 1);
                                gen1 = long.Parse(part, NumberStyles.HexNumber);
                            }

                            if (line.StartsWith("generation 2"))
                            {
                                Assert.True(started);
                                Assert.Null(gen2);
                                var part = line.Substring(line.LastIndexOf('x') + 1);
                                gen2 = long.Parse(part, NumberStyles.HexNumber);

                                curSegments = "SOH";
                            }

                            if (line.StartsWith("Large object heap"))
                            {
                                Assert.NotNull(curSegments);
                                curSegments = "LOH";
                            }

                            if (line.StartsWith("Pinned object heap"))
                            {
                                Assert.NotNull(curSegments);
                                curSegments = "POH";
                            }

                            var match =
                                Regex.Match(
                                    line,
                                    @"^ (?<segment> [0-9a-f]+) \s+ (?<begin> [0-9a-f]+) \s+ (?<allocated> [0-9a-f]+) \s+ (?<committed> [0-9a-f]+) \s+ 0x([0-9a-f]+) \( (?<allocatedSize> \d+) \) \s+ 0x([0-9a-f]+) \( (?<committedSize> \d+) \) $",
                                    RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
                                );

                            if (match.Success)
                            {
                                Assert.True(started);

                                var start = long.Parse(match.Groups["allocated"].Value, NumberStyles.HexNumber);
                                var size = long.Parse(match.Groups["allocatedSize"].Value);

                                switch (curSegments)
                                {
                                    case "SOH": sohSegments.Add(new HeapDetailsBuilder.HeapSegment(start, size)); break;
                                    case "LOH": lohSegments.Add(new HeapDetailsBuilder.HeapSegment(start, size)); break;
                                    case "POH": pohSegments.Add(new HeapDetailsBuilder.HeapSegment(start, size)); break;
                                    default: throw new InvalidOperationException();
                                }
                            }

                            if (started && line.All(c => c == '-'))
                            {
                                rawDetailsBuilder.Add(
                                    new HeapDetails(
                                        rawDetailsBuilder.Count,
                                        gen0.Value,
                                        gen1.Value,
                                        gen2.Value,
                                        sohSegments.ToImmutable(),
                                        lohSegments.ToImmutable(),
                                        pohSegments.ToImmutable()
                                    )
                                );

                                sohSegments.Clear();
                                lohSegments.Clear();
                                pohSegments.Clear();

                                gen0 = gen1 = gen2 = null;
                                curSegments = null;

                                started = false;
                            }
                        }

                        if (started)
                        {
                            rawDetailsBuilder.Add(
                                new HeapDetails(
                                    rawDetailsBuilder.Count,
                                    gen0.Value,
                                    gen1.Value,
                                    gen2.Value,
                                    sohSegments.ToImmutable(),
                                    lohSegments.ToImmutable(),
                                    pohSegments.ToImmutable()
                                )
                            );
                        }
                    }
                    var rawDetails = rawDetailsBuilder.ToImmutable();

                    // check that everything is classifiable
                    // this is transitive, so we only need to check it once
                    var heapEntriesBuilder = ImmutableList.CreateBuilder<HeapEntry>();
                    await foreach (var heapEntry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        heapEntriesBuilder.Add(heapEntry);
                    }
                    var heapEntries = heapEntriesBuilder.ToImmutable();

                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);

                    var specialValueType = typeDetails.Single(kv => kv.Key.TypeName == typeof(LoadHeapDetailsAsync_SpecialValue[]).FullName);
                    var specialValueTypeHeapEntries = heapEntries.Where(he => specialValueType.Value.Contains(he.MethodTable)).ToImmutableList();

                    // figure out which HeapEntry corresponds to which array
                    var pinnedBytes = pinnedVal[0].Id.ToByteArray();
                    var lohBytes = lohVal[0].Id.ToByteArray();
                    var gen2Bytes = gen2Val[0].Id.ToByteArray();
                    var gen1Bytes = gen1Val[0].Id.ToByteArray();
                    var gen0Bytes = gen0Val[0].Id.ToByteArray();

                    HeapEntry? pinnedHe;
                    HeapEntry? lohHe;
                    HeapEntry? gen2He;
                    HeapEntry? gen1He;
                    HeapEntry? gen0He;

                    pinnedHe = lohHe = gen2He = gen1He = gen0He = null;

                    foreach (var he in specialValueTypeHeapEntries)
                    {
                        var arr = await analyze.LoadArrayDetailsAsync(he).ConfigureAwait(false);

                        if (arr.FirstElementAddress == null)
                        {
                            continue;
                        }

                        // the only thing in LoadHeapDetailsAsync_SpecialValue is a guid, so this should give us that field
                        var guidLongs = await analyze.LoadLongsAsync(arr.FirstElementAddress.Value, 16 / sizeof(long)).ConfigureAwait(false);
                        var guidBytes = guidLongs.SelectMany(g => BitConverter.GetBytes(g)).ToArray();

                        if (guidBytes.SequenceEqual(lohBytes))
                        {
                            Assert.Null(lohHe);
                            lohHe = he;
                        }
                        else if (guidBytes.SequenceEqual(pinnedBytes))
                        {
                            Assert.Null(pinnedHe);
                            pinnedHe = he;
                        }
                        else if (guidBytes.SequenceEqual(gen2Bytes))
                        {
                            Assert.Null(gen2He);
                            gen2He = he;
                        }
                        else if (guidBytes.SequenceEqual(gen1Bytes))
                        {
                            Assert.Null(gen1He);
                            gen1He = he;
                        }
                        else if (guidBytes.SequenceEqual(gen0Bytes))
                        {
                            Assert.Null(gen0He);
                            gen0He = he;
                        }
                    }

                    Assert.NotNull(pinnedHe);
                    Assert.NotNull(lohHe);
                    Assert.NotNull(gen2He);
                    Assert.NotNull(gen1He);
                    Assert.NotNull(gen0He);

                    // check that heap entries correspond to expected heap segments
                    var pinnedHeap = DetermineGeneration(rawDetails, pinnedHe.Value);
                    Assert.Equal(HeapDetails.HeapClassification.PinnedObjectHeap, pinnedHeap);

                    var lohHeap = DetermineGeneration(rawDetails, lohHe.Value);
                    Assert.Equal(HeapDetails.HeapClassification.LargeObjectHeap, lohHeap);

                    var gen2Heap = DetermineGeneration(rawDetails, gen2He.Value);
                    Assert.Equal(HeapDetails.HeapClassification.Generation2, gen2Heap);

                    var gen1Heap = DetermineGeneration(rawDetails, gen1He.Value);
                    Assert.Equal(HeapDetails.HeapClassification.Generation1, gen1Heap);

                    var gen0Heap = DetermineGeneration(rawDetails, gen0He.Value);
                    Assert.Equal(HeapDetails.HeapClassification.Generation0, gen0Heap);

                    // make sure we can classify everything that's _LIVE_ on the heap
                    foreach (var he in heapEntries)
                    {
                        DetermineGeneration(rawDetails, he);
                    }

                    return rawDetails;
                }
            }

            // determine location of heap entry, w.r.t. generations and heaps
            static HeapDetails.HeapClassification DetermineGeneration(ImmutableList<HeapDetails> details, HeapEntry he)
            {
                foreach (var detail in details)
                {
                    if (detail.TryClassify(he, out var classification))
                    {
                        return classification;
                    }
                }

                throw new Exception($"Couldn't classify {he}");
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadGCHandlesAsync(ArrayPool<char> pool)
        {
            var pinned = new byte[4];
            var pinnedHandle = GCHandle.Alloc(pinned, GCHandleType.Pinned);

            var weak = new byte[4];
            var weakHandle = GCHandle.Alloc(pinned, GCHandleType.Weak);

            var weakRes = new byte[4];
            var weakResHandle = new WeakReference<byte[]>(weakRes, trackResurrection: true);

            var normal = new byte[4];
            var normalHandle = GCHandle.Alloc(pinned, GCHandleType.Normal);

            // todo: dependent handle, needs .NET 6+

            try
            {
                await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

                var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

                await RunForAllAnalyzersAsync(
                    pool,
                    dump,
                    shouldMatch,
                    static (analyzer, shouldMatch) => LoadGCHandlesCommonAsync(analyzer, shouldMatch)
                )
                .ConfigureAwait(false);
            }
            finally
            {
                pinnedHandle.Free();
                weakHandle.Free();
                normalHandle.Free();
            }

            GC.KeepAlive(pinned);
            GC.KeepAlive(weakRes);
            GC.KeepAlive(weakResHandle);
            GC.KeepAlive(weak);
            GC.KeepAlive(normal);

            // common checks for all analyzers
            static async ValueTask LoadGCHandlesCommonAsync(IAnalyzer analyzer, ImmutableList<HeapGCHandle> shouldMatch)
            {
                var handlesRaw = await analyzer.LoadGCHandlesAsync().ConfigureAwait(false);

                var handles = await LoadMethodTablesWhereNeededAsync(analyzer, handlesRaw).ConfigureAwait(false);

                // this test seems flaky... but actually catching it in the act is hard
                // so go entry by entry until I find the issue
                for (var i = 0; i < Math.Max(handles.Count, shouldMatch.Count); i++)
                {
                    Assert.Equal(shouldMatch[i], handles[i]);
                }
            }

            // load expected results
            static async ValueTask<ImmutableList<HeapGCHandle>> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var handlesManualRawBuilder = ImmutableList.CreateBuilder<HeapGCHandle>();
                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("gchandles")).ConfigureAwait(false))
                    {
                        var lineStr = line.ToString();
                        line.Dispose();

                        var match = Regex.Match(lineStr, @"^ (?<handle> [a-f0-9]+) \s+ (?<type> \S+) \s+ (?<obj> [a-f0-9]+) \s+ (?<size> \d+) \s+ ((?<refCount> \d+) \s+)? (?<name> .*) $", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var handle = long.Parse(match.Groups["handle"].Value, NumberStyles.HexNumber);
                        var typeStr = match.Groups["type"].Value;
                        var refCount = match.Groups["refCount"].Success ? long.Parse(match.Groups["refCount"].Value, NumberStyles.HexNumber) : 0;
                        var obj = long.Parse(match.Groups["obj"].Value, NumberStyles.HexNumber);
                        var size = int.Parse(match.Groups["size"].Value);
                        var name = match.Groups["name"].Value;

                        HeapGCHandle.HandleTypes handleType;
                        if (typeStr.Equals("Pinned", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.Pinned;
                        }
                        else if (typeStr.Equals("RefCounted", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.RefCounted;
                        }
                        else if (typeStr.Equals("WeakShort", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.WeakShort;
                        }
                        else if (typeStr.Equals("WeakLong", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.WeakLong;
                        }
                        else if (typeStr.Equals("Strong", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.Strong;
                        }
                        else if (typeStr.Equals("Variable", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.Variable;
                        }
                        else if (typeStr.Equals("AsyncPinned", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.AsyncPinned;
                        }
                        else if (typeStr.Equals("SizeRef", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.SizedRef;
                        }
                        else if (typeStr.Equals("Dependent", StringComparison.Ordinal))
                        {
                            handleType = HeapGCHandle.HandleTypes.Dependent;
                        }
                        else
                        {
                            throw new Exception($"Unexpected gc handle type: {typeStr}");
                        }

                        handlesManualRawBuilder.Add(new HeapGCHandle(handle, handleType, obj, name, size));
                    }
                    var handlesManualRaw = handlesManualRawBuilder.ToImmutable();

                    var handlesManual = await LoadMethodTablesWhereNeededAsync(analyze, handlesManualRaw).ConfigureAwait(false);

                    return handlesManual;
                }
            }

            // fill out the given gc handles
            static async ValueTask<ImmutableList<HeapGCHandle>> LoadMethodTablesWhereNeededAsync(IAnalyzer analyzer, ImmutableList<HeapGCHandle> loaded)
            {
                var ret = ImmutableList.CreateBuilder<HeapGCHandle>();

                foreach (var handle in loaded)
                {
                    if (handle.MethodTableInitialized)
                    {
                        ret.Add(handle);
                        continue;
                    }

                    var obj = await analyzer.LoadObjectInstanceFieldsSpecificsAsync(handle.ObjectAddress).ConfigureAwait(false);

                    var withMt = handle.SetMethodTable(obj.Value.MethodTable);
                    ret.Add(withMt);
                }

                return ret.ToImmutable();
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadHeapFragmentationAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                    pool,
                    dump,
                    shouldMatch,
                    static (analyzer, shouldMatch) => LoadHeapFragmentationCommonAsync(analyzer, shouldMatch)
                )
                .ConfigureAwait(false);

            // run common checks for all analyzers
            static async ValueTask LoadHeapFragmentationCommonAsync(IAnalyzer analyzer, HeapFragmentation shouldMatch)
            {
                var fragementation = await analyzer.LoadHeapFragmentationAsync().ConfigureAwait(false);

                Assert.Equal(shouldMatch, fragementation);
            }

            // get the expected result
            static async ValueTask<HeapFragmentation> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    // check against the manual way
                    long gen0, gen1, gen2, loh, poh;
                    gen0 = gen1 = gen2 = loh = poh = 0;

                    long freeGen0, freeGen1, freeGen2, freeLoh, freePoh;
                    freeGen0 = freeGen1 = freeGen2 = freeLoh = freePoh = 0;

                    var inFree = false;

                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("gcheapstat")).ConfigureAwait(false))
                    {
                        var str = line.ToString();
                        line.Dispose();

                        if (str.StartsWith("Free space:"))
                        {
                            inFree = true;
                            continue;
                        }

                        var match = Regex.Match(str, @"^ (?<heapName> \S+) \s+ (?<gen0> \d+) \s+ (?<gen1> \d+) \s+ (?<gen2> \d+) \s+ (?<loh> \d+) \s+ (?<poh> \d+)? .* $", RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

                        if (!match.Success)
                        {
                            continue;
                        }

                        var heapName = match.Groups["heapName"].Value;
                        var g0 = long.Parse(match.Groups["gen0"].Value);
                        var g1 = long.Parse(match.Groups["gen1"].Value);
                        var g2 = long.Parse(match.Groups["gen2"].Value);
                        var l = long.Parse(match.Groups["loh"].Value);
                        var p = match.Groups.ContainsKey("poh") && match.Groups["poh"].Success ? long.Parse(match.Groups["poh"].Value) : 0L;

                        if (heapName == "Total")
                        {
                            continue;
                        }

                        if (inFree)
                        {
                            freeGen0 += g0;
                            freeGen1 += g1;
                            freeGen2 += g2;
                            freeLoh += l;
                            freePoh += p;
                        }
                        else
                        {
                            gen0 += g0;
                            gen1 += g1;
                            gen2 += g2;
                            loh += l;
                            poh += p;
                        }
                    }

                    var rawFragmentation = new HeapFragmentation(freeGen0, gen0, freeGen1, gen1, freeGen2, gen2, freeLoh, loh, freePoh, poh);

                    return rawFragmentation;
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadUniqueMethodTablesAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                    pool,
                    dump,
                    shouldMatch,
                    static (analyzer, shouldMatch) => LoadUniqueMethodTablesCommonAsync(analyzer, shouldMatch)
                )
                .ConfigureAwait(false);

            // common bits for all analyzers
            static async ValueTask LoadUniqueMethodTablesCommonAsync(IAnalyzer analyzer, ImmutableList<long> shouldMatch)
            {
                var actualUnsorted = await analyzer.LoadUniqueMethodTablesAsync().ConfigureAwait(false);

                var actualList = actualUnsorted.ToImmutableList();
                var actual = actualList.Sort(static (a, b) => a.CompareTo(b));

                Assert.Equal(shouldMatch.AsEnumerable(), actual.AsEnumerable());
            }

            // get expected result
            static async ValueTask<ImmutableList<long>> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var rawBuilder = ImmutableHashSet.CreateBuilder<long>();

                    var skipRest = false;

                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("dumpheap")).ConfigureAwait(false))
                    {
                        var lineStr = line.ToString();
                        line.Dispose();

                        if (skipRest)
                        {
                            continue;
                        }

                        if (lineStr.Contains("Free"))
                        {
                            continue;
                        }

                        if (lineStr.Trim().StartsWith("MT "))
                        {
                            skipRest = true;
                            continue;
                        }

                        var match = Regex.Match(lineStr, @"^ ([0-9a-f]+) \s+ ([0-9a-f]+) \s+ ([0-9]+) \s* $", RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var addr = long.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
                        var mt = long.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
                        var size = int.Parse(match.Groups[3].Value);

                        rawBuilder.Add(mt);
                    }

                    var unsorted = rawBuilder.ToImmutable();

                    var asList = unsorted.ToImmutableList();

                    var ret = asList.Sort(static (a, b) => a.CompareTo(b));

                    return ret;
                }
            }
        }

        private sealed class _LoadLongsAsync
        {
            internal long[] Values;
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadLongsAsync(ArrayPool<char> pool)
        {
            const int LONGS_PER_VALUE = 0xFFFF + 10;    // need to make this multiline to properly test WinDbg option

            var random = new Random();
            var scratch = new byte[sizeof(long)];

            var valuesBuilder = ImmutableArray.CreateBuilder<long>();

            for (var i = 0; i < LONGS_PER_VALUE; i++)
            {
                random.NextBytes(scratch);
                var l = BitConverter.ToInt64(scratch);

                valuesBuilder.Add(l);
            }
            var values = valuesBuilder.ToImmutable().ToArray();

            var val = new _LoadLongsAsync { Values = values };

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump, val.Values).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                    pool,
                    dump,
                    shouldMatch,
                    static (analyzer, shouldMatch) => LoadLongsCommonAsync(analyzer, shouldMatch)
                )
                .ConfigureAwait(false);

            GC.KeepAlive(val);

            // common check for all analyzers
            static async ValueTask LoadLongsCommonAsync(IAnalyzer analyzer, (long TargetAddress, long[] ExpectedValue) shouldMatch)
            {
                var res = await analyzer.LoadLongsAsync(shouldMatch.TargetAddress, shouldMatch.ExpectedValue.Length).ConfigureAwait(false);

                Assert.Equal(shouldMatch.ExpectedValue, res.AsEnumerable());
            }

            // get expected values
            static async ValueTask<(long TargetAddress, long[] ExpectedValue)> GetShouldMatchAsync(SelfDumpHelper dump, long[] expected)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var types = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);

                    var targetType = types.Single(x => x.Key.TypeName.Contains(nameof(_LoadLongsAsync))).Value.Single();

                    var targetsBuilder = ImmutableList.CreateBuilder<HeapEntry>();
                    await foreach (var he in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (he.MethodTable == targetType)
                        {
                            targetsBuilder.Add(he);
                        }
                    }
                    var targets = targetsBuilder.ToImmutable();

                    foreach (var target in targets)
                    {
                        var details = await analyze.LoadObjectInstanceFieldsSpecificsAsync(target.Address).ConfigureAwait(false);
                        Assert.NotNull(details);

                        var valuesField = details.Value.InstanceFields.Single(x => x.InstanceField.Name == nameof(_LoadLongsAsync.Values));

                        var arrayDetails = await analyze.LoadArrayDetailsAsync(new HeapEntry(valuesField.Value, 0, 0, true)).ConfigureAwait(false);

                        if (arrayDetails.FirstElementAddress == null)
                        {
                            continue;
                        }


                        (long TargetAddress, long[] ExpectedValue)? ret = null;

                        await foreach (var line in analyze.SendCommand(Command.CreateCommandWithCountAndAddress("dq -c", expected.Length, " -w ", arrayDetails.FirstElementAddress.Value)).ConfigureAwait(false))
                        {
                            var str = line.ToString();
                            line.Dispose();

                            if (ret != null)
                            {
                                // have to fully enumerate this...
                                continue;
                            }

                            var colonIx = str.IndexOf(':');
                            Assert.NotEqual(-1, colonIx);

                            var rest = str[(colonIx + 1)..];
                            rest = rest.Trim();

                            var parts = rest.Split(' ');
                            var vals = parts.Select(p => long.Parse(p.Trim(), NumberStyles.HexNumber)).ToArray();

                            if (vals.SequenceEqual(expected))
                            {
                                ret = (arrayDetails.FirstElementAddress.Value, expected);
                            }
                        }

                        if (ret != null)
                        {
                            return ret.Value;
                        }
                    }

                    throw new Exception("Could't find target on heap, shouldn't be possible");
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadMethodTableTypeDetailsAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            await RunForAllAnalyzersAsync(
                pool,
                dump,
                shouldMatch,
                static (analyzer, shouldMatch) => LoadMethodTableTypeDetailsCommonAsync(analyzer, shouldMatch)
            )
            .ConfigureAwait(false);

            // run common checks for all analyzers
            static async ValueTask LoadMethodTableTypeDetailsCommonAsync(IAnalyzer analyzer, (ImmutableList<long> MethodTables, ImmutableList<TypeDetails> Details) shouldMatch)
            {
                var actualBuilder = ImmutableList.CreateBuilder<TypeDetails>();

                foreach (var mt in shouldMatch.MethodTables)
                {
                    var details = await analyzer.LoadMethodTableTypeDetailsAsync(mt).ConfigureAwait(false);
                    if (details == null)
                    {
                        throw new Exception("Shouldn't be possible");
                    }

                    actualBuilder.Add(details.Value);
                }

                var actual = actualBuilder.ToImmutable();

                for (var i = 0; i < Math.Max(shouldMatch.Details.Count, actual.Count); i++)
                {
                    var a = shouldMatch.Details[i];
                    var b = actual[i];

                    Assert.Equal(a, b);
                }
            }

            // get the expected result
            static async ValueTask<(ImmutableList<long> MethodTables, ImmutableList<TypeDetails> Details)> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var ret = ImmutableList.CreateBuilder<TypeDetails>();
                    var mtsInOrder = ImmutableList.CreateBuilder<long>();

                    var mts = await analyze.LoadUniqueMethodTablesAsync().ConfigureAwait(false);

                    foreach (var mt in mts)
                    {
                        var details = await analyze.LoadMethodTableTypeDetailsAsync(mt).ConfigureAwait(false);
                        if (details == null)
                        {
                            throw new Exception("Shouldn't be possible");
                        }

                        mtsInOrder.Add(mt);
                        ret.Add(details.Value);
                    }

                    return (mtsInOrder.ToImmutable(), ret.ToImmutable());
                }
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task PeakStringAsync(ArrayPool<char> pool)
        {
            var veryLongString = new string(Enumerable.Range(0, StringPeak.PeakLength * 5).Select(x => (char)('I' + x)).ToArray());

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            var shouldMatch = await GetShouldMatchAsync(dump).ConfigureAwait(false);

            Assert.Contains(shouldMatch.Strings, x => x.ActualLength > StringPeak.PeakLength && !x.ReadFullString && veryLongString.StartsWith(x.PeakedString));

            await RunForAllAnalyzersAsync(
                pool,
                dump,
                shouldMatch,
                static (analyzer, shouldMatch) => PeakStringCommonAsync(analyzer, shouldMatch)
            )
            .ConfigureAwait(false);

            GC.KeepAlive(veryLongString);

            // per-analyzer work that should produce consistent results
            static async ValueTask PeakStringCommonAsync(IAnalyzer analyze, (StringDetails Details, ImmutableList<HeapEntry> HeapEntries, ImmutableList<StringPeak> Strings) context)
            {
                var peaksBuilder = ImmutableList.CreateBuilder<StringPeak>();

                foreach (var stringRef in context.HeapEntries)
                {
                    var peaked = await analyze.PeakStringAsync(context.Details, stringRef).ConfigureAwait(false);

                    peaksBuilder.Add(peaked);
                }

                var peaks = peaksBuilder.ToImmutable();

                Assert.Equal(context.Strings.AsEnumerable(), peaks.AsEnumerable());
            }

            // load expected results
            static async ValueTask<(StringDetails Details, ImmutableList<HeapEntry> HeapEntries, ImmutableList<StringPeak> Strings)> GetShouldMatchAsync(SelfDumpHelper dump)
            {
                await using (var analyze = await DotNetDumpAnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile).ConfigureAwait(false))
                {
                    var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                    var stringType = typeDetails.Single(x => x.Key.TypeName == "System.String").Value.Single();

                    var stringRefsBuilder = ImmutableList.CreateBuilder<HeapEntry>();

                    await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                    {
                        if (stringType == entry.MethodTable)
                        {
                            stringRefsBuilder.Add(entry);
                        }
                    }

                    var stringRefs = stringRefsBuilder.ToImmutable();

                    var stringDetails = await analyze.LoadStringDetailsAsync(stringRefs.First()).ConfigureAwait(false);

                    var peaksBuilder = ImmutableList.CreateBuilder<StringPeak>();

                    foreach (var stringRef in stringRefs)
                    {
                        var peaked = await analyze.PeakStringAsync(stringDetails, stringRef).ConfigureAwait(false);

                        var knownLength = await analyze.LoadStringLengthAsync(stringDetails, stringRef).ConfigureAwait(false);
                        Assert.Equal(knownLength, peaked.ActualLength);

                        var knownValue = await analyze.LoadCharsAsync(stringRef.Address + stringDetails.FirstCharOffset, knownLength).ConfigureAwait(false);
                        Assert.StartsWith(peaked.PeakedString, knownValue, StringComparison.Ordinal);

                        Assert.Equal(peaked.PeakedString.Equals(knownValue), peaked.ReadFullString);

                        peaksBuilder.Add(peaked);
                    }

                    var peaks = peaksBuilder.ToImmutable();

                    return (stringDetails, stringRefs, peaks);
                }
            }
        }

        private static async ValueTask<ImmutableDictionary<TypeDetails, ImmutableHashSet<long>>> LoadTypeDetailsAsync(DotNetDumpAnalyzerProcess analyze)
        {
            var ret = ImmutableDictionary.CreateBuilder<TypeDetails, ImmutableHashSet<long>>();

            var mts = await analyze.LoadUniqueMethodTablesAsync().ConfigureAwait(false);

            foreach (var mt in mts)
            {
                var details = await analyze.LoadMethodTableTypeDetailsAsync(mt).ConfigureAwait(false);
                if (details == null)
                {
                    throw new Exception("Shouldn't be possible");
                }


                if (!ret.TryGetValue(details.Value, out var existing))
                {
                    ret[details.Value] = existing = ImmutableHashSet<long>.Empty;
                }

                ret[details.Value] = existing.Add(mt);
            }

            return ret.ToImmutable();
        }
    }
}
