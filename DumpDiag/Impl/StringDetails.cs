using System.Buffers;

namespace DumpDiag.Impl
{
    /// <summary>
    /// String is special, we need extra details for various purposes.
    /// </summary>
    internal readonly struct StringDetails: IDiagnosisSerializable<StringDetails>
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

        public StringDetails Read(IBufferReader<byte> reader)
        {
            var m = default(AddressWrapper).Read(reader).Value;
            var l = default(IntWrapper).Read(reader).Value;
            var f = default(IntWrapper).Read(reader).Value;

            return new StringDetails(m, l, f);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new AddressWrapper(MethodTable).Write(writer);
            new IntWrapper(LengthOffset).Write(writer);
            new IntWrapper(FirstCharOffset).Write(writer);
        }
    }
}
