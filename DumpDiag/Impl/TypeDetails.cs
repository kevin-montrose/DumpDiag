using System;

namespace DumpDiag.Impl
{
    internal readonly struct TypeDetails : IEquatable<TypeDetails>, IComparable<TypeDetails>
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
        => other.MethodTable == MethodTable; // names aren't consistently formatted between analyzers, so we can't use it for equality

        public override bool Equals(object? obj)
        => obj is TypeDetails other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(MethodTable); // names aren't consistently formatted between analyzers, so we can't use it for equality 

        public int CompareTo(TypeDetails other)
        => other.MethodTable.CompareTo(MethodTable);
    }
}
