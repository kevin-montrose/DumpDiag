using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct EEClassDetails
    {
        internal string ClassName { get; }
        internal long ParentEEClass { get; }
        internal ImmutableList<InstanceField> InstanceFields { get; }

        internal EEClassDetails(string className, long parent, ImmutableList<InstanceField> fields)
        {
            ClassName = className;
            ParentEEClass = parent;
            InstanceFields = fields;
        }

        public override string ToString()
        => $"{ParentEEClass:X2} {ClassName} {string.Join(", ", InstanceFields)}";
    }
}