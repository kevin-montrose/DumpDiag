using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal interface IResumableDiagnosisStorage : IAsyncDisposable
    {
        ValueTask IntializeWithVersionAsync(Version thisVersion);

        ValueTask<(bool HasData, T Data)> LoadDataAsync<T>(string name)
            where T : struct, IDiagnosisSerializable<T>;

        ValueTask StoreDataAsync<T>(string name, T data)
            where T : struct, IDiagnosisSerializable<T>;
    }

    // todo: move this
    internal interface IDiagnosisSerializable<T>
    {
        T Read(IBufferReader<byte> reader);
        void Write(IBufferWriter<byte> writer);
    }

    // todo: move this
    internal readonly struct IntWrapper : IDiagnosisSerializable<IntWrapper>, IEquatable<IntWrapper>, IComparable<IntWrapper>
    {
        internal int Value { get; }

        internal IntWrapper(int value)
        {
            Value = value;
        }

        public IntWrapper Read(IBufferReader<byte> reader)
        {
            var asLong = default(LongWrapper).Read(reader);

            var asUInt = (uint)asLong.Value;
            
            return new IntWrapper((int)asUInt);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            var asUInt = (uint)Value;

            new LongWrapper(asUInt).Write(writer);
        }

        public int CompareTo(IntWrapper other)
        => Value.CompareTo(other.Value);

        public override bool Equals(object? obj)
        => obj is IntWrapper other && Equals(other);

        public bool Equals(IntWrapper other)
        => Value.Equals(other.Value);

        public override int GetHashCode()
        => Value;

        public override string ToString()
        => Value.ToString();
    }

    // todo: move this
    internal readonly struct AddressWrapper : IDiagnosisSerializable<AddressWrapper>, IEquatable<AddressWrapper>, IComparable<AddressWrapper>
    {
        private const long NEVER_SET_BITS = 0x7;
        private const int SHIFT = 3;

        internal long Value { get; }

        internal AddressWrapper(long value)
        {
            if ((value & NEVER_SET_BITS) != 0)
            {
                throw new ArgumentException("Not an address, should be 8 byte aligned");
            }

            Value = value;
        }

        public AddressWrapper Read(IBufferReader<byte> reader)
        {
            var raw = (ulong)default(LongWrapper).Read(reader).Value;
            var shifted = raw << SHIFT;

            return new AddressWrapper((long)shifted);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            var shifted = ((ulong)Value) >> SHIFT;

            new LongWrapper((long)shifted).Write(writer);
        }

        public bool Equals(AddressWrapper other)
        => other.Value == Value;

        public override bool Equals(object? obj)
        => obj is AddressWrapper other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Value);

        public override string ToString()
        => $"{Value:X2}";

        public int CompareTo(AddressWrapper other)
        => Value.CompareTo(other.Value);
    }

    // todo: move this
    internal readonly struct LongWrapper : IDiagnosisSerializable<LongWrapper>, IEquatable<LongWrapper>, IComparable<LongWrapper>
    {
        // basic idea is to use the bits in the first byte to indicate how many bytes are needed
        // akin to UTF8
        //
        // So use 0b1xxxxxxx                                                                                  <= ??? ( 8 -  1 =  7 bits) [ 1  byte]
        //        0b01xxxxxx xxxxxxxx                                                                         <= ??? (16 -  2 = 14 bits) [ 2 bytes]
        //        0b001xxxxx xxxxxxxx xxxxxxxx                                                                <= ??? (24 -  3 = 21 bits) [ 3 bytes]
        //        0b0001xxxx xxxxxxxx xxxxxxxx xxxxxxxx                                                       <= ??? (32 -  4 = 28 bits) [ 4 bytes]
        //        0b00001xxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx                                              <= ??? (40 -  5 = 35 bits) [ 5 bytes]
        //        0b000001xx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx                                     <= ??? (48 -  6 = 42 bits) [ 6 bytes]
        //        0b0000001x xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx                            <= ??? (56 -  7 = 49 bits) [ 7 bytes]
        //        0b00000001 xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx                   <= ??? (64 -  8 = 56 bits) [ 8 bytes]
        //        0b00000000 1xxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx          <= ??? (72 -  9 = 63 bits) [ 9 bytes]
        //        0b00000000 01xxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx xxxxxxxx <= ??? (80 - 10 = 70 bits) [10 bytes]
        //
        // this doesn't self-synchronize like utf8 does, but we don't need that
        //
        // for various reasons high values are really rare, so we'll tend to pack these into fewer bytes
        //
        // By starting every value with some number of 1s and then at least 1 0, we can determine what we need to do
        // using a leading 0 count

        private const byte ONE_BYTE_MARKER = 0b10000000;
        private const byte ONE_BYTE_DATA = 0b01111111;

        private const byte TWO_BYTE_MARKER = 0b01000000;
        private const byte TWO_BYTE_DATA = 0b00111111;

        private const byte THREE_BYTE_MARKER = 0b00100000;
        private const byte THREE_BYTE_DATA = 0b00011111;

        private const byte FOUR_BYTE_MARKER = 0b00010000;
        private const byte FOUR_BYTE_DATA = 0b00001111;

        private const byte FIVE_BYTE_MARKER = 0b00001000;
        private const byte FIVE_BYTE_DATA = 0b00000111;

        private const byte SIX_BYTE_MARKER = 0b00000100;
        private const byte SIX_BYTE_DATA = 0b00000011;

        private const byte SEVEN_BYTE_MARKER = 0b00000010;
        private const byte SEVEN_BYTE_DATA = 0b00000001;

        private const byte EIGHT_BYTE_MARKER = 0b00000001;
        private const byte EIGHT_BYTE_DATA = 0b00000000;

        private const byte NINE_OR_MORE_BYTE_MARKER = 0b00000000;
        private const byte NINE_OR_MORE_BYTE_DATA = 0b00000000;

        private const int MAXIMUM_ENCODING_LENGTH = 10;

        /// <summary>
        /// Triples of (firstByteMask, secondByteMask, shift) for each count of leading zero bits.
        /// 
        /// So we can lookup the needed parameters in Write() with a simple indexing
        /// </summary>
        private static readonly byte[] WRITE_BYTE_MARKER_AND_SHIFTS_FOR_ZERO_BITS = PrepareWriteByteMarkersAndShiftForZeroBits();
        private static IntPtr WRITE_BYTE_MARKER_AND_SHIFT_FOR_ZERO_BITS_POINTER = GetPointer(WRITE_BYTE_MARKER_AND_SHIFTS_FOR_ZERO_BITS);   // keeping as pointer for faster indexing

        /// <summary>
        /// Triples of (mask, num bytes, needs next byte) for each count of leading zero bits.
        /// 
        /// So we can lookup the needed parameters in Read() with a simple indexing
        /// </summary>
        private static readonly byte[] READ_DATA_MASKS_AND_SKIPS_FOR_ZERO_BITS = PrepareReadDataMaskSkipsForZeroBits();
        private static IntPtr READ_DATA_MASKS_AND_SKIPS_FOR_ZERO_BITS_PTR = GetPointer(READ_DATA_MASKS_AND_SKIPS_FOR_ZERO_BITS);   // keeping as pointer for faster indexing

        internal long Value { get; }

        internal LongWrapper(long value)
        {
            Value = value;
        }

        private unsafe static IntPtr GetPointer(byte[] arr)
        {
            fixed (byte* ret = arr)
            {
                return (IntPtr)ret;
            }
        }

        private static byte[] PrepareReadDataMaskSkipsForZeroBits()
        {
            var toCopy =
                new byte[]
                {
                    ONE_BYTE_DATA, 1, 0,                  // 0 leading 0s
                    TWO_BYTE_DATA, 2, 0,                  // 1 leading 0
                    THREE_BYTE_DATA, 3, 0,                // 2 leading 0s
                    FOUR_BYTE_DATA, 4, 0,                 // 3 leading 0s
                    FIVE_BYTE_DATA, 5, 0,                 // 4 leading 0s
                    SIX_BYTE_DATA, 6, 0,                  // 5 leading 0s
                    SEVEN_BYTE_DATA, 7, 0,                // 7 leading 0s
                    EIGHT_BYTE_DATA, 8, 0,                // 8 leading 0s
                    NINE_OR_MORE_BYTE_DATA, 9, 1,         // 9+ leading 0s
                };

            var ret = GC.AllocateUninitializedArray<byte>(toCopy.Length, pinned: true);

            toCopy.CopyTo(ret.AsMemory());

            return ret;
        }

        private static byte[] PrepareWriteByteMarkersAndShiftForZeroBits()
        {
            var toCopy =
                new byte[]
                {
                    // 0x00000000_01000000 => 8 + 8 + 8 + 8 + 8 + 8 + 8 + 8 + 6 = 70 bits
                    NINE_OR_MORE_BYTE_MARKER, TWO_BYTE_MARKER, 9 * 8,
                    NINE_OR_MORE_BYTE_MARKER, TWO_BYTE_MARKER, 9 * 8, 

                    // 0x00000000_10000000 => allows 8 + 8 + 8 + 8 + 8 + 8 + 8 + 7 = 63 bits of data
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,       
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,
                    NINE_OR_MORE_BYTE_MARKER, ONE_BYTE_MARKER, 8 * 8,

                    // 0x0000001 => allows 8 + 8 + 8 + 8 + 8 + 8 + 8 + 0 = 56 bits of data
                    EIGHT_BYTE_MARKER, 0, 7 * 8,                            
                    EIGHT_BYTE_MARKER, 0, 7 * 8,
                    EIGHT_BYTE_MARKER, 0, 7 * 8,
                    EIGHT_BYTE_MARKER, 0, 7 * 8,
                    EIGHT_BYTE_MARKER, 0, 7 * 8,
                    EIGHT_BYTE_MARKER, 0, 7 * 8,
                    EIGHT_BYTE_MARKER, 0, 7 * 8,

                    // 0x00000010 => allows 8 + 8 + 8 + 8 + 8 + 8 + 1 = 49 bits of data
                    SEVEN_BYTE_MARKER, 0, 6 * 8,                            
                    SEVEN_BYTE_MARKER, 0, 6 * 8,
                    SEVEN_BYTE_MARKER, 0, 6 * 8,
                    SEVEN_BYTE_MARKER, 0, 6 * 8,
                    SEVEN_BYTE_MARKER, 0, 6 * 8,
                    SEVEN_BYTE_MARKER, 0, 6 * 8,
                    SEVEN_BYTE_MARKER, 0, 6 * 8,

                    // 0x00000100 => allows 8 + 8 + 8 + 8 + 8 + 2 = 42 bits of data
                    SIX_BYTE_MARKER, 0, 5 * 8,                              
                    SIX_BYTE_MARKER, 0, 5 * 8,
                    SIX_BYTE_MARKER, 0, 5 * 8,
                    SIX_BYTE_MARKER, 0, 5 * 8,
                    SIX_BYTE_MARKER, 0, 5 * 8,
                    SIX_BYTE_MARKER, 0, 5 * 8,
                    SIX_BYTE_MARKER, 0, 5 * 8,

                    // marker is 0x00001000 => allows 8 + 8 + 8 + 8 + 3 = 35 bits of data
                    FIVE_BYTE_MARKER, 0, 4 * 8,                             
                    FIVE_BYTE_MARKER, 0, 4 * 8,
                    FIVE_BYTE_MARKER, 0, 4 * 8,
                    FIVE_BYTE_MARKER, 0, 4 * 8,
                    FIVE_BYTE_MARKER, 0, 4 * 8,
                    FIVE_BYTE_MARKER, 0, 4 * 8,
                    FIVE_BYTE_MARKER, 0, 4 * 8,

                    // marker is 0x00010000 => allows 8 + 8 + 8 + 4 = 28 bits of data
                    FOUR_BYTE_MARKER, 0, 3 * 8,                             
                    FOUR_BYTE_MARKER, 0, 3 * 8,
                    FOUR_BYTE_MARKER, 0, 3 * 8,
                    FOUR_BYTE_MARKER, 0, 3 * 8,
                    FOUR_BYTE_MARKER, 0, 3 * 8,
                    FOUR_BYTE_MARKER, 0, 3 * 8,
                    FOUR_BYTE_MARKER, 0, 3 * 8,

                    // marker is 0x00100000 => allows 8 + 8 + 5 = 21 bits of data
                    THREE_BYTE_MARKER, 0, 2 * 8,                            
                    THREE_BYTE_MARKER, 0, 2 * 8,                            
                    THREE_BYTE_MARKER, 0, 2 * 8,                            
                    THREE_BYTE_MARKER, 0, 2 * 8,                            
                    THREE_BYTE_MARKER, 0, 2 * 8,                            
                    THREE_BYTE_MARKER, 0, 2 * 8,                            
                    THREE_BYTE_MARKER, 0, 2 * 8,                            

                    // marker is 0x01000000 => allows 8 + 6 = 14 bits of data
                    TWO_BYTE_MARKER, 0, 1 * 8,                              
                    TWO_BYTE_MARKER, 0, 1 * 8,                              
                    TWO_BYTE_MARKER, 0, 1 * 8,                              
                    TWO_BYTE_MARKER, 0, 1 * 8,                              
                    TWO_BYTE_MARKER, 0, 1 * 8,                              
                    TWO_BYTE_MARKER, 0, 1 * 8,                              
                    TWO_BYTE_MARKER, 0, 1 * 8,                              

                    // marker is 0x10000000 => allows 7 bits of data
                    ONE_BYTE_MARKER, 0, 0,                                  
                    ONE_BYTE_MARKER, 0, 0,                                  
                    ONE_BYTE_MARKER, 0, 0,                                  
                    ONE_BYTE_MARKER, 0, 0,                                  
                    ONE_BYTE_MARKER, 0, 0,                                  
                    ONE_BYTE_MARKER, 0, 0,                                  
                    ONE_BYTE_MARKER, 0, 0,                                  
                };

            var ret = GC.AllocateUninitializedArray<byte>(toCopy.Length, pinned: true);

            toCopy.CopyTo(ret.AsMemory());

            return ret;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint GetLeadingZeros(long l)
        {
            uint leadingOnes;
            if (System.Runtime.Intrinsics.X86.Lzcnt.X64.IsSupported)
            {
                leadingOnes = (uint)System.Runtime.Intrinsics.X86.Lzcnt.X64.LeadingZeroCount((ulong)l);
            }
            else if (System.Runtime.Intrinsics.Arm.ArmBase.Arm64.IsSupported)
            {
                leadingOnes = (uint)System.Runtime.Intrinsics.Arm.ArmBase.Arm64.LeadingZeroCount(l);
            }
            else
            {
                throw new NotImplementedException("Didn't expect this platform");
            }

            return leadingOnes;
        }

        internal static void Fill(IBufferReader<byte> reader, Span<byte> data)
        {
            var remaining = data;
            while (!remaining.IsEmpty)
            {
                var readInto = remaining;
                if (reader.Read(ref readInto) && readInto.IsEmpty)
                {
                    // ended early
                    throw new Exception("Shouldn't be possible");
                }

                remaining = remaining.Slice(readInto.Length);
                reader.Advance(readInto.Length);
            }
        }

        public unsafe LongWrapper Read(IBufferReader<byte> reader)
        {
            const uint IGNORED_BITS_IN_LONG = sizeof(long) * 8 - sizeof(byte) * 8; 

            Span<byte> maxData = stackalloc byte[MAXIMUM_ENCODING_LENGTH];
            var availableData = maxData;

            if (reader.Read(ref availableData) && availableData.Length == 0)
            {
                // ended early
                throw new Exception("Shouldn't be possible");
            }

            var firstByte = availableData[0];

            var leadingZerosFirstByte = GetLeadingZeros(firstByte) - IGNORED_BITS_IN_LONG;

            var ptr = (byte*)READ_DATA_MASKS_AND_SKIPS_FOR_ZERO_BITS_PTR;
            ptr += leadingZerosFirstByte * 3;
            
            var data = *ptr;
            ptr++;

            var totalBytesNeeded = *ptr;
            ptr++;

            // make sure we have all the data we need
            if (availableData.Length < totalBytesNeeded)
            {
                // note that this will re-read that first byte, but whatever
                availableData = maxData[0..totalBytesNeeded];
                Fill(reader, availableData);
            }
            else
            {
                // we already have it, but only consume the parts we need
                availableData = availableData[0..totalBytesNeeded];
                reader.Advance(availableData.Length);
            }

            var skipFirstByte = *ptr;
            if (skipFirstByte != 0)
            {
                var secondByte = availableData[1];
                var leadingZerosSecondByte = GetLeadingZeros(secondByte) - IGNORED_BITS_IN_LONG;
                ptr = (byte*)READ_DATA_MASKS_AND_SKIPS_FOR_ZERO_BITS_PTR;
                ptr += leadingZerosSecondByte * 3;
                data = *ptr;
                ptr++;

                var extraBytesNeeded = (byte)(*ptr - 1); // subtract one here, because we've already got one
                totalBytesNeeded += extraBytesNeeded;

                // we might need additional data
                if (availableData.Length < totalBytesNeeded)
                {
                    var extraNeeded = totalBytesNeeded - availableData.Length;
                    var toFill = maxData[availableData.Length..(availableData.Length + extraNeeded)];
                    Fill(reader, toFill);
                    availableData = maxData[0..totalBytesNeeded];
                }
            }

            var dataStart = availableData[skipFirstByte..];
            var ret = Concat(data, dataStart);

            return new LongWrapper((long)ret);

            static ulong Concat(byte dataMaskInFirstByte, Span<byte> all)
            {
                ulong ret = 0;

                ret = (ulong)(all[0] & dataMaskInFirstByte);

                for (var i = 1; i < all.Length; i++)
                {
                    ret <<= 8;
                    ret |= all[i];
                }

                return ret;
            }
        }

        public unsafe void Write(IBufferWriter<byte> writer)
        {
            var zeroBits = GetLeadingZeros(Value);

            var asULong = (ulong)Value;

            // lookup what's needed based on the first non-zero bit
            byte* lookupPtr = (byte*)WRITE_BYTE_MARKER_AND_SHIFT_FOR_ZERO_BITS_POINTER;
            lookupPtr += zeroBits * 3;
            var firstMarker = *lookupPtr;
            lookupPtr++;
            var secondMarker = *lookupPtr;
            lookupPtr++;
            int shift = *lookupPtr;

            var needSpace = (shift / 8) + 1;

            // actually compress the stuff into a span
            var maxToWrite = writer.GetSpan(needSpace);
            fixed (byte* maxToWritePtr = maxToWrite)
            {
                var ptr = maxToWritePtr;

                var partOfFirst = shift >= 64 ? 0 : (byte)(asULong >> shift);
                var firstByte = (byte)(firstMarker | partOfFirst);
                *ptr = firstByte;

                shift -= 8;
                ptr++;

                if (shift >= 0)
                {
                    var partOfSecond = shift >= 64 ? 0 : (byte)(asULong >> shift);
                    var secondByte = (byte)(secondMarker | partOfSecond);
                    *ptr = secondByte;

                    ptr++;
                    shift -= 8;

                    while (shift >= 0)
                    {
                        var fullByte = (byte)(asULong >> shift);
                        *ptr = fullByte;

                        ptr++;
                        shift -= 8;
                    }
                }
            }

            writer.Advance(needSpace);
        }

        public int CompareTo(LongWrapper other)
        => Value.CompareTo(other.Value);

        public override bool Equals(object? obj)
        => obj is LongWrapper other && Equals(other);

        public bool Equals(LongWrapper other)
        => Value.Equals(other.Value);

        public override int GetHashCode()
        => HashCode.Combine(Value);

        public override string ToString()
        => Value.ToString();
    }

    // todo: move this
    internal readonly struct ImmutableListWrapper<T> : IDiagnosisSerializable<ImmutableListWrapper<T>>
        where T : struct, IDiagnosisSerializable<T>
    {
        internal ImmutableList<T> Value { get; }

        internal ImmutableListWrapper(ImmutableList<T> value)
        {
            Value = value;
        }

        public ImmutableListWrapper<T> Read(IBufferReader<byte> reader)
        {
            var count = default(IntWrapper).Read(reader).Value;

            var inst = default(T);

            var builder = ImmutableList.CreateBuilder<T>();
            for (var i = 0; i < count; i++)
            {
                var item = inst.Read(reader);
                builder.Add(item);
            }

            return new ImmutableListWrapper<T>(builder.ToImmutable());
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new IntWrapper(Value.Count).Write(writer);

            foreach (var item in Value)
            {
                item.Write(writer);
            }
        }
    }

    // todo: move this
    internal readonly struct ImmutableHashSetWrapper<T> : IDiagnosisSerializable<ImmutableHashSetWrapper<T>>
        where T : struct, IDiagnosisSerializable<T>
    {
        internal ImmutableHashSet<T> Value { get; }

        internal ImmutableHashSetWrapper(ImmutableHashSet<T> value)
        {
            Value = value;
        }

        public ImmutableHashSetWrapper<T> Read(IBufferReader<byte> reader)
        {
            var count = default(IntWrapper).Read(reader).Value;

            var inst = default(T);

            var builder = ImmutableHashSet.CreateBuilder<T>();
            for (var i = 0; i < count; i++)
            {
                var item = inst.Read(reader);
                builder.Add(item);
            }

            return new ImmutableHashSetWrapper<T>(builder.ToImmutable());
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new IntWrapper(Value.Count).Write(writer);

            foreach (var item in Value)
            {
                item.Write(writer);
            }
        }
    }

    // todo: move this
    internal readonly struct ImmutableDictionaryWrapper<TKey, TValue> : IDiagnosisSerializable<ImmutableDictionaryWrapper<TKey, TValue>>
        where TKey : struct, IDiagnosisSerializable<TKey>
        where TValue : struct, IDiagnosisSerializable<TValue>
    {
        internal ImmutableDictionary<TKey, TValue> Value { get; }

        internal ImmutableDictionaryWrapper(ImmutableDictionary<TKey, TValue> value)
        {
            Value = value;
        }

        public ImmutableDictionaryWrapper<TKey, TValue> Read(IBufferReader<byte> reader)
        {
            var count = default(IntWrapper).Read(reader).Value;

            var instKey = default(TKey);
            var instVal = default(TValue);

            var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();
            for (var i = 0; i < count; i++)
            {
                var key = instKey.Read(reader);
                var val = instVal.Read(reader);
                builder.Add(key, val);
            }

            return new ImmutableDictionaryWrapper<TKey, TValue>(builder.ToImmutable());
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new IntWrapper(Value.Count).Write(writer);

            foreach (var kv in Value)
            {
                kv.Key.Write(writer);
                kv.Value.Write(writer);
            }
        }
    }

    // todo: move this
    internal readonly struct BoolWrapper : IDiagnosisSerializable<BoolWrapper>
    {
        internal bool Value { get; }

        internal BoolWrapper(bool value)
        {
            Value = value;
        }

        public BoolWrapper Read(IBufferReader<byte> reader)
        {
            var val = default(IntWrapper).Read(reader).Value;

            if (val == 0)
            {
                return new BoolWrapper(false);
            }
            else if (val == 1)
            {
                return new BoolWrapper(true);
            }

            throw new Exception("Shouldn't be possible");
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new IntWrapper(Value ? 1 : 0).Write(writer);
        }
    }

    // todo: move this
    internal readonly struct StringWrapper : IDiagnosisSerializable<StringWrapper>
    {
        internal string Value { get; }

        internal StringWrapper(string value)
        {
            Value = value;
        }

        public StringWrapper Read(IBufferReader<byte> reader)
        {
            var bytesInString = default(IntWrapper).Read(reader).Value;

            Span<byte> needsFill = bytesInString <= 512 ? stackalloc byte[bytesInString] : new byte[bytesInString];

            LongWrapper.Fill(reader, needsFill);

            var str = Encoding.UTF8.GetString(needsFill);

            return new StringWrapper(str);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            var stringBytes = Encoding.UTF8.GetByteCount(Value);
            new IntWrapper(stringBytes).Write(writer);

            Encoding.UTF8.GetBytes(Value, writer);
        }
    }
}
