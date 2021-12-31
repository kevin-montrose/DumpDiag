using System;
using System.Collections.Immutable;
using System.Linq;

namespace DumpDiag.Impl
{
    internal readonly struct ThreadAnalysis : IEquatable<ThreadAnalysis>
    {
        internal ImmutableList<ImmutableList<AnalyzerStackFrame>> ThreadStacks { get; }
        internal ImmutableDictionary<string, int> StackFrameCounts { get; }

        internal ThreadAnalysis(ImmutableList<ImmutableList<AnalyzerStackFrame>> stacks, ImmutableDictionary<string, int> counts)
        {
            ThreadStacks = stacks;
            StackFrameCounts = counts;
        }

        public bool Equals(ThreadAnalysis other)
        {
            // only check the stable bits
            if(other.ThreadStacks.Count != ThreadStacks.Count)
            {
                return false;
            }

            for (var i = 0; i < ThreadStacks.Count; i++)
            {
                var o = other.ThreadStacks[i];
                var s = ThreadStacks[i];

                if (!o.SequenceEqual(s))
                {
                    return false;
                }
            }

            return true;
        }

        public override int GetHashCode()
        {
            var ret = new HashCode();

            foreach(var stack in ThreadStacks)
            {
                foreach(var frame in stack)
                {
                    ret.Add(frame);
                }
            }

            return ret.ToHashCode();
        }
    }
}
