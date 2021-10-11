using DumpDiag.Impl;
using System;
using System.Buffers;
using Xunit;

namespace DumpDiag.Tests
{
    public class SequenceReaderHelperTests
    {
        [Fact]
        public void TryParseInstanceFieldNoValue()
        {
            var strs = @"00007ff8e2d7b258  4000ba5       30         System.Int32  1 instance           m_taskId
00007ff8e2e31a30  4000ba6        8      System.Delegate  0 instance           m_action
00007ff8e2d70c68  4000ba7       10        System.Object  0 instance           m_stateObject
00007ff8e2f52390  4000ba8       18 ...sks.TaskScheduler  0 instance           m_taskScheduler
00007ff8e2d7b258  4000ba9       34         System.Int32  1 instance           m_stateFlags
00007ff8e2d70c68  4000baa       20        System.Object  0 instance           m_continuationObject
00007ff8e33703c0  4000bae       28 ...tingentProperties  0 instance           m_contingentProperties
00007ff8e350c258  4000b60       38 ...mpl.StringDetails  1 instance           m_result
00007ff8e2f3df38  40010dd       48        System.Action  0 instance           _moveNextAction
00007ff8e483c238  40010de       58 ...tails, DumpDiag]]  1 instance           StateMachine
00007ff8e2f518b0  40010df       50 ....ExecutionContext  0 instance           Context";

            foreach(var line in strs.Split("\n"))
            {
                var lineRef = line.Trim();
                var seq = new ReadOnlySequence<char>(lineRef.AsMemory());

                Assert.True(SequenceReaderHelper.TryParseInstanceFieldNoValue(seq, ArrayPool<char>.Shared, out _), $"for: {lineRef}");
            }
        }

        [Fact]
        public void TryParseInstanceFieldWithValue()
        {
            var strs = @"00007ff8e2ed70e8  4000c16       18 ...CancellationToken  1 instance 00000230b242f9f8 m_defaultCancellationToken
00007ff8e2f52390  4000c17        8 ...sks.TaskScheduler  0 instance 0000000000000000 m_defaultScheduler
00007ff8e2f50738  4000c18       10         System.Int32  1 instance                0 m_defaultCreationOptions
00007ff8e2f525b8  4000c19       14         System.Int32  1 instance                0 m_defaultContinuationOptions
00007ff8e2f525b8  4000c19       18 ...tails, DumpDiag]]  1 instance 0123456789ABCDEF m_dummy";

            foreach (var line in strs.Split("\n"))
            {
                var lineRef = line.Trim();
                var seq = new ReadOnlySequence<char>(lineRef.AsMemory());

                Assert.True(SequenceReaderHelper.TryParseInstanceFieldWithValue(seq, ArrayPool<char>.Shared, out _), $"for: {lineRef}");
            }
        }
    }
}
