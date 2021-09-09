using DumpDiag.Impl;
using DumpDiag.Tests.Helpers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;

namespace DumpDiag.Tests
{
    public class ProcessStreamReaderTests : IDisposable
    {
        private static ArrayPool<char>[] CharArrayPools = new[] { ArrayPool<char>.Shared, new PoisonedArrayPool<char>(), new LeakTrackingArrayPool<char>(ArrayPool<char>.Shared), new LeakTrackingArrayPool<char>(new PoisonedArrayPool<char>()) };

        private static Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>>[] Enumerators = new Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>>[] { FreePrompt, FreeLazy, FreeRandom };

        private static string[] NewLines = new[] { "\r\n", "\r", "\n" };

        public static IEnumerable<object[]> PoolAndNewLineParameters
        {
            get
            {
                var ret = new List<object[]>();
                foreach (var c in CharArrayPools)
                {
                    foreach (var nl in NewLines)
                    {
                        ret.Add(new object[] { c, nl });
                    }
                }

                return ret;
            }
        }

        public static IEnumerable<object[]> EnumeratorAndPoolAndNewLineParameters
        {
            get
            {
                var ret = new List<object[]>();
                foreach (var enumerator in Enumerators)
                {
                    foreach (var c in CharArrayPools)
                    {
                        foreach (var nl in NewLines)
                        {
                            ret.Add(new object[] { enumerator, c, nl });
                        }
                    }
                }

                return ret;
            }
        }

        public static IEnumerable<object[]> EnumeratorAndPoolAndNewLineAndReadSizeParameters
        {
            get
            {
                var ret = new List<object[]>();
                foreach (var size in TestableReadSizes.ReadSizes)
                {
                    foreach (var pools in EnumeratorAndPoolAndNewLineParameters)
                    {
                        ret.Add(pools.Append(size).ToArray());
                    }
                }

                return ret;
            }
        }

        private static IEnumerable<OwnedSequence<char>> FreePrompt(ProcessStreamReader reader)
        {
            foreach (var line in reader.ReadAllLines())
            {
                try
                {
                    yield return line;
                }
                finally
                {
                    line.Dispose();
                }
            }
        }

        private static IEnumerable<OwnedSequence<char>> FreeLazy(ProcessStreamReader reader)
        {
            var pending = new List<OwnedSequence<char>>();

            try
            {
                foreach (var line in reader.ReadAllLines())
                {
                    pending.Add(line);

                    yield return line;
                }
            }
            finally
            {
                foreach (var toFree in pending)
                {
                    toFree.Dispose();
                }
            }
        }

        private static IEnumerable<OwnedSequence<char>> FreeRandom(ProcessStreamReader reader)
        {
            var rand = new Random();

            var pending = new List<OwnedSequence<char>>();

            try
            {
                foreach (var line in reader.ReadAllLines())
                {
                    try
                    {
                        yield return line;
                    }
                    finally
                    {

                        var r = rand.Next(2);
                        if (r == 0)
                        {
                            line.Dispose();
                        }
                        else
                        {
                            pending.Add(line);
                        }
                    }
                }
            }
            finally
            {
                foreach (var toFree in pending)
                {
                    toFree.Dispose();
                }
            }
        }

        public void Dispose()
        {
            // after each test, check that we haven't leaked anything
            foreach (var pool in CharArrayPools.OfType<LeakTrackingArrayPool<char>>())
            {
                pool.AssertEmpty();
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineParameters))]
        public void Simple(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                using var mem = new MemoryStream();
                {
                    using var writer = new StreamWriter(mem, encoding);
                    writer.Write("hello");
                    writer.Write(newLine);
                    writer.Write("world");
                    writer.Write(newLine);
                }

                var bytes = mem.ToArray();

                using var mem2 = new MemoryStream(bytes);

                using var stream = new ProcessStreamReader(charPool, mem2, encoding, newLine);

                var lineIx = 0;
                foreach (var line in enumerate(stream))
                {
                    Assert.True(lineIx <= 1);

                    switch (lineIx)
                    {
                        case 0: Assert.Equal("hello", line.ToString()); break;
                        case 1: Assert.Equal("world", line.ToString()); break;
                    }


                    lineIx++;
                }

                Assert.Equal(2, lineIx);
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineParameters))]
        public void Large(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {

                var mediumStringBuilder = new StringBuilder();
                for (var i = 0; i < (1024 * 4 - 4) + 1; i++)
                {
                    var c = (char)('A' + (i % 26));
                    mediumStringBuilder.Append(c);
                }
                var mediumString = mediumStringBuilder.ToString();

                var bigStringBuilder = new StringBuilder();
                for (var i = 0; i < 16 * 1024; i++)
                {
                    var c = (char)('0' + (i % 10));
                    bigStringBuilder.Append(c);
                }
                var bigString = bigStringBuilder.ToString();

                using var mem = new MemoryStream();
                {
                    using var writer = new StreamWriter(mem, encoding);
                    writer.Write("hello");
                    writer.Write(newLine);

                    writer.Write(mediumString);
                    writer.Write(newLine);

                    writer.Write("world");
                    writer.Write(newLine);

                    writer.Write(bigString);
                    writer.Write(newLine);
                }

                var bytes = mem.ToArray();

                using var mem2 = new MemoryStream(bytes);

                using var stream = new ProcessStreamReader(charPool, mem2, encoding, newLine);

                var lineIx = 0;
                foreach (var line in enumerate(stream))
                {
                    Assert.True(lineIx <= 3);

                    switch (lineIx)
                    {
                        case 0: Assert.Equal("hello", line.ToString()); break;
                        case 1: Assert.Equal(mediumString, line.ToString()); break;
                        case 2: Assert.Equal("world", line.ToString()); break;
                        case 3: Assert.Equal(bigString, line.ToString()); break;
                    }

                    lineIx++;
                }

                Assert.Equal(4, lineIx);
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineParameters))]
        public void Empty(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                using var stream = new ProcessStreamReader(charPool, new MemoryStream(Array.Empty<byte>()), encoding, newLine);

                var ix = 0;
                foreach (var line in enumerate(stream))
                {
                    ix++;
                }

                Assert.Equal(0, ix);
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineParameters))]
        public void NoEndingLine(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                // multiple lines, but last line has no new line
                {
                    using var mem = new MemoryStream();
                    {
                        using var writer = new StreamWriter(mem, encoding);
                        writer.Write("hello");
                        writer.Write(newLine);

                        writer.Write("world");
                    }

                    var bytes = mem.ToArray();

                    using var mem2 = new MemoryStream(bytes);

                    using var stream = new ProcessStreamReader(charPool, mem2, encoding, newLine);

                    var lineIx = 0;
                    foreach (var line in enumerate(stream))
                    {
                        Assert.True(lineIx <= 1);

                        switch (lineIx)
                        {
                            case 0: Assert.Equal("hello", line.ToString()); break;
                            case 1: Assert.Equal("world", line.ToString()); break;
                        }

                        lineIx++;
                    }

                    Assert.Equal(2, lineIx);
                }

                // single line, no ending
                {
                    using var mem = new MemoryStream();
                    {
                        using var writer = new StreamWriter(mem, encoding);
                        writer.Write("hello");
                    }

                    var bytes = mem.ToArray();

                    using var mem2 = new MemoryStream(bytes);

                    using var stream = new ProcessStreamReader(charPool, mem2, encoding, newLine);

                    var lineIx = 0;
                    foreach (var line in enumerate(stream))
                    {
                        Assert.True(lineIx == 0);

                        switch (lineIx)
                        {
                            case 0: Assert.Equal("hello", line.ToString()); break;
                        }

                        lineIx++;
                    }

                    Assert.Equal(1, lineIx);
                }
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineParameters))]
        public void ExactlyFillCharBuffer(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                // two lines that perfectly fill each buffer
                {
                    var line1Builder = new StringBuilder();
                    while (line1Builder.Length < ProcessStreamReader.CharBufferSize - newLine.Length - sizeof(int) / sizeof(char))
                    {
                        var c = (char)('A' + (line1Builder.Length % 26));
                        line1Builder.Append(c);
                    }
                    var line1 = line1Builder.ToString();

                    var line2Builder = new StringBuilder();
                    while (line2Builder.Length < ProcessStreamReader.CharBufferSize - newLine.Length - sizeof(int) / sizeof(char))
                    {
                        var c = (char)('0' + (line1Builder.Length % 10));
                        line2Builder.Append(c);
                    }
                    var line2 = line2Builder.ToString();

                    using var mem = new MemoryStream();
                    {
                        using var writer = new StreamWriter(mem, encoding);
                        writer.Write(line1);
                        writer.Write(newLine);

                        writer.Write(line2);
                        writer.Write(newLine);
                    }

                    var bytes = mem.ToArray();

                    using var mem2 = new MemoryStream(bytes);

                    using var stream = new ProcessStreamReader(charPool, mem2, encoding, newLine);

                    var lineIx = 0;
                    foreach (var line in enumerate(stream))
                    {
                        Assert.True(lineIx <= 1);

                        switch (lineIx)
                        {
                            case 0: Assert.Equal(line1, line.ToString()); break;
                            case 1: Assert.Equal(line2, line.ToString()); break;
                        }

                        lineIx++;
                    }

                    Assert.Equal(2, lineIx);
                }

                // one line with no ending that perfectly fills the buffer twice
                {
                    var lineBuilder = new StringBuilder();
                    while (lineBuilder.Length < (ProcessStreamReader.CharBufferSize - sizeof(int) / sizeof(char)) * 2)
                    {
                        var c = (char)('A' + (lineBuilder.Length % 26));
                        lineBuilder.Append(c);
                    }
                    var line = lineBuilder.ToString();

                    using var mem = new MemoryStream();
                    {
                        using var writer = new StreamWriter(mem, encoding);
                        writer.Write(line);
                    }

                    var bytes = mem.ToArray();

                    using var mem2 = new MemoryStream(bytes);

                    using var stream = new ProcessStreamReader(charPool, mem2, encoding, newLine);

                    var lineIx = 0;
                    foreach (var readLine in enumerate(stream))
                    {
                        Assert.True(lineIx <= 1);

                        switch (lineIx)
                        {
                            case 0: Assert.Equal(line, readLine.ToString()); break;
                        }

                        lineIx++;
                    }

                    Assert.Equal(1, lineIx);
                }
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineAndReadSizeParameters))]
        public void Staggered(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine, int readSize)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                var mediumStringBuilder = new StringBuilder();
                for (var i = 0; i < (1024 * 4 - 4) + 1; i++)
                {
                    var c = (char)('A' + (i % 26));
                    mediumStringBuilder.Append(c);
                }
                var mediumString = mediumStringBuilder.ToString();

                var bigStringBuilder = new StringBuilder();
                for (var i = 0; i < 16 * 1024; i++)
                {
                    var c = (char)('0' + (i % 10));
                    bigStringBuilder.Append(c);
                }
                var bigString = bigStringBuilder.ToString();

                using var mem = new MemoryStream();
                {
                    using var writer = new StreamWriter(mem, encoding);
                    writer.Write("hello");
                    writer.Write(newLine);

                    writer.Write(mediumString);
                    writer.Write(newLine);

                    writer.Write("world");
                    writer.Write(newLine);

                    writer.Write(bigString);
                    writer.Write(newLine);
                }

                var bytes = mem.ToArray();

                using var mem2 = new MemoryStream(bytes);
                using var staggered = new FixedStrideStream(readSize, mem2);

                using var stream = new ProcessStreamReader(charPool, staggered, encoding, newLine);

                var lineIx = 0;
                foreach (var line in enumerate(stream))
                {
                    Assert.True(lineIx <= 3);

                    switch (lineIx)
                    {
                        case 0: Assert.Equal("hello", line.ToString()); break;
                        case 1: Assert.Equal(mediumString, line.ToString()); break;
                        case 2: Assert.Equal("world", line.ToString()); break;
                        case 3: Assert.Equal(bigString, line.ToString()); break;
                    }

                    lineIx++;
                }

                Assert.Equal(4, lineIx);
            }
        }

        [Theory]
        [MemberData(nameof(EnumeratorAndPoolAndNewLineAndReadSizeParameters))]
        public void NotFullyEnumerated(Func<ProcessStreamReader, IEnumerable<OwnedSequence<char>>> enumerate, ArrayPool<char> charPool, string newLine, int readSize)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                var mediumStringBuilder = new StringBuilder();
                for (var i = 0; i < (1024 * 4 - 4) + 1; i++)
                {
                    var c = (char)('A' + (i % 26));
                    mediumStringBuilder.Append(c);
                }
                var mediumString = mediumStringBuilder.ToString();

                var bigStringBuilder = new StringBuilder();
                for (var i = 0; i < 16 * 1024; i++)
                {
                    var c = (char)('0' + (i % 10));
                    bigStringBuilder.Append(c);
                }
                var bigString = bigStringBuilder.ToString();

                using var mem = new MemoryStream();
                {
                    using var writer = new StreamWriter(mem, encoding);
                    writer.Write("hello");
                    writer.Write(newLine);

                    writer.Write(mediumString);
                    writer.Write(newLine);

                    writer.Write("world");
                    writer.Write(newLine);

                    writer.Write(bigString);
                    writer.Write(newLine);
                }

                var bytes = mem.ToArray();

                using var mem2 = new MemoryStream(bytes);
                using var staggered = new FixedStrideStream(readSize, mem2);

                using var stream = new ProcessStreamReader(charPool, staggered, encoding, newLine);

                var lineIx = 0;
                foreach (var line in enumerate(stream))
                {
                    switch (lineIx)
                    {
                        case 0: Assert.Equal("hello", line.ToString()); break;
                        case 1: Assert.Equal(mediumString, line.ToString()); break;
                    }

                    lineIx++;
                    if (lineIx == 2)
                    {
                        break;
                    }
                }

                Assert.Equal(2, lineIx);
            }
        }

        [Theory]
        [MemberData(nameof(PoolAndNewLineParameters))]
        public void SplitAcrossSegments(ArrayPool<char> arrayPool, string newLine)
        {
            foreach (var encoding in TestableEncodings.Encodings)
            {
                if (newLine.Length == 1)
                {
                    return;
                }

                var builder = new StringBuilder();
                builder.Append("hello world" + newLine);

                var nextChar = 0;
                while (builder.Length + (sizeof(int) / sizeof(char)) + newLine.Length - 1 < ProcessStreamReader.CharBufferSize)
                {
                    var c = (char)('A' + (nextChar % 52));

                    builder.Append(c);

                    nextChar++;
                }

                builder.Append(newLine);
                builder.Append("foo bar");

                var expectedText = builder.ToString();
                var bytes = encoding.GetBytes(expectedText);

                using var mem = new MemoryStream(bytes);

                using var stream = new ProcessStreamReader(arrayPool, mem, encoding, newLine);

                var disposeLater = new List<IDisposable>();
                try
                {
                    var lines = new List<string>();

                    foreach (var line in stream.ReadAllLines())
                    {
                        var lineText = line.ToString();

                        Assert.DoesNotContain(newLine, lineText);

                        lines.Add(lineText);
                        disposeLater.Add(line);
                    }

                    var expectedLines = expectedText.Split(newLine);
                    Assert.Equal(expectedLines, lines);
                }
                finally
                {
                    foreach (var d in disposeLater)
                    {
                        d.Dispose();
                    }
                }
            }
        }
    }
}
