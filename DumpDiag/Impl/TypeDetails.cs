using System;

namespace DumpDiag.Impl
{
    internal readonly struct TypeDetails : IEquatable<TypeDetails>
    {
        internal string TypeName { get; }
        internal long MethodTable { get; }

        internal TypeDetails(string name, long methodTable)
        {
            TypeName = name;
            MethodTable = methodTable;
        }

        public override string ToString()
        => $"{TypeName} {MethodTable:X2}";

        public bool Equals(TypeDetails other)
        => other.MethodTable == MethodTable && other.TypeName == TypeName;

        public override bool Equals(object? obj)
        => obj is TypeDetails other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(TypeName, MethodTable);
    }
}
