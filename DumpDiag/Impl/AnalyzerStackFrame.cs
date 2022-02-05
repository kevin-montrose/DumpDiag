using System;
using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct AnalyzerStackFrame : IEquatable<AnalyzerStackFrame>, IDiagnosisSerializable<AnalyzerStackFrame>
    {
        internal long ChildStackPointer { get; }
        internal long InstructionPointer { get; }
        internal string CallSite { get; }

        internal AnalyzerStackFrame(long cp, long ip, string cs)
        {
            ChildStackPointer = cp;
            InstructionPointer = ip;
            CallSite = cs;
        }

        public override string ToString()
        => $"{ChildStackPointer:X2} {InstructionPointer:X2} {CallSite}";

        public bool Equals(AnalyzerStackFrame other)
        => ChildStackPointer == other.ChildStackPointer && InstructionPointer == other.InstructionPointer;  // don't include CallSite, because it different debuggers include different amounts of detail about generics

        public override bool Equals(object? obj)
        => obj is AnalyzerStackFrame other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(ChildStackPointer, InstructionPointer); // don't include CallSite, because it different debuggers include different amounts of detail about generics

        public AnalyzerStackFrame Read(IBufferReader<byte> reader)
        {
            var csp = default(LongWrapper).Read(reader).Value;
            var ip = default(LongWrapper).Read(reader).Value;
            var cs = default(StringWrapper).Read(reader).Value;

            return new AnalyzerStackFrame(csp, ip, cs);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            new LongWrapper(ChildStackPointer).Write(writer);
            new LongWrapper(InstructionPointer).Write(writer);
            new StringWrapper(CallSite).Write(writer);
        }
    }
}
