using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct ObjectInstanceDetails
    {
        internal long EEClass { get; }
        internal ImmutableList<InstanceFieldWithValue> InstanceFields { get; }

        internal ObjectInstanceDetails(long eeClass, ImmutableList<InstanceFieldWithValue> fields)
        {
            EEClass = eeClass;
            InstanceFields = fields;
        }

        public override string ToString()
        => $"{EEClass} {string.Join(", ", InstanceFields)}";
    }
}
