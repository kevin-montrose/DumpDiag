using System;
using System.Buffers;
using System.Collections.Immutable;

namespace DumpDiag.Impl
{
    internal readonly struct PinAnalysis : IEquatable<PinAnalysis>, IDiagnosisSerializable<PinAnalysis>
    {
        internal readonly struct CountSizePair : IEquatable<CountSizePair>, IDiagnosisSerializable<CountSizePair>
        {
            internal long Size { get; }
            internal int Count { get; }

            internal CountSizePair(long size, int count)
            {
                Size = size;
                Count = count;
            }

            public override bool Equals(object? obj)
            => obj is CountSizePair other && Equals(other);

            public bool Equals(CountSizePair other)
            => other.Size == Size && other.Count == Count;

            public override int GetHashCode()
            => HashCode.Combine(Size, Count);

            public override string ToString()
            => $"{nameof(Count)} = {Count}, {nameof(Size)} = {Size}";

            public CountSizePair Read(IBufferReader<byte> reader)
            {
                var s = default(LongWrapper).Read(reader).Value;
                var c = default(IntWrapper).Read(reader).Value;

                return new CountSizePair(s, c);
            }

            public void Write(IBufferWriter<byte> writer)
            {
                new LongWrapper(Size).Write(writer);
                new IntWrapper(Count).Write(writer);
            }
        }

        internal ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, CountSizePair>> Pins { get; }
        internal ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, CountSizePair>> AsyncPins { get; }

        internal PinAnalysis(
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> pins,
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, (int Count, long Size)>> asyncPins
        ) :
            this(
                pins.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutableDictionary(x => x.Key, x => new CountSizePair(x.Value.Size, x.Value.Count))),
                asyncPins.ToImmutableDictionary(kv => kv.Key, kv => kv.Value.ToImmutableDictionary(x => x.Key, x => new CountSizePair(x.Value.Size, x.Value.Count)))
            )
        {
        }

        private PinAnalysis(
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, CountSizePair>> pins,
            ImmutableDictionary<HeapDetails.HeapClassification, ImmutableDictionary<TypeDetails, CountSizePair>> asyncPins
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

        public PinAnalysis Read(IBufferReader<byte> reader)
        {
            var p = default(ImmutableDictionaryWrapper<IntWrapper, ImmutableDictionaryWrapper<TypeDetails, CountSizePair>>).Read(reader).Value;
            var a = default(ImmutableDictionaryWrapper<IntWrapper, ImmutableDictionaryWrapper<TypeDetails, CountSizePair>>).Read(reader).Value;

            return
                new PinAnalysis(
                    p.ToImmutableDictionary(kv => (HeapDetails.HeapClassification)kv.Key.Value, kv => kv.Value.Value),
                    a.ToImmutableDictionary(kv => (HeapDetails.HeapClassification)kv.Key.Value, kv => kv.Value.Value)
                );
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new ImmutableDictionaryWrapper<IntWrapper, ImmutableDictionaryWrapper<TypeDetails, CountSizePair>>(
                Pins.ToImmutableDictionary(
                    kv => new IntWrapper((int)kv.Key),
                    kv => new ImmutableDictionaryWrapper<TypeDetails, CountSizePair>(kv.Value)
                )
            )
            .Write(writer);
            new ImmutableDictionaryWrapper<IntWrapper, ImmutableDictionaryWrapper<TypeDetails, CountSizePair>>(
                AsyncPins.ToImmutableDictionary(
                    kv => new IntWrapper((int)kv.Key),
                    kv => new ImmutableDictionaryWrapper<TypeDetails, CountSizePair>(kv.Value)
                )
            )
            .Write(writer);
        }
    }
}
