using System;
using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct PinAnalysis : IEquatable<PinAnalysis>
    {
        internal ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> Pins { get; }
        internal ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> AsyncPins { get; }

        internal PinAnalysis(
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> pins,
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> asyncPins
        )
        {
            Pins = pins;
            AsyncPins = asyncPins;
        }

        public bool Equals(PinAnalysis other)
        {
            if (other.Pins.Count != Pins.Count)
            {
                return false;
            }

            if (other.AsyncPins.Count != AsyncPins.Count)
            {
                return false;
            }

            foreach (var type in Pins.Keys)
            {
                if (!other.Pins.TryGetValue(type, out var o))
                {
                    return false;
                }

                var s = Pins[type];

                if (o.Count != s.Count)
                {
                    return false;
                }

                foreach (var sK in s.Keys)
                {
                    if (!o.TryGetValue(sK, out var oV))
                    {
                        return false;
                    }

                    var sV = s[sK];

                    if (!sV.Equals(oV))
                    {
                        return false;
                    }
                }
            }

            foreach (var type in AsyncPins.Keys)
            {
                if (!other.AsyncPins.TryGetValue(type, out var o))
                {
                    return false;
                }

                var s = AsyncPins[type];

                if (o.Count != s.Count)
                {
                    return false;
                }

                foreach (var sK in s.Keys)
                {
                    if (!o.TryGetValue(sK, out var oV))
                    {
                        return false;
                    }

                    var sV = s[sK];

                    if (!sV.Equals(oV))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        => obj is PinAnalysis other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Pins.Count, AsyncPins.Count);   // other details aren't ordered, so can't include
    }
}
