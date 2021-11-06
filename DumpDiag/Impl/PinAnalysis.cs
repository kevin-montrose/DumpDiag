using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct PinAnalysis
    {
        internal ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<string, (int Count, long Size)>> Pins { get; }
        internal ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<string, (int Count, long Size)>> AsyncPins { get; }

        internal PinAnalysis(
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<string, (int Count, long Size)>> pins,
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<string, (int Count, long Size)>> asyncPins
        )
        {
            Pins = pins;
            AsyncPins = asyncPins;
        }
    }
}
