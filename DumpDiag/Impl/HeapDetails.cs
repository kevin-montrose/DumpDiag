using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace DumpDiag.Impl
{
    internal readonly struct HeapDetails : IEquatable<HeapDetails>, IDiagnosisSerializable<HeapDetails>
    {
        internal enum HeapClassification
        {
            None = 0,

            Generation0,
            Generation1,
            Generation2,
            LargeObjectHeap,
            PinnedObjectHeap
        }

        private readonly int heapIndex;

        private readonly long gen0Start;
        private readonly long gen1Start;
        private readonly long gen2Start;

        private readonly ImmutableArray<HeapDetailsBuilder.HeapSegment> soh;
        private readonly ImmutableArray<HeapDetailsBuilder.HeapSegment> loh;
        private readonly ImmutableArray<HeapDetailsBuilder.HeapSegment> poh;

        internal HeapDetails(
            int index,
            long g0,
            long g1,
            long g2,
            ImmutableArray<HeapDetailsBuilder.HeapSegment> soh,
            ImmutableArray<HeapDetailsBuilder.HeapSegment> loh,
            ImmutableArray<HeapDetailsBuilder.HeapSegment> poh
        )
        {
            Debug.Assert(soh.Length == 1);

            heapIndex = index;
            gen0Start = g0;
            gen1Start = g1;
            gen2Start = g2;

            this.soh = soh;
            this.loh = loh;
            this.poh = poh;
        }

        internal bool TryClassify(HeapEntry heapEntry, out HeapClassification heapClassification)
        {
            var addr = heapEntry.Address;

            if (IsIn(addr, soh))
            {
                long? dist = null;
                var classified = HeapClassification.None;

                if (addr >= gen0Start)
                {
                    dist = addr - gen0Start;
                    classified = HeapClassification.Generation0;
                }

                if (addr >= gen1Start)
                {
                    var newDist = addr - gen1Start;
                    if (dist == null || newDist < dist.Value)
                    {
                        dist = newDist;
                        classified = HeapClassification.Generation1;
                    }
                }

                if (addr >= gen2Start)
                {
                    var newDist = addr - gen2Start;
                    if (dist == null || newDist < dist.Value)
                    {
                        classified = HeapClassification.Generation2;
                    }
                }

                if (classified == HeapClassification.None)
                {
                    throw new Exception($"Couldn't classify entry on SOH, shouldn't be possible");
                }

                heapClassification = classified;
                return true;
            }

            if (IsIn(addr, loh))
            {
                heapClassification = HeapClassification.LargeObjectHeap;
                return true;
            }

            if (IsIn(addr, poh))
            {
                heapClassification = HeapClassification.PinnedObjectHeap;
                return true;
            }

            heapClassification = default;
            return false;

            static bool IsIn(long addr, ImmutableArray<HeapDetailsBuilder.HeapSegment> segments)
            {
                foreach (var seg in segments)
                {
                    if (addr >= seg.LowAddress && addr < seg.HighAddress)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Heap {heapIndex:N0}");
            sb.AppendLine("----");
            sb.AppendLine($"Gen 0 Start: {gen0Start:X2}");
            sb.AppendLine($"Gen 1 Start: {gen1Start:X2}");
            sb.AppendLine($"Gen 2 Start: {gen2Start:X2}");

            sb.AppendLine();
            sb.AppendLine($"Small Object Heap");
            sb.AppendLine("----");
            foreach (var segment in soh)
            {
                sb.AppendLine($"  Start: {segment.LowAddress:X2}, Size: {segment.SizeBytes:N0} bytes");
            }

            if (loh.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Large Object Heap");
                sb.AppendLine("----");
                foreach (var segment in loh)
                {
                    sb.AppendLine($"  Start: {segment.LowAddress:X2}, Size: {segment.SizeBytes:N0} bytes");
                }
            }

            if (poh.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Pinned Object Heap");
                sb.AppendLine("----");
                foreach (var segment in poh)
                {
                    sb.AppendLine($"  Start: {segment.LowAddress:X2}, Size: {segment.SizeBytes:N0} bytes");
                }
            }

            return sb.ToString();
        }

        public bool Equals(HeapDetails other)
        => other.gen0Start == gen0Start &&
           other.gen1Start == gen1Start &&
           other.gen2Start == gen2Start &&
           other.heapIndex == heapIndex &&
           other.loh.SequenceEqual(loh) &&
           other.poh.SequenceEqual(poh) &&
           other.soh.SequenceEqual(soh);

        public override bool Equals(object? obj)
        => obj is HeapDetails other && Equals(other);

        public override int GetHashCode()
        {
            var ret = new HashCode();
            ret.Add(gen0Start);
            ret.Add(gen1Start);
            ret.Add(gen2Start);
            ret.Add(heapIndex);

            foreach (var seg in loh)
            {
                ret.Add(seg);
            }

            foreach (var seg in poh)
            {
                ret.Add(seg);
            }

            foreach (var seg in soh)
            {
                ret.Add(soh);
            }

            return ret.ToHashCode();
        }

        public HeapDetails Read(IBufferReader<byte> reader)
        {
            var g0 = default(AddressWrapper).Read(reader).Value;
            var g1 = default(AddressWrapper).Read(reader).Value;
            var g2 = default(AddressWrapper).Read(reader).Value;
            var h = default(IntWrapper).Read(reader).Value;
            var l = default(ImmutableListWrapper<HeapDetailsBuilder.HeapSegment>).Read(reader).Value.ToImmutableArray();
            var p = default(ImmutableListWrapper<HeapDetailsBuilder.HeapSegment>).Read(reader).Value.ToImmutableArray();
            var s = default(ImmutableListWrapper<HeapDetailsBuilder.HeapSegment>).Read(reader).Value.ToImmutableArray();

            return new HeapDetails(h, g0, g1, g2, s, l, p);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new AddressWrapper(this.gen0Start).Write(writer);
            new AddressWrapper(this.gen1Start).Write(writer);
            new AddressWrapper(this.gen2Start).Write(writer);
            new IntWrapper(this.heapIndex).Write(writer);
            new ImmutableListWrapper<HeapDetailsBuilder.HeapSegment>(this.loh.ToImmutableList()).Write(writer);
            new ImmutableListWrapper<HeapDetailsBuilder.HeapSegment>(this.poh.ToImmutableList()).Write(writer);
            new ImmutableListWrapper<HeapDetailsBuilder.HeapSegment>(this.soh.ToImmutableList()).Write(writer);
        }
    }
}
