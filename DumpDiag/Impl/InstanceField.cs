using System;
using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct InstanceField : IEquatable<InstanceField>, IDiagnosisSerializable<InstanceField>
    {
        internal string Name { get; }
        internal long MethodTable { get; }

        internal InstanceField(string name, long methodTable)
        {
            Name = name;
            MethodTable = methodTable;
        }

        public bool Equals(InstanceField other)
        => other.MethodTable == MethodTable && other.Name == Name;

        public override bool Equals(object? obj)
        => obj is InstanceField other && Equals(other);

        public override string ToString()
        => $"{Name} {MethodTable:X2}";

        public override int GetHashCode()
        => HashCode.Combine(Name, MethodTable);

        public InstanceField Read(IBufferReader<byte> reader)
        {
            var n = default(StringWrapper).Read(reader).Value;
            var mt = default(AddressWrapper).Read(reader).Value;

            return new InstanceField(n, mt);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new StringWrapper(Name).Write(writer);
            new AddressWrapper(MethodTable).Write(writer);
        }
    }
}
