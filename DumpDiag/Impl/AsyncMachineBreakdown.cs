using System;
using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct AsyncMachineBreakdown : IEquatable<AsyncMachineBreakdown>
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

        public bool Equals(AsyncMachineBreakdown other)
        {
            if (!other.Type.Equals(Type)) return false;
            if (other.StateSizeBytes != StateSizeBytes) return false;

            if (other.StateMachineFields.Count != StateMachineFields.Count) return false;

            for (var i = 0; i < StateMachineFields.Count; i++)
            {
                var o = other.StateMachineFields[i];
                var s = StateMachineFields[i];

                if (!o.Equals(s))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        => obj is AsyncMachineBreakdown other && Equals(other);

        public override int GetHashCode()
        {
            var ret = new HashCode();
            ret.Add(Type);
            ret.Add(StateSizeBytes);

            foreach (var field in StateMachineFields)
            {
                ret.Add(field);
            }

            return ret.ToHashCode();
        }
    }
}
