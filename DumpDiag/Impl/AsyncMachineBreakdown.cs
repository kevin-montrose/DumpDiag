using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct AsyncMachineBreakdown
    {
        internal TypeDetails Type { get; }
        internal int StateSizeBytes { get; }
        internal ImmutableList<InstanceFieldWithTypeDetails> StateMachineFields { get; }

        internal AsyncMachineBreakdown(TypeDetails type, int stateSize, ImmutableList<InstanceFieldWithTypeDetails> stateMachineFields)
        {
            Type = type;
            StateSizeBytes = stateSize;
            StateMachineFields = stateMachineFields;
        }

        public override string ToString()
        => $"{Type} {StateSizeBytes:N0} {string.Join(", ", StateMachineFields)}";
    }
}
