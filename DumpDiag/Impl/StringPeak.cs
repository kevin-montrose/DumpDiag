using System;

namespace DumpDiag.Impl
{
    internal readonly struct StringPeak : IEquatable<StringPeak>
    {
        internal const int PeakLength = 128; // 128 CHARS, 256 bytes

        internal int ActualLength { get; }
        internal string PeakedString { get; }
        internal bool ReadFullString => ActualLength <= PeakLength - sizeof(int) / sizeof(char);

        internal StringPeak(int actualLength, string peaked)
        {
            ActualLength = actualLength;
            PeakedString = peaked;
        }

        public bool Equals(StringPeak other)
        => other.ActualLength == ActualLength && other.PeakedString == PeakedString;

        public override bool Equals(object? obj)
        => obj is StringPeak other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(ActualLength, PeakedString);

        public override string ToString()
        => $"{nameof(ActualLength)}: {ActualLength}, {nameof(ReadFullString)}: {ReadFullString}, {nameof(PeakedString)}: {PeakedString}";
    }
}
