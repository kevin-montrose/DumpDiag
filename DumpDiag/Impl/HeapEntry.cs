using System;

namespace DumpDiag.Impl
{
    internal readonly struct HeapEntry : IEquatable<HeapEntry>
    {
        public long Address { get; }
        public long MethodTable { get; }
        public int Size { get; }
        public bool Live { get; }
        public bool Dead => !Live;

        internal HeapEntry(long addr, long mt, int size, bool l)
        {
            Address = addr;
            MethodTable = mt;
            Size = size;
            Live = l;
        }

        public override string ToString()
        => $"{Address:X2} {MethodTable:X2} {Size:N0} {(Live ? "live" : "dead")}";

        public bool Equals(HeapEntry other)
        => other.Address == Address && other.MethodTable == MethodTable && other.Size == Size && other.Live == Live && other.Dead == Dead;

        public override bool Equals(object obj)
        => obj is HeapEntry other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Address, MethodTable, Size, Live, Dead);
    }
}
