using System;
using System.Runtime.Serialization;

namespace DumpDiag.Tests.Helpers
{
    [Serializable]
    internal class InvalidOperation : Exception
    {
        public InvalidOperation()
        {
        }

        public InvalidOperation(string message) : base(message)
        {
        }

        public InvalidOperation(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected InvalidOperation(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}