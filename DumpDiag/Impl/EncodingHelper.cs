using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DumpDiag.Impl
{
    internal static class EncodingHelper
    {
        private sealed class HackDecoder : Decoder
        {
            internal static readonly HackDecoder Instance = new HackDecoder();

            private HackDecoder() { }

            public override unsafe void Convert(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush, out int bytesUsed, out int charsUsed, out bool completed)
            {
                // this a single byte encoding, there's no state to carry over ever
                completed = true;

                if (bytes.Length == 0)
                {
                    bytesUsed = 0;
                    charsUsed = 0;
                    return;
                }

                fixed (byte* bytePtr = bytes)
                fixed (char* charPtr = chars)
                {
                    var toUse = Math.Min(bytes.Length, chars.Length);

                    var ret = MultiByteToWideChar(bytePtr, toUse, charPtr, toUse);

                    // for code page 437, every single byte maps to a unicode codepoint <= 0xFFFF
                    // so consumed bytes == created chars
                    bytesUsed = ret;
                    charsUsed = ret;
                }
            }

            public override void Convert(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, int charCount, bool flush, out int bytesUsed, out int charsUsed, out bool completed)
            => throw new NotImplementedException("Shouldn't be used");

            public override unsafe void Convert(byte* bytes, int byteCount, char* chars, int charCount, bool flush, out int bytesUsed, out int charsUsed, out bool completed)
            => throw new NotImplementedException("Shouldn't be used");

            public override int GetCharCount(byte[] bytes, int index, int count)
            => throw new NotImplementedException("Shouldn't be used");

            public override unsafe int GetCharCount(byte* bytes, int count, bool flush)
            => throw new NotImplementedException("Shouldn't be used");

            public override int GetCharCount(byte[] bytes, int index, int count, bool flush)
            => throw new NotImplementedException("Shouldn't be used");

            public override unsafe int GetChars(byte* bytes, int byteCount, char* chars, int charCount, bool flush)
            => throw new NotImplementedException("Shouldn't be used");

            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => throw new NotImplementedException("Shouldn't be used");

            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex, bool flush)
            => throw new NotImplementedException("Shouldn't be used");

            public override int GetCharCount(ReadOnlySpan<byte> bytes, bool flush)
            => throw new NotImplementedException("Shouldn't be used");

            public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars, bool flush)
            => throw new NotImplementedException("Shouldn't be used");

            private static unsafe int MultiByteToWideChar(byte* pBytes, int byteCount, char* pChars, int count)
            {
                // default code page, basically "IBM"
                // such a hack
                const uint CODE_PAGE = 437;

                var result = MultiByteToWideChar(CODE_PAGE, 0, pBytes, byteCount, pChars, count);
                Debug.Assert(result > 0);

                return result;
            }

            [DllImport("kernel32.dll")]
            private static extern unsafe int MultiByteToWideChar(uint CodePage, uint dwFlags, byte* lpMultiByteStr, int cbMultiByte, char* lpWideCharStr, int cchWideChar);
        }

        /// <summary>
        /// Some built-in Decoders are _awful_, this helper works around that.
        /// </summary>
        internal static Decoder MakeDecoder(Encoding encoding)
        {
            if (OperatingSystem.IsWindows() && encoding.CodePage == 437)
            {
                return HackAroundBadDecoder(encoding);
            }

            return encoding.GetDecoder();
        }

        private static Decoder HackAroundBadDecoder(Encoding encoding)
        {
            // default decoder (which is used implicitly from OSEncoding) fully copies all bytes and chars
            // during count and get ops - which is terribad

            return HackDecoder.Instance;
        }
    }
}
