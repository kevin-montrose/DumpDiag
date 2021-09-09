namespace DumpDiag.Impl
{
    /// <summary>
    /// String is special, we need extra details for various purposes.
    /// </summary>
    internal readonly struct StringDetails
    {
        internal long MethodTable { get; }
        internal int LengthOffset { get; }
        internal int FirstCharOffset { get; }

        internal StringDetails(long mt, int lengthOffset, int firstCharOffset)
        {
            MethodTable = mt;
            LengthOffset = lengthOffset;
            FirstCharOffset = firstCharOffset;
        }

        public override string ToString()
        => $"{MethodTable:X2} {LengthOffset:X2} {FirstCharOffset:X2}";
    }
}
