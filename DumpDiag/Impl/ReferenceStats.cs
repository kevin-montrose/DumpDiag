namespace DumpDiag.Impl
{
    internal readonly struct ReferenceStats
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
    }
}
