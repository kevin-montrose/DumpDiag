using System;
using System.Buffers;
using Xunit;
using DumpDiag.Impl;
using System.Collections.Generic;

namespace DumpDiag.Tests
{
    public class ReadOnlySequenceExtensionsTests
    {
        private sealed class Segment : ReadOnlySequenceSegment<char>
        {
            internal Segment(ReadOnlyMemory<char> mem, long runningIndex, ReadOnlySequenceSegment<char> next)
            {
                this.Memory = mem;
                this.Next = next;
                this.RunningIndex = runningIndex;
            }
        }

        [Theory]
        [InlineData("> ", "abcd", false)]
        [InlineData("> ", "a", false)]
        [InlineData("> ", ">", false)]
        [InlineData("> ", "> ", true)]
        [InlineData("> ", "> dc -w 1 -c 1 0x0000", true)]
        [InlineData("> ", ">!", false)]
        [InlineData("> ", ">>>>>>>", false)]
        public void StartsWith(string needle, string haystack, bool expectedValue)
        {
            foreach(var seq in MakeSequencesFrom(haystack))
            {
                Assert.Equal(expectedValue, seq.StartsWith(needle.AsSpan(), StringComparison.Ordinal));
            }
        }

        [Theory]
        [InlineData("a", "", false)]
        [InlineData("a", "a", true)]
        [InlineData("a", "b", false)]
        [InlineData("a", "aa", false)]
        [InlineData("hello", "", false)]
        [InlineData("hello", "hello", true)]
        [InlineData("hello", "world", false)]
        public void EqualsSpan(string a, string b, bool expectedValue)
        {
            foreach (var seq in MakeSequencesFrom(a))
            {
                Assert.Equal(expectedValue, seq.Equals(b.AsSpan(), StringComparison.Ordinal));
            }

            foreach (var seq in MakeSequencesFrom(b))
            {
                Assert.Equal(expectedValue, seq.Equals(a.AsSpan(), StringComparison.Ordinal));
            }
        }

        private static IEnumerable<ReadOnlySequence<char>> MakeSequencesFrom(string text)
        {
            var singleSegment = new ReadOnlySequence<char>(text.AsMemory());

            yield return singleSegment;

            // two segments
            for (var i = 1; i < text.Length; i++)
            {
                var left = text.AsMemory()[0..i];
                var right = text.AsMemory()[i..];

                var rightSeg = new Segment(right, left.Length, null);
                var leftSeg = new Segment(left, 0, rightSeg);

                var seq = new ReadOnlySequence<char>(leftSeg, 0, rightSeg, right.Length);

                yield return seq;
            }

            // three segments
            for (var i = 1; i < text.Length; i++)
            {
                for (var j = i + 1; j < text.Length; j++)
                {
                    var left = text.AsMemory()[0..i];
                    var middle = text.AsMemory()[i..j];
                    var right = text.AsMemory()[j..];

                    var rightSeg = new Segment(right, middle.Length + left.Length, null);
                    var middleSeg = new Segment(middle, left.Length, rightSeg);
                    var leftSeg = new Segment(left, 0, middleSeg);

                    var seq = new ReadOnlySequence<char>(leftSeg, 0, rightSeg, right.Length);

                    yield return seq;
                }
            }
        }
    }
}
