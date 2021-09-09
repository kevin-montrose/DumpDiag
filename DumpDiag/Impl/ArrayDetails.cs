namespace DumpDiag.Impl
{
    internal readonly struct ArrayDetails
    {
        internal long? FirstElementAddress { get; }
        internal int Length { get; }

        internal ArrayDetails(long? addr, int len)
        {
            FirstElementAddress = addr;
            Length = len;
        }

        public override string ToString()
        => $"{(FirstElementAddress != null ? FirstElementAddress.Value.ToString("X2") : "--none--")} {Length:N0}";
    }
}
