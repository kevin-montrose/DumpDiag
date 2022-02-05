using System.Buffers;

namespace DumpDiag.Impl
{
    internal readonly struct InstanceFieldWithTypeDetails : IDiagnosisSerializable<InstanceFieldWithTypeDetails>
    {
        internal TypeDetails TypeDetails { get; }
        internal InstanceField InstanceField { get; }

        internal InstanceFieldWithTypeDetails(TypeDetails type, InstanceField instanceField)
        {
            TypeDetails = type;
            InstanceField = instanceField;
        }

        public override string ToString()
        => $"{TypeDetails} {InstanceField}";

        public InstanceFieldWithTypeDetails Read(IBufferReader<byte> reader)
        {
            var td = default(TypeDetails).Read(reader);
            var i = default(InstanceField).Read(reader);

            return new InstanceFieldWithTypeDetails(td, i);
        }

        public void Write(IBufferWriter<byte> writer)
        {
            TypeDetails.Write(writer);
            InstanceField.Write(writer);
        }
    }
}
