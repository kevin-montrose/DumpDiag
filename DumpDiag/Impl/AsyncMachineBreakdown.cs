using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct AsyncMachineBreakdown
    {
        internal TypeDetails Type { get; }
        internal long EEClass { get; }
        internal int StateSizeBytes { get; }
        internal ImmutableList<InstanceFieldWithTypeDetails> StateMachineFields { get; }

        internal AsyncMachineBreakdown(TypeDetails type, long eeClass, int stateSize, ImmutableList<InstanceFieldWithTypeDetails> stateMachineFields)
        {
            Type = type;
            EEClass = eeClass;
            StateSizeBytes = stateSize;
            StateMachineFields = stateMachineFields;
        }

        public override string ToString()
        => $"{Type} {EEClass:X2} {StateSizeBytes:N0} {string.Join(", ", StateMachineFields)}";
    }
}
