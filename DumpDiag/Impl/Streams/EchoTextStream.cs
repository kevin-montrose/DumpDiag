using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Debug helper that keeps track of (roughly) everything that passes through a stream.
    /// 
    /// Should not be instantiated in Release builds.
    /// </summary>
    internal sealed class EchoTextStream : Stream
    {
        private readonly StringBuilder echo;
        private readonly Encoding encoding;

        private readonly Stream inner;

        internal EchoTextStream(Stream inner, Encoding encoding)
        {
            this.inner = inner;
            this.encoding = encoding;

            echo = new StringBuilder();

#if !DEBUG
            throw new InvalidOperationException($"{nameof(EchoTextStream)} should not be created in non-DEBUG builds");
#endif
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
            const int MAX_KEEP = 128 * 1024;

            var read = inner.Read(buffer);
            var toConvert = buffer[0..read];

            echo.Append(encoding.GetString(toConvert));

            if(echo.Length > MAX_KEEP)
            {
                echo.Remove(0, echo.Length - MAX_KEEP);
            }

            return read;
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

        public override ValueTask DisposeAsync()
        => inner.DisposeAsync();

        public override string ToString()
        => echo.ToString();
    }
}
