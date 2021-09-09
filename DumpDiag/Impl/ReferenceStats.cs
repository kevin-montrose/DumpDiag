namespace DumpDiag.Impl
{
    internal readonly struct ReferenceStats
    {
        internal int Live { get; }
        internal int Dead { get; }

        internal ReferenceStats(int liveCount, int deadCount)
        {
            Live = liveCount;
            Dead = deadCount;
        }

        public override string ToString()
        => $"{Live + Dead:N0} references: {Live:N0} live, {Dead:N0} dead";
    }
}
