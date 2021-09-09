using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct FieldOffset
    {
        internal ReadOnlySequence<char> Name { get; }
        internal int Offset { get; }

        internal FieldOffset(ReadOnlySequence<char> name, int offset)
        {
            Name = name;
            Offset = offset;
        }

        public override string ToString()
        => $"{Name} {Offset:X2}";
    }
}
