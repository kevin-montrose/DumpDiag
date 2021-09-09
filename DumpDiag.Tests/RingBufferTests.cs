using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace DumpDiag.Tests
{
    public class RingBufferTests
    {
        public static IEnumerable<object[]> ReadSizeParameters
        {
            get
            {
                var ret = new List<object[]>();
                foreach (var size in TestableReadSizes.ReadSizes)
                {
                    ret.Add(new object[] { size });
                }

                return ret;
            }
        }

        [Fact]
        public void Simple()
        {
            Span<char> converted = stackalloc char[6];

            foreach (var encoding in TestableEncodings.Encodings)
            {
                using var mem = new MemoryStream(encoding.GetBytes("hello"));

                var decoder = EncodingHelper.MakeDecoder(encoding);

                RingBuffer buffer = default;
                RingBuffer.Intialize(ref buffer);

                Assert.True(buffer.Read(mem));

                buffer.Convert(decoder, converted, true, out var charsConsumed, out var finished);
                Assert.Equal(5, charsConsumed);
                Assert.True(finished);

                Assert.Equal("hello", new string(converted[0..5]));
            }
        }

        [Fact]
        public void Large()
        {
            Span<char> converted = stackalloc char[64];

            foreach (var encoding in TestableEncodings.Encodings)
            {
                var lineBuilder = new StringBuilder();
                while (lineBuilder.Length < 8 * 1024)
                {
                    var c = (char)('A' + (lineBuilder.Length % 26));
                    lineBuilder.Append(c);
                }
                var line = lineBuilder.ToString();

                using var mem = new MemoryStream(encoding.GetBytes(line));

                var decoder = EncodingHelper.MakeDecoder(encoding);

                RingBuffer buffer = default;
                RingBuffer.Intialize(ref buffer);

                var readLineBuilder = new StringBuilder();

                var canReadMore = true;
                while (canReadMore)
                {
                    canReadMore = buffer.Read(mem);

                    // handle everything in the buffer
                    while (buffer.HasData && buffer.HasData)
                    {
                        buffer.Convert(decoder, converted, false, out var convertedChars, out var finished);
                        var toAppend = converted[0..convertedChars];
                        readLineBuilder.Append(toAppend);
                        Assert.False(finished);
                    }
                }

                // handle anything still lingering in the decoder
                var completed = false;
                while (!completed)
                {
                    buffer.Convert(decoder, converted, true, out var leftOver, out completed);

                    if (leftOver > 0)
                    {
                        var toAppend = converted[0..leftOver];
                        readLineBuilder.Append(toAppend);
                    }
                }
                Assert.True(completed);

                var readLine = readLineBuilder.ToString();

                Assert.Equal(line, readLine);
            }
        }

        [Theory]
        [MemberData(nameof(ReadSizeParameters))]
        public void InterleavedConvertAndReads(int readSize)
        {
            Span<char> converted = stackalloc char[readSize];

            foreach (var encoding in TestableEncodings.Encodings)
            {
                var lineBuilder = new StringBuilder();
                while (lineBuilder.Length < 8 * 1024)
                {
                    var c = (char)('A' + (lineBuilder.Length % 26));
                    lineBuilder.Append(c);
                }
                var line = lineBuilder.ToString();

                using var mem = new MemoryStream(encoding.GetBytes(line));

                var decoder = EncodingHelper.MakeDecoder(encoding);

                RingBuffer buffer = default;
                RingBuffer.Intialize(ref buffer);

                var readLineBuilder = new StringBuilder();

                var canReadMore = true;
                while (canReadMore)
                {
                    canReadMore = buffer.Read(mem);

                    // rather than consuming _all_ available data, we consume some so there's space to read
                    if (buffer.HasData && buffer.HasData)
                    {
                        buffer.Convert(decoder, converted, false, out var convertedChars, out var finished);
                        var toAppend = converted[0..convertedChars];
                        readLineBuilder.Append(toAppend);
                        Assert.False(finished);
                    }
                }

                // now we need to drain anything left since we didn't guarantee that we consumed all available data
                while (buffer.HasData && buffer.HasData)
                {
                    buffer.Convert(decoder, converted, false, out var convertedChars, out var finished);
                    var toAppend = converted[0..convertedChars];
                    readLineBuilder.Append(toAppend);
                    Assert.False(finished);
                }

                // and handle converting anything still in the decoder
                var completed = false;
                while (!completed)
                {
                    buffer.Convert(decoder, converted, true, out var leftOver, out completed);

                    if (leftOver > 0)
                    {
                        var toAppend = converted[0..leftOver];
                        readLineBuilder.Append(toAppend);
                    }
                }
                Assert.True(completed);

                var readLine = readLineBuilder.ToString();

                Assert.Equal(line, readLine);
            }
        }
    }
}
