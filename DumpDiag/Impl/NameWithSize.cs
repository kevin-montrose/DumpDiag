using System;

namespace DumpDiag.Impl
{
    internal readonly struct NameWithSize : IEquatable<NameWithSize>
    {
        internal string Name { get; }
        internal int SizeBytes { get; }

        internal NameWithSize(string name, int size)
        {
            Name = name;
            SizeBytes = size;
        }

        public override string ToString()
        => $"{Name} {SizeBytes:N0}";

        public bool Equals(NameWithSize other)
        => SizeBytes == other.SizeBytes && Name == other.Name;

        public override bool Equals(object? obj)
        => obj is NameWithSize other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Name, SizeBytes);
    }
}
