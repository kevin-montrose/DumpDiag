using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    internal delegate void WellKnownDelegate(string x, int y);

    [CollectionDefinition(nameof(MustRunInIsolationTestCollection), DisableParallelization = true)]
    public class MustRunInIsolationTestCollection { }

    // because we care a lot about the state of _this specific process_ we only want to run code in this class
    // any other tests shouldn't run at the same time, so we can muck about with process state without worrying about
    // conflicts
    [Collection(nameof(MustRunInIsolationTestCollection))]
    public class AnalyzerProcessTests : IDisposable
    {
        // todo: de-dupe
        private static ArrayPool<char>[] CharArrayPools = new[] { ArrayPool<char>.Shared, new PoisonedArrayPool<char>(), new LeakTrackingArrayPool<char>(ArrayPool<char>.Shared), new LeakTrackingArrayPool<char>(new PoisonedArrayPool<char>()) };

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

        public void Dispose()
        {
            // after each test, check that we haven't leaked anything
            foreach (var pool in CharArrayPools.OfType<LeakTrackingArrayPool<char>>())
            {
                pool.AssertEmpty();
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task DumpHeapLiveAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var res = new List<OwnedSequence<char>>();

                var shouldMatch = new List<HeapEntry>();

                try
                {
                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("dumpheap -live")).ConfigureAwait(false))
                    {
                        res.Add(line);
                    }

                    foreach (var l in res)
                    {
                        var str = l.ToString();
                        if (str.Contains("Free"))
                        {
                            continue;
                        }

                        if (str.Trim().StartsWith("MT "))
                        {
                            break;
                        }

                        var match = Regex.Match(str, @"^ ([0-9a-f]+) \s+ ([0-9a-f]+) \s+ ([0-9]+) \s* $", RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var addr = long.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
                        var mt = long.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
                        var size = int.Parse(match.Groups[3].Value);

                        shouldMatch.Add(new HeapEntry(addr, mt, size, true));
                    }
                }
                finally
                {
                    foreach (var toFree in res)
                    {
                        toFree.Dispose();
                    }
                }

                var ix = 0;
                var actual = new List<HeapEntry>();
                await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                {
                    actual.Add(entry);

                    // fail a little faster by doubling up some checks
                    Assert.True(ix < shouldMatch.Count);

                    var shouldMatchEntry = shouldMatch[ix];
                    Assert.Equal(shouldMatchEntry, entry);
                    ix++;
                }

                Assert.Equal(shouldMatch.Count, actual.Count);
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task DumpHeapDeadAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var res = new List<OwnedSequence<char>>();

                var shouldMatch = new List<HeapEntry>();

                try
                {
                    await foreach (var line in analyze.SendCommand(Command.CreateCommand("dumpheap -dead")).ConfigureAwait(false))
                    {
                        res.Add(line);
                    }

                    foreach (var l in res)
                    {
                        var str = l.ToString();
                        if (str.Contains("Free"))
                        {
                            continue;
                        }

                        if (str.Trim().StartsWith("MT "))
                        {
                            break;
                        }

                        var match = Regex.Match(str, @"^ ([0-9a-f]+) \s+ ([0-9a-f]+) \s+ ([0-9]+) \s* $", RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase);
                        if (!match.Success)
                        {
                            continue;
                        }

                        var addr = long.Parse(match.Groups[1].Value, NumberStyles.HexNumber);
                        var mt = long.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
                        var size = int.Parse(match.Groups[3].Value);

                        shouldMatch.Add(new HeapEntry(addr, mt, size, false));
                    }
                }
                finally
                {
                    foreach (var toFree in res)
                    {
                        toFree.Dispose();
                    }
                }

                var ix = 0;
                var actual = new List<HeapEntry>();
                await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Dead).ConfigureAwait(false))
                {
                    actual.Add(entry);

                    // fail a little faster by doubling up some checks
                    Assert.True(ix < shouldMatch.Count);

                    var shouldMatchEntry = shouldMatch[ix];
                    Assert.Equal(shouldMatchEntry, entry);
                    ix++;
                }

                Assert.Equal(shouldMatch.Count, actual.Count);
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadStringTypeDetailsAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
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

                var stringDetails = await analyze.LoadStringDetailsAsync(stringRef.Value).ConfigureAwait(false);

                // this has been constant since like... 2003?  Just hard code the check.
                Assert.Equal(8, stringDetails.LengthOffset);
                Assert.Equal(12, stringDetails.FirstCharOffset);
            }
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task LoadThreadStackFramesAsync(ArrayPool<char> pool)
        {
            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var expectedThreadCount = 0;
                await foreach (var line in analyze.SendCommand(Command.CreateCommand("threads")).ConfigureAwait(false))
                {
                    line.Dispose();
                    expectedThreadCount++;
                }

                var actualThreadCount = await analyze.CountActiveThreadsAsync().ConfigureAwait(false);

                Assert.Equal(expectedThreadCount, actualThreadCount);

                for (var i = 0; i < expectedThreadCount; i++)
                {
                    // switch to thread
                    await foreach (var line in analyze.SendCommand(Command.CreateCommandWithCount("threads", i)).ConfigureAwait(false))
                    {
                        line.Dispose();
                    }

                    var expectedStackFrame = new List<AnalyzerStackFrame>();
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

                        expectedStackFrame.Add(new AnalyzerStackFrame(sp, ip, cs));
                    }

                    var actualStackFrame = new List<AnalyzerStackFrame>();
                    foreach (var frame in await analyze.GetStackTraceForThreadAsync(i).ConfigureAwait(false))
                    {
                        actualStackFrame.Add(frame);
                    }

                    Assert.Equal(expectedStackFrame, actualStackFrame);
                }
            }
        }

        [Fact]
        public async Task ReadStringAsync() // this is pretty expensive, so don't bother with all the different options
        {
            var uniqueString = Guid.NewGuid().ToString();

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var analyze = await AnalyzerProcess.CreateAsync(ArrayPool<char>.Shared, dump.DotNetDumpPath, dump.DumpFile))
            {
                var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                var stringType = typeDetails.Single(x => x.Key.TypeName == "System.String").Value.Single();

                var stringRefs = new List<HeapEntry>();

                await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                {
                    if (stringType == entry.MethodTable)
                    {
                        stringRefs.Add(entry);
                    }
                }

                var stringDetails = await analyze.LoadStringDetailsAsync(stringRefs.First()).ConfigureAwait(false);

                var strings = new List<string>();
                foreach (var stringEntry in stringRefs)
                {
                    var stringLen = await analyze.GetStringLengthAsync(stringDetails, stringEntry).ConfigureAwait(false);

                    string val = await analyze.ReadCharsAsync(stringEntry.Address + stringDetails.FirstCharOffset, stringLen).ConfigureAwait(false);

                    strings.Add(val);
                }

                Assert.Contains(uniqueString, strings);
            }

            GC.KeepAlive(uniqueString);
        }

        [Theory]
        [MemberData(nameof(ArrayPoolParameters))]
        public async Task ReadDelegateDetailsAsync(ArrayPool<char> pool)
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

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                var wellKnownDelegateType = typeDetails.Single(x => x.Key.TypeName == typeof(WellKnownDelegate).FullName).Value.Single();

                var instances = new List<HeapEntry>();

                await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                {
                    if (wellKnownDelegateType == entry.MethodTable)
                    {
                        instances.Add(entry);
                    }
                }

                var delegateDetails = new List<DelegateDetails>();
                foreach (var he in instances)
                {
                    var del = await analyze.ReadDelegateDetailsAsync(he).ConfigureAwait(false);
                    delegateDetails.Add(del);
                }

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

            GC.KeepAlive(instanceDelegate);
            GC.KeepAlive(staticDelegate);
            GC.KeepAlive(chainedDelegate);
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

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
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

                var delTypePath = await ConcatAsync(ReadClassHiearchyAsync(analyze, delEntry.Value.MethodTable)).ConfigureAwait(false);
                var barTypePath = await ConcatAsync(ReadClassHiearchyAsync(analyze, barEntry.Value.MethodTable)).ConfigureAwait(false);
                var objTypePath = await ConcatAsync(ReadClassHiearchyAsync(analyze, objEntry.Value.MethodTable)).ConfigureAwait(false);

                Assert.Equal("DumpDiag.Tests.WellKnownDelegate -> System.MulticastDelegate -> System.Delegate -> System.Object", delTypePath);
                Assert.Equal("DumpDiag.Tests.AnalyzerProcessTests+Bar -> DumpDiag.Tests.AnalyzerProcessTests+Foo -> System.Object", barTypePath);
                Assert.Equal("System.Object", objTypePath);
            }

            GC.KeepAlive(instanceDelegate);
            GC.KeepAlive(knownTypeInst);
            GC.KeepAlive(objInst);

            static async IAsyncEnumerable<string> ReadClassHiearchyAsync(AnalyzerProcess analyze, long mt)
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

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var typeDetails = await LoadTypeDetailsAsync(analyze).ConfigureAwait(false);
                var charArrayType = typeDetails.Single(x => x.Key.TypeName == "System.Char[]").Value.Single();

                var charArrayHeapEntries = new List<HeapEntry>();
                await foreach (var entry in analyze.LoadHeapAsync(LoadHeapMode.Live).ConfigureAwait(false))
                {
                    if (charArrayType == entry.MethodTable)
                    {
                        charArrayHeapEntries.Add(entry);
                    }
                }

                var charArrs = new List<string>();
                foreach (var entry in charArrayHeapEntries)
                {
                    var details = await analyze.ReadArrayDetailsAsync(entry).ConfigureAwait(false);
                    var value = details.Length == 0 ? "" : await analyze.ReadCharsAsync(details.FirstElementAddress.Value, details.Length).ConfigureAwait(false);
                    charArrs.Add(value);
                }


                Assert.Contains(new string(charArr), charArrs);
            }

            GC.KeepAlive(charArr);
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

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var shouldMatch = new List<AsyncStateMachineDetails>();

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

                        shouldMatch.Add(new AsyncStateMachineDetails(addr, mt, size, desc));
                    }
                }

                var actual = new List<AsyncStateMachineDetails>();
                await foreach (var entry in analyze.LoadAsyncStateMachinesAsync().ConfigureAwait(false))
                {
                    actual.Add(entry);
                }

                Assert.Equal(shouldMatch, actual);

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

            completeSignal.Release(3);

            await incompleteTask.ConfigureAwait(false);
            await incompleteValueTask.ConfigureAwait(false);
            await incompleteTaskRun.ConfigureAwait(false);

            GC.KeepAlive(incompleteTask);
            GC.KeepAlive(incompleteTaskRun);
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

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
            {
                var stateMachinesBuilder = ImmutableList.CreateBuilder<AsyncStateMachineDetails>();
                await foreach (var machine in analyze.LoadAsyncStateMachinesAsync().ConfigureAwait(false))
                {
                    stateMachinesBuilder.Add(machine);
                }

                var stateMachines = stateMachinesBuilder.ToImmutable();
                var target = stateMachines.First(x => x.Description.Contains("<" + nameof(LoadAsyncStateMachinesAsync_IncompleteTaskAsync) + ">"));

                long? eeClass = null;

                var fieldsBuilder = ImmutableList.CreateBuilder<InstanceFieldWithValue>();
                await foreach (var line in analyze.SendCommand(Command.CreateCommandWithAddress("dumpobj", target.Address)).ConfigureAwait(false))
                {
                    var entryStr = line.ToString();
                    line.Dispose();

                    if (eeClass == null && entryStr.StartsWith("EEClass: "))
                    {
                        eeClass = long.Parse(entryStr.Substring(entryStr.LastIndexOf(' ') + 1), NumberStyles.HexNumber);
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
                Assert.NotEmpty(fields);

                var actual = await analyze.LoadObjectInstanceFieldsSpecificsAsync(target.Address).ConfigureAwait(false);

                Assert.NotNull(actual);

                Assert.Equal(eeClass.Value, actual.Value.EEClass);

                Assert.Equal(fields.Count, actual.Value.InstanceFields.Count);

                foreach (var actualField in actual.Value.InstanceFields)
                {
                    Assert.Contains(actualField, fields);
                }
            }

            completeSignal.Release(1);
            await incompleteTask.ConfigureAwait(false);

            GC.KeepAlive(incompleteTask);
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

            await using var dump = await SelfDumpHelper.TakeSelfDumpAsync();

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
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

                var details = await analyze.LoadHeapDetailsAsync().ConfigureAwait(false);

                Assert.Equal(rawDetails, details);

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
                    var arr = await analyze.ReadArrayDetailsAsync(he).ConfigureAwait(false);

                    if (arr.FirstElementAddress == null)
                    {
                        continue;
                    }

                    // the only thing in LoadHeapDetailsAsync_SpecialValue is a guid, so this should give us that field
                    var guidLongs = await analyze.ReadLongsAsync(arr.FirstElementAddress.Value, 16 / sizeof(long)).ConfigureAwait(false);
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
                var pinnedHeap = DetermineGeneration(details, pinnedHe.Value);
                Assert.Equal(HeapDetails.HeapClassification.PinnedObjectHeap, pinnedHeap);

                var lohHeap = DetermineGeneration(details, lohHe.Value);
                Assert.Equal(HeapDetails.HeapClassification.LargeObjectHeap, lohHeap);

                var gen2Heap = DetermineGeneration(details, gen2He.Value);
                Assert.Equal(HeapDetails.HeapClassification.Generation2, gen2Heap);

                var gen1Heap = DetermineGeneration(details, gen1He.Value);
                Assert.Equal(HeapDetails.HeapClassification.Generation1, gen1Heap);

                var gen0Heap = DetermineGeneration(details, gen0He.Value);
                Assert.Equal(HeapDetails.HeapClassification.Generation0, gen0Heap);

                // make sure we can classify everything that's _LIVE_ on the heap
                foreach (var he in heapEntries)
                {
                    DetermineGeneration(details, he);
                }
            }

            GC.KeepAlive(pinnedVal);
            GC.KeepAlive(gen2Val);
            GC.KeepAlive(gen1Val);
            GC.KeepAlive(gen0Val);

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

                await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
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

                    var handlesRaw = await analyze.LoadGCHandlesAsync().ConfigureAwait(false);

                    var handlesManual = await LoadMethodTablesWhereNeededAsync(analyze, handlesManualRaw).ConfigureAwait(false);
                    var handles = await LoadMethodTablesWhereNeededAsync(analyze, handlesRaw).ConfigureAwait(false);

                    Assert.Equal(handlesManual, handles);
                }
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

            static async ValueTask<ImmutableList<HeapGCHandle>> LoadMethodTablesWhereNeededAsync(AnalyzerProcess proc, ImmutableList<HeapGCHandle> loaded)
            {
                var ret = ImmutableList.CreateBuilder<HeapGCHandle>();

                foreach (var handle in loaded)
                {
                    if (handle.MethodTableInitialized)
                    {
                        ret.Add(handle);
                        continue;
                    }

                    var obj = await proc.LoadObjectInstanceFieldsSpecificsAsync(handle.ObjectAddress).ConfigureAwait(false);

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

            await using (var analyze = await AnalyzerProcess.CreateAsync(pool, dump.DotNetDumpPath, dump.DumpFile))
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

                var fragementation = await analyze.LoadHeapFragmentationAsync().ConfigureAwait(false);

                Assert.Equal(rawFragmentation, fragementation);
            }
        }

        private static async ValueTask<ImmutableDictionary<TypeDetails, ImmutableHashSet<long>>> LoadTypeDetailsAsync(AnalyzerProcess analyze)
        {
            var ret = ImmutableDictionary.CreateBuilder<TypeDetails, ImmutableHashSet<long>>();

            var mts = await analyze.LoadUniqueMethodTablesAsync().ConfigureAwait(false);

            foreach (var mt in mts)
            {
                var details = await analyze.ReadMethodTableTypeDetailsAsync(mt).ConfigureAwait(false);
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
