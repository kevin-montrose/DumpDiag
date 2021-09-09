using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct ThreadAnalysis
    {
        internal ImmutableList<ImmutableList<AnalyzerStackFrame>> ThreadStacks { get; }
        internal ImmutableDictionary<string, int> StackFrameCounts { get; }

        internal ThreadAnalysis(ImmutableList<ImmutableList<AnalyzerStackFrame>> stacks, ImmutableDictionary<string, int> counts)
        {
            ThreadStacks = stacks;
            StackFrameCounts = counts;
        }
    }
}
