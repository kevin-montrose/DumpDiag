using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DumpDiag.Tests.Helpers
{
    internal sealed class FixedStrideStream : Stream
    {
        private readonly int stride;
        private readonly Stream inner;

        internal FixedStrideStream(int stride, Stream inner)
        {
            this.stride = stride;
            this.inner = inner;
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush()
        => inner.Flush();

        public override int Read(Span<byte> buffer)
        {
            var sliced = buffer;
            if(sliced.Length > stride)
            {
                sliced = sliced.Slice(0, stride);
            }

            return inner.Read(sliced);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
        }
    }
}
