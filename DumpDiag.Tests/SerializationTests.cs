using DumpDiag.Impl;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class SerializationTests
    {
        private sealed class DummyWriter : IBufferWriter<byte>
        {
            internal byte[] Data => bytes.ToArray();

            private readonly List<byte> bytes;

            private byte[] currentSegment;

            internal DummyWriter()
            {
                bytes = new List<byte>();
            }

            public void Advance(int count)
            {
                bytes.AddRange(currentSegment.Take(count));
                currentSegment = null;
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                currentSegment = new byte[sizeHint > 0 ? sizeHint : 32];
                return currentSegment.AsMemory();
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            => GetMemory(sizeHint).Span;
        }

        private sealed class DummyReader : IBufferReader<byte>
        {
            internal bool IsFinished => data.Length == currentStart;

            private readonly byte[] data;
            private readonly int maxRead;

            private int currentStart;

            internal DummyReader(byte[] data, int maxRead)
            {
                this.data = data;
                this.maxRead = maxRead;
                currentStart = 0;
            }

            public bool Read(ref Span<byte> readInto)
            {
                var toCopy = data.AsSpan()[currentStart..];
                var copyLen = Math.Min(Math.Min(toCopy.Length, readInto.Length), maxRead);

                toCopy[0..copyLen].CopyTo(readInto);

                readInto = readInto[0..copyLen];

                return (currentStart + copyLen) == data.Length;
            }

            public void Advance(int count)
            {
                if (count <= 0)
                {
                    throw new Exception();
                }

                currentStart += count;

                if (currentStart > data.Length)
                {
                    throw new Exception();
                }
            }
        }

        private static readonly int[] READ_STEPS = new int[] { 1, 2, 3, 4, 5, 6, 7, 8, int.MaxValue };

        [Theory]
        [InlineData((int)0)]
        [InlineData((int)-1)]
        [InlineData((int)1)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        [InlineData((int)0x7F)]
        [InlineData((int)0x80)]
        [InlineData((int)0x7F_ED)]
        [InlineData((int)0x80_12)]
        [InlineData((int)0x7F_ED_BC)]
        [InlineData((int)0x80_12_34)]
        [InlineData((int)0x7F_ED_BC_A9)]
        [InlineData(unchecked((int)0x80_12_34_56))]
        public void IntsSpecific(int val)
        {
            var wrapper = new IntWrapper(val);

            var writer = new DummyWriter();

            wrapper.Write(writer);

            var data = writer.Data;

            Assert.NotEmpty(data);
            Assert.True(data.Length <= sizeof(int) + 1);

            foreach (var step in READ_STEPS)
            {
                var reader = new DummyReader(data, step);

                var roundtrip = default(IntWrapper).Read(reader);

                Assert.Equal(val, roundtrip.Value);
                Assert.True(reader.IsFinished);
            }
        }


        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BoolSpecific(bool val)
        {
            var wrapper = new BoolWrapper(val);

            var writer = new DummyWriter();

            wrapper.Write(writer);

            var data = writer.Data;

            Assert.NotEmpty(data);
            Assert.True(1 == data.Length);

            foreach (var step in READ_STEPS)
            {
                var reader = new DummyReader(data, step);

                var roundtrip = default(BoolWrapper).Read(reader);

                Assert.Equal(val, roundtrip.Value);
                Assert.True(reader.IsFinished);
            }
        }

        [Theory]
        [InlineData((long)0)]
        [InlineData((long)-1)]
        [InlineData((long)1)]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        [InlineData((long)0x7F)]
        [InlineData((long)0x80)]
        [InlineData((long)0x7F_ED)]
        [InlineData((long)0x80_12)]
        [InlineData((long)0x7F_ED_BC)]
        [InlineData((long)0x80_12_34)]
        [InlineData((long)0x7F_ED_BC_A9)]
        [InlineData((long)0x80_12_34_56)]
        [InlineData((long)0x7F_ED_BC_A9_87)]
        [InlineData((long)0x80_12_34_56_78)]
        [InlineData((long)0x7F_ED_BC_A9_87_65)]
        [InlineData((long)0x80_12_34_56_78_9A)]
        [InlineData((long)0x7F_ED_BC_A9_87_65_43)]
        [InlineData((long)0x80_12_34_56_78_9A_BC)]
        [InlineData((long)0x7F_ED_BC_A9_87_65_43_21)]
        [InlineData(unchecked((long)0x80_12_34_56_78_9A_BC_DE))]
        public void LongsSpecific(long val)
        {
            var wrapper = new LongWrapper(val);

            var writer = new DummyWriter();

            wrapper.Write(writer);

            var data = writer.Data;

            Assert.NotEmpty(data);
            Assert.True(data.Length <= sizeof(long) + 2);

            foreach (var step in READ_STEPS)
            {
                var reader = new DummyReader(data, step);

                var roundtrip = default(LongWrapper).Read(reader);

                Assert.Equal(val, roundtrip.Value);
                Assert.True(reader.IsFinished);
            }
        }

        [Fact]
        public void IntsGeneral()
        {
            for (var i = 0; i < 32; i++)
            {
                var toWrite = (1 << i) | 1;

                var writer = new DummyWriter();

                (new IntWrapper(toWrite)).Write(writer);

                var data = writer.Data;

                Assert.NotEmpty(data);
                Assert.True(data.Length <= sizeof(int) + 1);

                foreach (var step in READ_STEPS)
                {
                    var reader = new DummyReader(data, step);

                    var roundtrip = default(IntWrapper).Read(reader);

                    Assert.Equal(toWrite, roundtrip.Value);
                    Assert.True(reader.IsFinished);
                }
            }
        }

        [Fact]
        public void LongsGeneral()
        {
            for (var i = 0; i < 64; i++)
            {
                var toWrite = (1L << i) | 1L;

                var writer = new DummyWriter();

                (new LongWrapper(toWrite)).Write(writer);

                var data = writer.Data;

                Assert.NotEmpty(data);
                Assert.True(data.Length <= sizeof(long) + 2);

                foreach (var step in READ_STEPS)
                {
                    var reader = new DummyReader(data, step);

                    var roundtrip = default(LongWrapper).Read(reader);

                    Assert.Equal(toWrite, roundtrip.Value);
                    Assert.True(reader.IsFinished);
                }
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("\0")]
        [InlineData("\r\n")]
        [InlineData("a")]
        [InlineData("ab")]
        [InlineData("abc")]
        [InlineData("abcd")]
        [InlineData("abcde")]
        [InlineData("お前はもう死んでいる")]
        public void StringsSpecific(string val)
        {
            var wrapper = new StringWrapper(val);

            var writer = new DummyWriter();

            wrapper.Write(writer);

            var data = writer.Data;

            Assert.NotEmpty(data);

            foreach (var step in READ_STEPS)
            {
                var reader = new DummyReader(data, step);

                var roundtrip = default(StringWrapper).Read(reader);

                Assert.Equal(val, roundtrip.Value);
                Assert.True(reader.IsFinished);
            }
        }

        [Fact]
        public void ListGeneric()
        {
            for (var i = 0; i < 64; i++)
            {
                var list = Enumerable.Range(0, i).ToImmutableList();
                var wrapper = new ImmutableListWrapper<IntWrapper>(list.Select(x => new IntWrapper(x)).ToImmutableList());

                var writer = new DummyWriter();

                wrapper.Write(writer);

                var data = writer.Data;

                Assert.NotEmpty(data);

                foreach (var step in READ_STEPS)
                {
                    var reader = new DummyReader(data, step);

                    var roundtrip = default(ImmutableListWrapper<IntWrapper>).Read(reader);

                    var roundtripValue = roundtrip.Value.Select(x => x.Value).ToImmutableList();

                    Assert.Equal(list, roundtripValue);
                    Assert.True(reader.IsFinished);
                }
            }
        }

        [Fact]
        public void HashSetGeneric()
        {
            for (var i = 0; i < 64; i++)
            {
                var list = Enumerable.Range(0, i).ToImmutableHashSet();
                var wrapper = new ImmutableHashSetWrapper<IntWrapper>(list.Select(x => new IntWrapper(x)).ToImmutableHashSet());

                var writer = new DummyWriter();

                wrapper.Write(writer);

                var data = writer.Data;

                Assert.NotEmpty(data);

                foreach (var step in READ_STEPS)
                {
                    var reader = new DummyReader(data, step);

                    var roundtrip = default(ImmutableHashSetWrapper<IntWrapper>).Read(reader);

                    var roundtripValue = roundtrip.Value.Select(x => x.Value).ToImmutableHashSet();

                    Assert.True(list.SetEquals(roundtripValue));
                    Assert.True(reader.IsFinished);
                }
            }
        }

        [Fact]
        public void DictionaryGeneric()
        {
            for (var i = 0; i < 64; i++)
            {
                var keys = Enumerable.Range(0, i).ToImmutableList();
                var data = Enumerable.Range(0, i).Select(x => x + "-" + x).ToImmutableList();
                var builder = ImmutableDictionary.CreateBuilder<int, string>();
                for (var y = 0; y < keys.Count; y++)
                {
                    builder.Add(keys[y], data[y]);
                }
                var original = builder.ToImmutable();

                var wrapper = new ImmutableDictionaryWrapper<IntWrapper, StringWrapper>(original.ToImmutableDictionary(kv => new IntWrapper(kv.Key), kv => new StringWrapper(kv.Value)));

                var writer = new DummyWriter();

                wrapper.Write(writer);

                var bytes = writer.Data;

                Assert.NotEmpty(bytes);

                foreach (var step in READ_STEPS)
                {
                    var reader = new DummyReader(bytes, step);

                    var roundtrip = default(ImmutableDictionaryWrapper<IntWrapper, StringWrapper>).Read(reader);

                    var roundtripValue = roundtrip.Value.ToImmutableDictionary(kv => kv.Key.Value, kv => kv.Value.Value);

                    var originalInOrder = original.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key);
                    var roundtripInOrder = roundtripValue.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key);

                    Assert.Equal(originalInOrder, roundtripInOrder);

                    Assert.True(reader.IsFinished);
                }
            }
        }

        [Fact]
        public async Task FileBackedStorage_IntegrityChecksAsync()
        {
            // version
            using (var mem = new MemoryStream())
            {
                var storage = new FileBackedDiagnosisStorage(mem);

                Assert.False(await storage.IsValidForVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false));

                await storage.IntializeWithVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false);

                Assert.True(await storage.IsValidForVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false));

                Assert.False(await storage.IsValidForVersionAsync(new Version(1, 2, 3, 5)).ConfigureAwait(false));
                Assert.False(await storage.IsValidForVersionAsync(new Version(1, 2, 4, 4)).ConfigureAwait(false));
                Assert.False(await storage.IsValidForVersionAsync(new Version(1, 3, 3, 4)).ConfigureAwait(false));
                Assert.False(await storage.IsValidForVersionAsync(new Version(2, 2, 3, 4)).ConfigureAwait(false));
            }

            // smashed header
            using (var mem = new MemoryStream())
            {
                var storage = new FileBackedDiagnosisStorage(mem);

                await storage.IntializeWithVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false);

                var goodData = mem.ToArray();

                for (var i = 0; i < FileBackedDiagnosisStorage.HEADER_SIZE; i++)
                {
                    var badData = goodData.ToArray();
                    badData[i]++;

                    using (var badMem = new MemoryStream(badData))
                    {
                        var badStorage = new FileBackedDiagnosisStorage(badMem);

                        Assert.False(await badStorage.IsValidForVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false));
                    }
                }
            }
        }

        [Fact]
        public async Task FileBackedStorage_AllTypesAsync()
        {
            var typesToSerialize =
                typeof(DumpDiagnoser)
                    .Assembly
                    .GetTypes()
                    .Where(t => t.GetInterfaces().Select(t => t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t).Any(x => x == typeof(IDiagnosisSerializable<>)))
                    .ToImmutableList();

            Assert.NotEmpty(typesToSerialize);

            using var mem = new MemoryStream();
            var storage = new FileBackedDiagnosisStorage(mem);

            await storage.IntializeWithVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false);

            var shouldMatch = ImmutableDictionary.CreateBuilder<string, object>();

            foreach (var t in typesToSerialize)
            {
                object data;
                if (t == typeof(IntWrapper))
                {
                    data = new IntWrapper(1);
                }
                else if (t == typeof(LongWrapper))
                {
                    data = new LongWrapper(2);
                }
                else if (t == typeof(AddressWrapper))
                {
                    data = new AddressWrapper(0x12345678);
                }
                else if (t == typeof(BoolWrapper))
                {
                    data = new BoolWrapper(true);
                }
                else if (t == typeof(StringWrapper))
                {
                    data = new StringWrapper("hello");
                }
                else if (t == typeof(AnalyzerStackFrame))
                {
                    data = new AnalyzerStackFrame(1234, 5678, "target");
                }
                else if (t == typeof(AsyncMachineBreakdown))
                {
                    data = new AsyncMachineBreakdown(new TypeDetails("type", 0x12345678), 14, ImmutableList.Create(new InstanceFieldWithTypeDetails(new TypeDetails("type2", 0x8888), new InstanceField("field", 0x81818))));
                }
                else if (t == typeof(AsyncStateMachineDetails))
                {
                    data = new AsyncStateMachineDetails(0x08080, 0x1818, 42, "async state machine");
                }
                else if (t == typeof(HeapDetails))
                {
                    data = new HeapDetails(1, 0x8, 0x88, 0x888, ImmutableArray.Create(new HeapDetailsBuilder.HeapSegment(0x8888, 15)), ImmutableArray.Create(new HeapDetailsBuilder.HeapSegment(0x18888, 30)), ImmutableArray.Create(new HeapDetailsBuilder.HeapSegment(0x28888, 45)));
                }
                else if (t == typeof(HeapEntry))
                {
                    data = new HeapEntry(0x12340, 0x56780, 24, true);
                }
                else if (t == typeof(HeapFragmentation))
                {
                    data = new HeapFragmentation(1234, 5678, 9012, 34567, 8901, 23456, 7890, 12345, 6789, 123456);
                }
                else if (t == typeof(InstanceField))
                {
                    data = new InstanceField("field", 0x81818);
                }
                else if (t == typeof(InstanceFieldWithTypeDetails))
                {
                    data = new InstanceFieldWithTypeDetails(new TypeDetails("type2", 0x8888), new InstanceField("field", 0x81818));
                }
                else if (t == typeof(PinAnalysis))
                {
                    data =
                        new PinAnalysis(
                            ImmutableDictionary.Create<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>>()
                                .Add(
                                    HeapDetails.HeapClassification.Generation0,
                                    ImmutableDictionary.Create<TypeDetails, (int Count, long Size)>()
                                        .Add(new TypeDetails("foo", 0x18), (1, 2L))
                                ),
                            ImmutableDictionary.Create<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>>()
                                .Add(
                                    HeapDetails.HeapClassification.Generation1,
                                    ImmutableDictionary.Create<TypeDetails, (int Count, long Size)>()
                                        .Add(new TypeDetails("bar", 0x28), (3, 4L))
                                )
                        );
                }
                else if (t == typeof(ReferenceStats))
                {
                    data = new ReferenceStats(1, 2, 3, 4);
                }
                else if (t == typeof(ImmutableListWrapper<>))
                {
                    continue;
                }
                else if (t == typeof(ImmutableDictionaryWrapper<,>))
                {
                    continue;
                }
                else if (t == typeof(ImmutableHashSetWrapper<>))
                {
                    continue;
                }
                else if (t == typeof(StringDetails))
                {
                    data = new StringDetails(0x1238, 2, 4);
                }
                else if (t == typeof(ThreadAnalysis))
                {
                    data =
                        new ThreadAnalysis(
                            ImmutableList.Create(
                                ImmutableList.Create(new AnalyzerStackFrame(1234, 5678, "target"))
                            ),
                            ImmutableDictionary.Create<string, int>().Add("foo", 123)
                        );
                }
                else if (t == typeof(TypeDetails))
                {
                    data = new TypeDetails("foo", 0x18);
                }
                else if (t == typeof(HeapDetailsBuilder.HeapSegment))
                {
                    data = new HeapDetailsBuilder.HeapSegment(0x12348, 90);
                }
                else if (t == typeof(PinAnalysis.CountSizePair))
                {
                    data = new PinAnalysis.CountSizePair(123, 456);
                }
                else
                {
                    throw new Exception($"Unexpected type: {t.FullName}");
                }

                Assert.IsType(t, data);

                var toInvoke = _FileBackedStorage_AllTypesAsync_Mtd.MakeGenericMethod(t);

                var toAwait = (Task)toInvoke.Invoke(null, new object[] { shouldMatch, storage, t.FullName, data });

                await toAwait.ConfigureAwait(false);

                foreach (var kv in shouldMatch)
                {
                    var key = kv.Key;
                    var value = kv.Value;

                    var toCheck = __FileBackedStorage_AllTypesAsync_Mtd.MakeGenericMethod(value.GetType());

                    await ((Task)(toCheck.Invoke(null, new object[] { key, value, storage }))).ConfigureAwait(false);
                }
            }
        }

        private static readonly MethodInfo __FileBackedStorage_AllTypesAsync_Mtd = typeof(SerializationTests).GetMethod(nameof(__FileBackedStorage_AllTypesAsync), BindingFlags.NonPublic | BindingFlags.Static);
        private static async Task __FileBackedStorage_AllTypesAsync<T>(string key, T expectedValue, FileBackedDiagnosisStorage storage)
            where T : struct, IDiagnosisSerializable<T>
        {
            var onDisk = await storage.LoadDataAsync<T>(key).ConfigureAwait(false);
            Assert.True(onDisk.HasData);

            Assert.Equal(expectedValue, onDisk.Data);
        }

        private static readonly MethodInfo _FileBackedStorage_AllTypesAsync_Mtd = typeof(SerializationTests).GetMethod(nameof(_FileBackedStorage_AllTypesAsync), BindingFlags.NonPublic | BindingFlags.Static);
        private static async Task _FileBackedStorage_AllTypesAsync<T>(ImmutableDictionary<string, object>.Builder shouldMatch, FileBackedDiagnosisStorage storage, string name, object o)
            where T : struct, IDiagnosisSerializable<T>
        {
            shouldMatch.Add(name, o);

            var unboxed = (T)o;

            await storage.StoreDataAsync(name, unboxed).ConfigureAwait(false);
        }

        private sealed class LoggingFailableStream : Stream
        {
            private readonly int? failAfter;
            private readonly Stream inner;

            internal int WriteCalls { get; private set; }

            public override bool CanRead => inner.CanRead;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => inner.CanWrite;

            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => inner.Position = value;
            }

            internal LoggingFailableStream(Stream inner, int? failAfter)
            {
                this.failAfter = failAfter;
                this.inner = inner;
            }

            public override void Flush()
            => inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin)
            => inner.Seek(offset, origin);

            public override void SetLength(long value)
            => inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (failAfter != null && WriteCalls >= failAfter.Value)
                {
                    throw new IOException("No no no");
                }

                WriteCalls++;
                inner.Write(buffer, offset, count);
            }
        }

        [Fact]
        public async Task FileBackedDiagnosisStorage_FailuresAsync()
        {
            var allData = GetToWriteData();

            // figure out how many write calls are needed
            int maximumWriteCalls;
            using (var writer = new LoggingFailableStream(new MemoryStream(), null))
            {
                var storage = new FileBackedDiagnosisStorage(writer);

                foreach (var data in allData)
                {
                    var toInvoke = _FileBackedStorage_AllTypesAsync_Mtd.MakeGenericMethod(data.GetType());

                    var addTask = (Task)toInvoke.Invoke(null, new object[] { ImmutableDictionary.CreateBuilder<string, object>(), storage, data.GetType().FullName, data });

                    await addTask.ConfigureAwait(false);
                }

                maximumWriteCalls = writer.WriteCalls;
            }

            // now fail each write call 
            var allTasks =
                Enumerable.Range(0, maximumWriteCalls)
                    .Select(
                        async failAfter =>
                        {
                            var tempFile = Path.GetTempFileName();
                            try
                            {
                                var safelyWritten = ImmutableDictionary.Create<string, object>();

                                using (var mem = File.Create(tempFile))
                                using (var writer = new LoggingFailableStream(mem, failAfter))
                                {
                                    var storage = new FileBackedDiagnosisStorage(writer);

                                    // write data until we fail
                                    try
                                    {
                                        await storage.IntializeWithVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false);

                                        foreach (var data in allData)
                                        {
                                            var key = data.GetType().FullName;

                                            var toInvoke = _FileBackedStorage_AllTypesAsync_Mtd.MakeGenericMethod(data.GetType());

                                            var addTask = (Task)toInvoke.Invoke(null, new object[] { ImmutableDictionary.CreateBuilder<string, object>(), storage, key, data });

                                            await addTask.ConfigureAwait(false);

                                            safelyWritten = safelyWritten.Add(key, data);

                                        }
                                    }
                                    catch (IOException)
                                    {
                                        // fine!
                                    }
                                }

                                // now check that everything we said was persisted _is_ persisted.
                                using (var file = File.Open(tempFile, FileMode.Open))
                                {
                                    var safeStorage = new FileBackedDiagnosisStorage(file);

                                    await safeStorage.IntializeWithVersionAsync(new Version(1, 2, 3, 4)).ConfigureAwait(false);

                                    foreach (var kv in safelyWritten)
                                    {
                                        var key = kv.Key;
                                        var expected = kv.Value;

                                        var toInvoke = __FileBackedStorage_AllTypesAsync_Mtd.MakeGenericMethod(expected.GetType());

                                        var checkTask = (Task)toInvoke.Invoke(null, new object[] { key, expected, safeStorage });

                                        await checkTask.ConfigureAwait(false);
                                    }
                                }
                            }
                            finally
                            {
                                try
                                {
                                    File.Delete(tempFile);
                                }
                                catch
                                {
                                    // best effort
                                }
                            }
                        }
                    )
                    .ToArray();

            await Task.WhenAll(allTasks).ConfigureAwait(false);

            static ImmutableList<object> GetToWriteData()
            {
                var typesToSerialize =
               typeof(DumpDiagnoser)
                   .Assembly
                   .GetTypes()
                   .Where(t => t.GetInterfaces().Select(t => t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t).Any(x => x == typeof(IDiagnosisSerializable<>)))
                   .ToImmutableList();

                Assert.NotEmpty(typesToSerialize);

                var toWriteBuilder = ImmutableList.CreateBuilder<object>();

                foreach (var t in typesToSerialize)
                {
                    object data;
                    if (t == typeof(IntWrapper))
                    {
                        data = new IntWrapper(1);
                    }
                    else if (t == typeof(LongWrapper))
                    {
                        data = new LongWrapper(2);
                    }
                    else if (t == typeof(AddressWrapper))
                    {
                        data = new AddressWrapper(0x12345678);
                    }
                    else if (t == typeof(BoolWrapper))
                    {
                        data = new BoolWrapper(true);
                    }
                    else if (t == typeof(StringWrapper))
                    {
                        data = new StringWrapper("hello");
                    }
                    else if (t == typeof(AnalyzerStackFrame))
                    {
                        data = new AnalyzerStackFrame(1234, 5678, "target");
                    }
                    else if (t == typeof(AsyncMachineBreakdown))
                    {
                        data = new AsyncMachineBreakdown(new TypeDetails("type", 0x12345678), 14, ImmutableList.Create(new InstanceFieldWithTypeDetails(new TypeDetails("type2", 0x8888), new InstanceField("field", 0x81818))));
                    }
                    else if (t == typeof(AsyncStateMachineDetails))
                    {
                        data = new AsyncStateMachineDetails(0x08080, 0x1818, 42, "async state machine");
                    }
                    else if (t == typeof(HeapDetails))
                    {
                        data = new HeapDetails(1, 0x8, 0x88, 0x888, ImmutableArray.Create(new HeapDetailsBuilder.HeapSegment(0x8888, 15)), ImmutableArray.Create(new HeapDetailsBuilder.HeapSegment(0x18888, 30)), ImmutableArray.Create(new HeapDetailsBuilder.HeapSegment(0x28888, 45)));
                    }
                    else if (t == typeof(HeapEntry))
                    {
                        data = new HeapEntry(0x12340, 0x56780, 24, true);
                    }
                    else if (t == typeof(HeapFragmentation))
                    {
                        data = new HeapFragmentation(1234, 5678, 9012, 34567, 8901, 23456, 7890, 12345, 6789, 123456);
                    }
                    else if (t == typeof(InstanceField))
                    {
                        data = new InstanceField("field", 0x81818);
                    }
                    else if (t == typeof(InstanceFieldWithTypeDetails))
                    {
                        data = new InstanceFieldWithTypeDetails(new TypeDetails("type2", 0x8888), new InstanceField("field", 0x81818));
                    }
                    else if (t == typeof(PinAnalysis))
                    {
                        data =
                            new PinAnalysis(
                                ImmutableDictionary.Create<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>>()
                                    .Add(
                                        HeapDetails.HeapClassification.Generation0,
                                        ImmutableDictionary.Create<TypeDetails, (int Count, long Size)>()
                                            .Add(new TypeDetails("foo", 0x18), (1, 2L))
                                    ),
                                ImmutableDictionary.Create<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>>()
                                    .Add(
                                        HeapDetails.HeapClassification.Generation1,
                                        ImmutableDictionary.Create<TypeDetails, (int Count, long Size)>()
                                            .Add(new TypeDetails("bar", 0x28), (3, 4L))
                                    )
                            );
                    }
                    else if (t == typeof(ReferenceStats))
                    {
                        data = new ReferenceStats(1, 2, 3, 4);
                    }
                    else if (t == typeof(ImmutableListWrapper<>))
                    {
                        continue;
                    }
                    else if (t == typeof(ImmutableDictionaryWrapper<,>))
                    {
                        continue;
                    }
                    else if (t == typeof(ImmutableHashSetWrapper<>))
                    {
                        continue;
                    }
                    else if (t == typeof(StringDetails))
                    {
                        data = new StringDetails(0x1238, 2, 4);
                    }
                    else if (t == typeof(ThreadAnalysis))
                    {
                        data =
                            new ThreadAnalysis(
                                ImmutableList.Create(
                                    ImmutableList.Create(new AnalyzerStackFrame(1234, 5678, "target"))
                                ),
                                ImmutableDictionary.Create<string, int>().Add("foo", 123)
                            );
                    }
                    else if (t == typeof(TypeDetails))
                    {
                        data = new TypeDetails("foo", 0x18);
                    }
                    else if (t == typeof(HeapDetailsBuilder.HeapSegment))
                    {
                        data = new HeapDetailsBuilder.HeapSegment(0x12348, 90);
                    }
                    else if (t == typeof(PinAnalysis.CountSizePair))
                    {
                        data = new PinAnalysis.CountSizePair(123, 456);
                    }
                    else
                    {
                        throw new Exception($"Unexpected type: {t.FullName}");
                    }

                    Assert.IsType(t, data);

                    toWriteBuilder.Add(data);
                }

                return toWriteBuilder.ToImmutable();
            }
        }
    }
}
