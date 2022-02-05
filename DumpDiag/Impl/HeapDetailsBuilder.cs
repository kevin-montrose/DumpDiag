using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;

namespace DumpDiag.Impl
{
    internal struct HeapDetailsBuilder
    {
        internal readonly struct HeapSegment : IEquatable<HeapSegment>, IDiagnosisSerializable<HeapSegment>
        {
            internal long LowAddress { get; }
            internal long SizeBytes { get; }
            internal long HighAddress { get; }

            internal HeapSegment(long startAddr, long size)
            {
                Debug.Assert(size > 0);

                LowAddress = startAddr - size;
                SizeBytes = size;
                HighAddress = startAddr;
            }

            public override string ToString()
            => $"{LowAddress:X2} {SizeBytes:N0}";

            public bool Equals(HeapSegment other)
            => other.LowAddress == LowAddress &&
               other.SizeBytes == SizeBytes;

            public override bool Equals(object? obj)
            => obj is HeapSegment other && Equals(other);

            public override int GetHashCode()
            => HashCode.Combine(LowAddress, SizeBytes);

            public HeapSegment Read(IBufferReader<byte> reader)
            {
                var h = default(AddressWrapper).Read(reader).Value;
                var s = default(LongWrapper).Read(reader).Value;

                return new HeapSegment(h, s);
            }

            public void Write(IBufferWriter<byte> writer)
            {
                new AddressWrapper(HighAddress).Write(writer);
                new LongWrapper(SizeBytes).Write(writer);
            }
        }

        private long? gen0Start;
        internal long Gen0Start
        {
            set
            {
                if (!IsStarted || gen0Start != null)
                {
                    throw new InvalidOperationException();
                }

                gen0Start = value;
            }
        }

        private long? gen1Start;
        internal long Gen1Start
        {
            set
            {
                if (!IsStarted || gen1Start != null)
                {
                    throw new InvalidOperationException();
                }

                gen1Start = value;
            }
        }

        private long? gen2Start;
        internal long Gen2Start
        {
            set
            {
                if (!IsStarted || gen2Start != null)
                {
                    throw new InvalidOperationException();
                }

                gen2Start = value;
            }
        }

        internal bool GenerationsFinished => gen0Start != null && gen1Start != null && gen2Start != null;

        private int? index;
        internal bool IsStarted => index.HasValue;

        private ImmutableArray<HeapSegment>.Builder? soh;
        private ImmutableArray<HeapSegment>.Builder? loh;
        private ImmutableArray<HeapSegment>.Builder? poh;

        private ImmutableArray<HeapSegment>.Builder? currentHeap;

        internal void StartSmallObjectHeap()
        => StartHeap(ref currentHeap, ref soh);

        internal void StartLargeObjectHeap()
        => StartHeap(ref currentHeap, ref loh);

        internal void StartPinnedObjectHeap()
        => StartHeap(ref currentHeap, ref poh);

        private static void StartHeap(ref ImmutableArray<HeapSegment>.Builder? currentHeap, ref ImmutableArray<HeapSegment>.Builder? newHeap)
        {
            if (newHeap != null)
            {
                throw new InvalidOperationException();
            }

            newHeap = ImmutableArray.CreateBuilder<HeapSegment>();
            currentHeap = newHeap;
        }

        internal void AddSegment(long startAddr, long sizeBytes)
        {
            if (currentHeap == null)
            {
                throw new InvalidOperationException();
            }

            currentHeap.Add(new HeapSegment(startAddr, sizeBytes));
        }

        internal void Start(int index)
        {
            if (IsStarted)
            {
                throw new InvalidOperationException();
            }

            this.index = index;
        }

        internal HeapDetails ToHeapDetails()
        {
            if (index == null)
            {
                throw new InvalidOperationException();
            }

            if (gen0Start == null)
            {
                throw new InvalidOperationException();
            }

            if (gen1Start == null)
            {
                throw new InvalidOperationException();
            }

            if (gen2Start == null)
            {
                throw new InvalidOperationException();
            }

            if (soh == null)
            {
                throw new InvalidOperationException();
            }

            var sohArr = soh.ToImmutable();
            var lohArr = loh?.ToImmutable() ?? ImmutableArray<HeapSegment>.Empty;
            var pohArr = poh?.ToImmutable() ?? ImmutableArray<HeapSegment>.Empty;

            return new HeapDetails(index.Value, gen0Start.Value, gen1Start.Value, gen2Start.Value, sohArr, lohArr, pohArr);
        }
    }
}
