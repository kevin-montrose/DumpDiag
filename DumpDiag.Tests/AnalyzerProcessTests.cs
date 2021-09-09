using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    internal delegate void WellKnownDelegate(string x, int y);

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
                var stringType = typeDetails["System.String"].Single();
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
                var stringType = typeDetails["System.String"].Single();

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
                var wellKnownDelegateType = typeDetails[typeof(WellKnownDelegate).FullName].Single();

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
                var wellKnownDelegateType = typeDetails[typeof(WellKnownDelegate).FullName].Single();
                var barType = typeDetails[typeof(Bar).FullName].Single();
                var objType = typeDetails[typeof(object).FullName].Single();

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
                var charArrayType = typeDetails["System.Char[]"].Single();

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

        private static async ValueTask<ImmutableDictionary<string, ImmutableHashSet<long>>> LoadTypeDetailsAsync(AnalyzerProcess analyze)
        {
            var ret = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<long>>();

            var mts = await analyze.LoadUniqueMethodTablesAsync().ConfigureAwait(false);

            foreach (var mt in mts)
            {
                var name = await analyze.ReadMethodTableTypeNameAsync(mt).ConfigureAwait(false);

                if(!ret.TryGetValue(name, out var existing))
                {
                    ret[name] = existing = ImmutableHashSet<long>.Empty;
                }

                ret[name] = existing.Add(mt);
            }

            return ret.ToImmutable();
        }
    }
}
