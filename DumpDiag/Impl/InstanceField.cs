using System;

namespace DumpDiag.Impl
{
    internal readonly struct InstanceField : IEquatable<InstanceField>
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
    }

    // todo: move this elsewhere
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

    // todo: move this elsewhere
    internal readonly struct InstanceFieldWithTypeDetails
    {
        internal TypeDetails TypeDetails { get; }
        internal InstanceField InstanceField { get; }

        internal InstanceFieldWithTypeDetails(TypeDetails type, InstanceField instanceField)
        {
            TypeDetails = type;
            InstanceField = instanceField;
        }

        public override string ToString()
        => $"{TypeDetails} {InstanceField}";
    }
}
