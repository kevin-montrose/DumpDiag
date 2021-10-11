using System;

namespace DumpDiag.Impl
{
    internal readonly struct TypeDetails : IEquatable<TypeDetails>
    {
        internal string TypeName { get; }

        internal TypeDetails(string name)
        {
            TypeName = name;
        }

        public override string ToString()
        => $"{TypeName}";

        public bool Equals(TypeDetails other)
        => other.TypeName == TypeName;

        public override bool Equals(object? obj)
        => obj is TypeDetails other && Equals(other);

        public override int GetHashCode()
        => TypeName.GetHashCode();
    }
}
