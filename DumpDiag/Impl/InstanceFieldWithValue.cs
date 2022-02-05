using System;

namespace DumpDiag.Impl
{
    internal readonly struct InstanceFieldWithValue : IEquatable<InstanceFieldWithValue>
    {
        internal InstanceField InstanceField { get; }
        internal long Value { get; }

        internal InstanceFieldWithValue(InstanceField field, long val)
        {
            InstanceField = field;
            Value = val;
        }

        public override string ToString()
        => $"{InstanceField} {Value:X2}";

        public bool Equals(InstanceFieldWithValue other)
        => other.Value == Value && other.InstanceField.Equals(InstanceField);

        public override bool Equals(object? obj)
        => obj is InstanceFieldWithValue other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(InstanceField, Value);
    }
}
