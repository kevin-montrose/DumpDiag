using System;
using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct ReferenceStats : IEquatable<ReferenceStats>, IComparable<ReferenceStats>, IDiagnosisSerializable<ReferenceStats>
    {
        internal int Live { get; }
        internal long LiveBytes { get; }
        internal int Dead { get; }
        internal long DeadBytes { get; }

        internal ReferenceStats(int liveCount, long liveBytes, int deadCount, long deadBytes)
        {
            Live = liveCount;
            LiveBytes = liveBytes;
            Dead = deadCount;
            DeadBytes = deadBytes;
        }

        public override string ToString()
        => $"{Live + Dead:N0} ({LiveBytes + DeadBytes:N0} bytes) references: {Live:N0} live ({LiveBytes:N0} bytes), {Dead:N0} dead ({DeadBytes:N0} bytes)";

        public bool Equals(ReferenceStats other)
        => other.Live == Live &&
           other.LiveBytes == LiveBytes &&
           other.Dead == Dead &&
           other.DeadBytes == DeadBytes;

        public override bool Equals(object? obj)
        => obj is ReferenceStats other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(Live, LiveBytes, Dead, DeadBytes);

        public int CompareTo(ReferenceStats other)
        {
            var ret = other.Live.CompareTo(Live);
            if (ret != 0) return ret;

            ret = other.Dead.CompareTo(Dead);
            if (ret != 0) return ret;

            ret = other.LiveBytes.CompareTo(LiveBytes);
            if (ret != 0) return ret;

            ret = other.DeadBytes.CompareTo(DeadBytes);
            if (ret != 0) return ret;

            return 0;
        }

        public ReferenceStats Read(IBufferReader<byte> reader)
        {
            var l = default(IntWrapper).Read(reader).Value;
            var lb = default(LongWrapper).Read(reader).Value;
            var d = default(IntWrapper).Read(reader).Value;
            var db = default(LongWrapper).Read(reader).Value;

            return new ReferenceStats(l, lb, d, db);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new IntWrapper(Live).Write(writer);
            new LongWrapper(LiveBytes).Write(writer);
            new IntWrapper(Dead).Write(writer);
            new LongWrapper(DeadBytes).Write(writer);
        }
    }
}
