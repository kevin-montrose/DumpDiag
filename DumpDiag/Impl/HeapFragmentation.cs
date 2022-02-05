using System;
using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct HeapFragmentation : IEquatable<HeapFragmentation>, IDiagnosisSerializable<HeapFragmentation>
    {
        internal long Gen0Size { get; }
        internal long Gen0Free { get; }
        internal double Gen0FragementationPercent => CalculatePercent(Gen0Free, Gen0Size);

        internal long Gen1Size { get; }
        internal long Gen1Free { get; }
        internal double Gen1FragementationPercent => CalculatePercent(Gen1Free, Gen1Size);

        internal long Gen2Size { get; }
        internal long Gen2Free { get; }
        internal double Gen2FragementationPercent => CalculatePercent(Gen2Free, Gen2Size);

        internal long LOHSize { get; }
        internal long LOHFree { get; }
        internal double LOHFragementationPercent => CalculatePercent(LOHFree, LOHSize);

        internal long POHSize { get; }
        internal long POHFree { get; }
        internal double POHFragementationPercent => CalculatePercent(POHFree, POHSize);

        internal long TotalSize => Gen0Size + Gen1Size + Gen2Size + LOHSize + POHSize;
        internal long TotalFree => Gen0Free + Gen1Free + Gen2Free + LOHFree + POHFree;
        internal double TotalFragmentationPercent => CalculatePercent(TotalFree, TotalSize);

        internal HeapFragmentation(
            long gen0Free, 
            long gen0Size,
            long gen1Free,
            long gen1Size,
            long gen2Free,
            long gen2Size,
            long lohFree,
            long lohSize,
            long pohFree,
            long pohSize
        )
        {
            Gen0Free = gen0Free;
            Gen0Size = gen0Size;

            Gen1Free = gen1Free;
            Gen1Size = gen1Size;

            Gen2Free = gen2Free;
            Gen2Size = gen2Size;

            LOHFree = lohFree;
            LOHSize = lohSize;

            POHFree = pohFree;
            POHSize = pohSize;
        }

        private static double CalculatePercent(long num, long denom)
        => denom == 0 ? 0 : Math.Round((num / (double)denom) * 100.0, 1);

        public override string ToString()
        => $"{nameof(Gen0FragementationPercent)}: {Gen0FragementationPercent}%, {nameof(Gen1FragementationPercent)}: {Gen1FragementationPercent}%, {nameof(Gen2FragementationPercent)}: {Gen2FragementationPercent}%, {nameof(LOHFragementationPercent)}: {LOHFragementationPercent}%, {nameof(POHFragementationPercent)}: {POHFragementationPercent}%";

        public bool Equals(HeapFragmentation other)
        => other.Gen0Free == Gen0Free &&
           other.Gen0Size == Gen0Size &&
           other.Gen1Free == Gen1Free &&
           other.Gen1Size == Gen1Size &&
           other.Gen2Free == Gen2Free &&
           other.Gen2Size == Gen2Size &&
           other.LOHFree == LOHFree &&
           other.LOHSize == LOHSize &&
           other.POHFree == POHFree &&
           other.POHSize == POHSize;

        public override bool Equals(object? obj)
        => obj is HeapFragmentation other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(
                Gen0Free,
                Gen0Size,
                Gen1Free,
                Gen1Size,
                Gen2Free,
                Gen2Size,
                LOHFree,
                HashCode.Combine(
                    LOHSize,
                    POHFree,
                    POHSize
                )
           );

        public HeapFragmentation Read(IBufferReader<byte> reader)
        {
            var g0f = default(LongWrapper).Read(reader).Value;
            var g0s = default(LongWrapper).Read(reader).Value;

            var g1f = default(LongWrapper).Read(reader).Value;
            var g1s = default(LongWrapper).Read(reader).Value;

            var g2f = default(LongWrapper).Read(reader).Value;
            var g2s = default(LongWrapper).Read(reader).Value;

            var lf = default(LongWrapper).Read(reader).Value;
            var ls = default(LongWrapper).Read(reader).Value;

            var pf = default(LongWrapper).Read(reader).Value;
            var ps = default(LongWrapper).Read(reader).Value;

            return new HeapFragmentation(g0f, g0s, g1f, g1s, g2f, g2s, lf, ls, pf, ps);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new LongWrapper(Gen0Free).Write(writer);
            new LongWrapper(Gen0Size).Write(writer);

            new LongWrapper(Gen1Free).Write(writer);
            new LongWrapper(Gen1Size).Write(writer);

            new LongWrapper(Gen2Free).Write(writer);
            new LongWrapper(Gen2Size).Write(writer);

            new LongWrapper(LOHFree).Write(writer);
            new LongWrapper(LOHSize).Write(writer);

            new LongWrapper(POHFree).Write(writer);
            new LongWrapper(POHSize).Write(writer);
        }
    }
}
