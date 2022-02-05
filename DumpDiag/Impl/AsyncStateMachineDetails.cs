using System;
using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct AsyncStateMachineDetails : IEquatable<AsyncStateMachineDetails>, IDiagnosisSerializable<AsyncStateMachineDetails>
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

        public override bool Equals(object? obj)
        => obj is AsyncStateMachineDetails other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Address, MethodTable, SizeBytes, Description);

        public AsyncStateMachineDetails Read(IBufferReader<byte> reader)
        {
            var a = default(AddressWrapper).Read(reader).Value;
            var m = default(AddressWrapper).Read(reader).Value;
            var s = default(IntWrapper).Read(reader).Value;
            var d = default(StringWrapper).Read(reader).Value;

            return new AsyncStateMachineDetails(a, m, s, d);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new AddressWrapper(Address).Write(writer);
            new AddressWrapper(MethodTable).Write(writer);
            new IntWrapper(SizeBytes).Write(writer);
            new StringWrapper(Description).Write(writer);
        }
    }
}
