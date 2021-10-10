using System;

namespace DumpDiag.Impl
{
    internal readonly struct AsyncStateMachineDetails : IEquatable<AsyncStateMachineDetails>
    {
        internal long Address { get; }
        internal long MethodTable { get; }
        internal int SizeBytes { get; }
        internal string Description { get; }

        internal AsyncStateMachineDetails(long addr, long mt, int size, string description)
        {
            Address = addr;
            MethodTable = mt;
            SizeBytes = size;
            Description = description;
        }

        public override string ToString()
        => $"{Address:X2} {MethodTable:X2} {SizeBytes:N0} {Description}";

        public bool Equals(AsyncStateMachineDetails other)
        => Address == other.Address && MethodTable == other.MethodTable && SizeBytes == other.SizeBytes && Description == other.Description;

        public override bool Equals(object obj)
        => obj is AsyncStateMachineDetails other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Address, MethodTable, SizeBytes, Description);
    }
}
