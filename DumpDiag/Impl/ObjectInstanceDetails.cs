using System;
using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct ObjectInstanceDetails : IEquatable<ObjectInstanceDetails>
    {
        internal long EEClass { get; }
        internal long MethodTable { get; }
        internal ImmutableList<InstanceFieldWithValue> InstanceFields { get; }

        internal ObjectInstanceDetails(long eeClass, long mt, ImmutableList<InstanceFieldWithValue> fields)
        {
            EEClass = eeClass;
            MethodTable = mt;
            InstanceFields = fields;
        }

        public override string ToString()
        => $"{EEClass:X2} {MethodTable:X2} {string.Join(", ", InstanceFields)}";

        public bool Equals(ObjectInstanceDetails other)
        {
            if (other.EEClass != EEClass) return false;
            if (other.MethodTable != MethodTable) return false;
            if (other.InstanceFields.Count != InstanceFields.Count) return false;

            foreach (var otherField in other.InstanceFields)
            {
                var matched = false;

                foreach (var selfField in InstanceFields)
                {
                    if (otherField.Equals(selfField))
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        => obj is ObjectInstanceDetails other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(EEClass, MethodTable);  // doesn't include InstanceFields because of order might vary
    }
}
