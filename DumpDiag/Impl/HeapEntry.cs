using System;
using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct HeapEntry : IEquatable<HeapEntry>, IDiagnosisSerializable<HeapEntry>
    {
        public long Address { get; }
        public long MethodTable { get; }
        public int SizeBytes { get; }
        public bool Live { get; }
        public bool Dead => !Live;

        internal HeapEntry(long addr, long mt, int size, bool l)
        {
            Address = addr;
            MethodTable = mt;
            SizeBytes = size;
            Live = l;
        }

        public override string ToString()
        => $"{Address:X2} {MethodTable:X2} {SizeBytes:N0} {(Live ? "live" : "dead")}";

        public bool Equals(HeapEntry other)
        => other.Address == Address && other.MethodTable == MethodTable && other.SizeBytes == SizeBytes && other.Live == Live && other.Dead == Dead;

        public override bool Equals(object? obj)
        => obj is HeapEntry other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Address, MethodTable, SizeBytes, Live, Dead);

        public HeapEntry Read(IBufferReader<byte> reader)
        {
            var a = default(AddressWrapper).Read(reader).Value;
            var m = default(AddressWrapper).Read(reader).Value;
            var s = default(IntWrapper).Read(reader).Value;
            var l = default(BoolWrapper).Read(reader).Value;

            return new HeapEntry(a, m, s, l);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new AddressWrapper(Address).Write(writer);
            new AddressWrapper(MethodTable).Write(writer);
            new IntWrapper(SizeBytes).Write(writer);
            new BoolWrapper(Live).Write(writer);
        }
    }
}
