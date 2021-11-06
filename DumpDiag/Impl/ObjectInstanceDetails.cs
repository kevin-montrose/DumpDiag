using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct ObjectInstanceDetails
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
    }
}
