using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Linq;

namespace DumpDiag.Impl
{
    internal readonly struct ThreadAnalysis : IEquatable<ThreadAnalysis>, IDiagnosisSerializable<ThreadAnalysis>
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

        public ThreadAnalysis Read(IBufferReader<byte> reader)
        {
            var stacks = default(ImmutableListWrapper<ImmutableListWrapper<AnalyzerStackFrame>>).Read(reader);
            var frames = default(ImmutableDictionaryWrapper<StringWrapper, IntWrapper>).Read(reader);

            return new ThreadAnalysis(stacks.Value.Select(x => x.Value).ToImmutableList(), frames.Value.ToImmutableDictionary(kv => kv.Key.Value, kv => kv.Value.Value));
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new ImmutableListWrapper<ImmutableListWrapper<AnalyzerStackFrame>>(ThreadStacks.Select(x => new ImmutableListWrapper<AnalyzerStackFrame>(x)).ToImmutableList()).Write(writer);
            new ImmutableDictionaryWrapper<StringWrapper, IntWrapper>(StackFrameCounts.ToImmutableDictionary(kv => new StringWrapper(kv.Key), kv => new IntWrapper(kv.Value))).Write(writer);
        }
    }
}
