using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    /// <summary>
    /// File is laid out like so
    /// 
    /// Header (56 bytes)
    ///   - DumpDiag - 16 bytes
    ///   - Magic Guid - 16 bytes
    ///   - Version (major, minor, build, revision) - 16 bytes
    ///   - Offset to current index (8 bytes)
    /// 
    /// Data
    /// 
    /// Index is a WrappedImmutableDictionary(string, long)
    /// 
    /// todo: eh, this has a failure mode where we die in the middle of writing to Offset in header
    ///       would be better to reserve space for a "next" pointer and then read/write that instead
    /// </summary>
    internal sealed class FileBackedDiagnosisStorage : IResumableDiagnosisStorage
    {
        private sealed class Writer : IBufferWriter<byte>
        {
            private readonly Stream writeTo;

            private byte[] currentBuffer;

            internal Writer(Stream writeTo)
            {
                this.writeTo = writeTo;
                currentBuffer = new byte[4 * 1024];
            }

            public void Advance(int count)
            {
                var toWrite = currentBuffer.AsSpan()[0..count];
                writeTo.Write(toWrite);
            }

            public Memory<byte> GetMemory(int sizeHint = 0)
            {
                if (currentBuffer.Length >= sizeHint)
                {
                    return currentBuffer;
                }

                currentBuffer = new byte[sizeHint];

                return currentBuffer;
            }

            public Span<byte> GetSpan(int sizeHint = 0)
            => GetMemory(sizeHint).Span;
        }

        private sealed class Reader : IBufferReader<byte>
        {
            private readonly Stream readFrom;

            private int startReadingFrom;
            private int stopReadingFrom;
            private byte[] currentBuffer;

            internal Reader(Stream readFrom)
            {
                this.readFrom = readFrom;

                currentBuffer = new byte[4 * 1024];
                startReadingFrom = stopReadingFrom = 0;
            }

            public bool Read(ref Span<byte> into)
            {
                if (stopReadingFrom == startReadingFrom)
                {
                    startReadingFrom = 0;
                    stopReadingFrom = readFrom.Read(currentBuffer);

                    if (stopReadingFrom == 0)
                    {
                        return true;
                    }
                }

                var canCopy = currentBuffer.AsSpan()[startReadingFrom..stopReadingFrom];

                var copyLength = Math.Min(canCopy.Length, into.Length);
                canCopy[0..copyLength].CopyTo(into);

                into = into[0..copyLength];

                return false;
            }

            public void Advance(int count)
            {
                Debug.Assert(count > 0);

                startReadingFrom += count;
            }

            internal void Reset()
            {
                startReadingFrom = stopReadingFrom = 0;
            }
        }

        // internal for testing
        internal const int HEADER_SIZE = 16 + 16 + 16 + 8;
        private const int HEADER_DUMP_DIAG_OFFSET = 0;
        private const int HEADER_MAGIC_GUID_OFFSET = HEADER_DUMP_DIAG_OFFSET + 16;
        private const int HEADER_VERSION_OFFSET = HEADER_MAGIC_GUID_OFFSET + 16;
        private const int HEADER_INDEX_OFFSET_OFFSET = HEADER_VERSION_OFFSET + 16;

        private static readonly Guid MAGIC_GUID = Guid.Parse("1F301361-765D-4087-B5EC-1C651D95D295");

        private readonly Stream stream;
        private readonly SemaphoreSlim exclusionLock;
        private readonly Writer writer;
        private readonly Reader reader;

        private ImmutableDictionaryWrapper<StringWrapper, LongWrapper> offsets;

        // internal for testing purposes
        internal FileBackedDiagnosisStorage(Stream stream)
        {
            this.stream = stream;
            exclusionLock = new SemaphoreSlim(1, 1);
            writer = new Writer(stream);
            reader = new Reader(stream);

            offsets = new ImmutableDictionaryWrapper<StringWrapper, LongWrapper>(ImmutableDictionary<StringWrapper, LongWrapper>.Empty);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                stream.Close();
            }
            catch
            {
                // best effort, all the flushing we've done elsewhere is what keeps this safe
            }

            exclusionLock.Dispose();

            return default;
        }

        public async ValueTask IntializeWithVersionAsync(Version thisVersion)
        {
            await exclusionLock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (!(await IsValidForVersionAsync(thisVersion).ConfigureAwait(false)))
                {
                    // truncate the file
                    stream.SetLength(0);

                    // slap the header in there
                    await WriteHeaderAsync(thisVersion).ConfigureAwait(false);

                    // append an empty index
                    var indexOffset = await WriteIndexAsync(offsets).ConfigureAwait(false);

                    // sync all the data before we update the index reference
                    await stream.FlushAsync().ConfigureAwait(false);

                    await UpdateIndexOffsetAsync(indexOffset).ConfigureAwait(false);
                }
                else
                {
                    offsets = await ReadLatestIndexAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                exclusionLock.Release();
            }
        }

        private ValueTask<T> ReadDataAsync<T>(long offset)
            where T : struct, IDiagnosisSerializable<T>
        {
            if (offset < HEADER_SIZE || offset > stream.Length)
            {
                throw new InvalidOperationException("Attempt to read out of bounds, suggests something is horribly broken");
            }

            stream.Seek(offset, SeekOrigin.Begin);

            var ret = default(T).Read(reader);

            reader.Reset();

            return new ValueTask<T>(ret);
        }

        private async ValueTask<ImmutableDictionaryWrapper<StringWrapper, LongWrapper>> ReadLatestIndexAsync()
        {
            var indexOffsetBytes = new byte[sizeof(long)].AsMemory();

            // move back to the header, specifically where the offset is
            stream.Seek(HEADER_INDEX_OFFSET_OFFSET, SeekOrigin.Begin);

            var remaining = indexOffsetBytes;

            while (remaining.Length > 0)
            {
                var read = await stream.ReadAsync(remaining).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("Could not read index offset, suggests something is horribly broken");
                }

                remaining = remaining.Slice(read);
            }

            var indexOffset =
                ((long)indexOffsetBytes.Span[0] << 56) | ((long)indexOffsetBytes.Span[1] << 48) | ((long)indexOffsetBytes.Span[2] << 40) | ((long)indexOffsetBytes.Span[3] << 32) |
                ((long)indexOffsetBytes.Span[4] << 24) | ((long)indexOffsetBytes.Span[5] << 16) | ((long)indexOffsetBytes.Span[6] << 8) | (long)indexOffsetBytes.Span[7];

            return await ReadDataAsync<ImmutableDictionaryWrapper<StringWrapper, LongWrapper>>(indexOffset).ConfigureAwait(false);
        }

        private async ValueTask UpdateIndexOffsetAsync(long offset)
        {
            // move back to the header, specifically where we write the offset
            stream.Seek(HEADER_INDEX_OFFSET_OFFSET, SeekOrigin.Begin);

            var offsetData = new byte[sizeof(long)];
            offsetData[0] = (byte)(offset >> 56);
            offsetData[1] = (byte)(offset >> 48);
            offsetData[2] = (byte)(offset >> 40);
            offsetData[3] = (byte)(offset >> 32);
            offsetData[4] = (byte)(offset >> 24);
            offsetData[5] = (byte)(offset >> 16);
            offsetData[6] = (byte)(offset >> 8);
            offsetData[7] = (byte)offset;

            await stream.WriteAsync(offsetData).ConfigureAwait(false);

            // this HAS to be flushed before we return
            // since we're gonna assume the file is consistent again
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private ValueTask<long> WriteIndexAsync(ImmutableDictionaryWrapper<StringWrapper, LongWrapper> index)
        => WriteDataAsync(index);

        private ValueTask<long> WriteDataAsync<T>(T data)
            where T : struct, IDiagnosisSerializable<T>
        {
            // move to the end of the file
            stream.Seek(0, SeekOrigin.End);
            var ret = stream.Position;

            // serialize the data
            data.Write(writer);

            return new ValueTask<long>(ret);
        }

        private ValueTask WriteHeaderAsync(Version version)
        {
            var headerBytes = new byte[HEADER_SIZE];

            stream.Seek(0, SeekOrigin.Begin);

            var headerBytesMem = headerBytes.AsSpan();

            var dumpDiagActualBytes = headerBytesMem[HEADER_DUMP_DIAG_OFFSET..(HEADER_DUMP_DIAG_OFFSET + "DumpDiag".Length * sizeof(char))];
            var magicGuidActualBytes = headerBytesMem[HEADER_MAGIC_GUID_OFFSET..(HEADER_MAGIC_GUID_OFFSET + 16)];
            var versionActualBytes = headerBytesMem[HEADER_VERSION_OFFSET..(HEADER_VERSION_OFFSET + 4 * sizeof(int))];
            var indexOffsetBytes = headerBytesMem[HEADER_INDEX_OFFSET_OFFSET..(HEADER_INDEX_OFFSET_OFFSET + sizeof(long))];

            MemoryMarshal.AsBytes("DumpDiag".AsMemory().Span).CopyTo(dumpDiagActualBytes);
            MAGIC_GUID.ToByteArray().AsSpan().CopyTo(magicGuidActualBytes);

            versionActualBytes[0] = (byte)(version.Major >> 24);
            versionActualBytes[1] = (byte)(version.Major >> 16);
            versionActualBytes[2] = (byte)(version.Major >> 8);
            versionActualBytes[3] = (byte)version.Major;

            versionActualBytes[4] = (byte)(version.Minor >> 24);
            versionActualBytes[5] = (byte)(version.Minor >> 16);
            versionActualBytes[6] = (byte)(version.Minor >> 8);
            versionActualBytes[7] = (byte)version.Minor;

            versionActualBytes[8] = (byte)(version.Build >> 24);
            versionActualBytes[9] = (byte)(version.Build >> 16);
            versionActualBytes[10] = (byte)(version.Build >> 8);
            versionActualBytes[11] = (byte)version.Build;

            versionActualBytes[12] = (byte)(version.Revision >> 24);
            versionActualBytes[13] = (byte)(version.Revision >> 16);
            versionActualBytes[14] = (byte)(version.Revision >> 8);
            versionActualBytes[15] = (byte)version.Revision;

            indexOffsetBytes.Fill(0xFF);

            return stream.WriteAsync(headerBytes);
        }

        // internal for testing purposes
        internal async ValueTask<bool> IsValidForVersionAsync(Version version)
        {
            var headerBytes = new byte[HEADER_SIZE];

            stream.Seek(0, SeekOrigin.Begin);

            var len = await stream.ReadAsync(headerBytes).ConfigureAwait(false);
            if (len < HEADER_SIZE)
            {
                return false;
            }

            var headerBytesMem = headerBytes.AsMemory();

            var dumpDiagActualBytes = headerBytesMem[HEADER_DUMP_DIAG_OFFSET..(HEADER_DUMP_DIAG_OFFSET + "DumpDiag".Length * sizeof(char))];
            var magicGuidActualBytes = headerBytesMem[HEADER_MAGIC_GUID_OFFSET..(HEADER_MAGIC_GUID_OFFSET + 16)];
            var versionActualBytes = headerBytesMem[HEADER_VERSION_OFFSET..(HEADER_VERSION_OFFSET + 4 * sizeof(int))];
            var indexOffsetBytes = headerBytesMem[HEADER_INDEX_OFFSET_OFFSET..(HEADER_INDEX_OFFSET_OFFSET + sizeof(long))];

            var dumpDiagExpected = "DumpDiag".AsMemory();
            if (!dumpDiagActualBytes.Span.SequenceEqual(MemoryMarshal.AsBytes(dumpDiagExpected.Span)))
            {
                return false;
            }

            var magicGuidExpected = MAGIC_GUID.ToByteArray().AsMemory();
            if (!magicGuidActualBytes.Span.SequenceEqual(magicGuidExpected.Span))
            {
                return false;
            }

            var versionMajorActual = (versionActualBytes.Span[0] << 24) | (versionActualBytes.Span[1] << 16) | (versionActualBytes.Span[2] << 8) | versionActualBytes.Span[3];
            var versiorMinorActual = (versionActualBytes.Span[4] << 24) | (versionActualBytes.Span[5] << 16) | (versionActualBytes.Span[6] << 8) | versionActualBytes.Span[7];
            var versiorBuildActual = (versionActualBytes.Span[8] << 24) | (versionActualBytes.Span[9] << 16) | (versionActualBytes.Span[10] << 8) | versionActualBytes.Span[11];
            var versiorRevisionActual = (versionActualBytes.Span[12] << 24) | (versionActualBytes.Span[13] << 16) | (versionActualBytes.Span[14] << 8) | versionActualBytes.Span[15];

            var versionActual = new Version(versionMajorActual, versiorMinorActual, versiorBuildActual, versiorRevisionActual);
            if (!versionActual.Equals(version))
            {
                return false;
            }

            var indexOffset =
                ((long)indexOffsetBytes.Span[0] << 56) | ((long)indexOffsetBytes.Span[1] << 48) | ((long)indexOffsetBytes.Span[2] << 40) | ((long)indexOffsetBytes.Span[3] << 32) |
                ((long)indexOffsetBytes.Span[4] << 24) | ((long)indexOffsetBytes.Span[5] << 16) | ((long)indexOffsetBytes.Span[6] << 8) | (long)indexOffsetBytes.Span[7];
            if (indexOffset < HEADER_SIZE || indexOffset >= stream.Length)
            {
                return false;
            }

            return true;
        }

        public async ValueTask<(bool HasData, T Data)> LoadDataAsync<T>(string name) where T : struct, IDiagnosisSerializable<T>
        {
            await exclusionLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var key = new StringWrapper(name);
                if (!offsets.Value.TryGetValue(key, out var offsetToData))
                {
                    return (false, default(T));
                }

                var data = await ReadDataAsync<T>(offsetToData.Value).ConfigureAwait(false);

                return (true, data);
            }
            finally
            {
                exclusionLock.Release();
            }
        }

        public async ValueTask StoreDataAsync<T>(string name, T data) where T : struct, IDiagnosisSerializable<T>
        {
            await exclusionLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var key = new StringWrapper(name);

                if (offsets.Value.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Attempted to overwrite data under key {key}");
                }

                // write the data
                var dataOffset = await WriteDataAsync(data).ConfigureAwait(false);
                var updatedIndex = offsets.Value.Add(key, new LongWrapper(dataOffset));
                var updatedIndexWrapper = new ImmutableDictionaryWrapper<StringWrapper, LongWrapper>(updatedIndex);

                // write the new index
                var indexOffset = await WriteIndexAsync(updatedIndexWrapper).ConfigureAwait(false);

                // sync all the data before we update the index reference
                await stream.FlushAsync().ConfigureAwait(false);

                await UpdateIndexOffsetAsync(indexOffset).ConfigureAwait(false);

                offsets = updatedIndexWrapper;

            }
            finally
            {
                exclusionLock.Release();
            }
        }

        internal static ValueTask<FileBackedDiagnosisStorage> CreateAsync(FileInfo file)
        {
            FileStream stream;
            if (file.Exists)
            {
                stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            else
            {
                stream = file.Open(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }

            return new ValueTask<FileBackedDiagnosisStorage>(new FileBackedDiagnosisStorage(stream));
        }
    }
}
