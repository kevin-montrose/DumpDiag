using DumpDiag.Impl;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class BoundedSharedChannelTests : IDisposable
    {
        // crank this up when making changes to better stress test the concurrent logic
        private const int ITERATION_COUNT = 1;

        private sealed class Node
        {
            public int Foo { get; }

            private Node _next;
            public Node Next
            {
                get => Volatile.Read(ref _next);
                set => Interlocked.Exchange(ref _next, value);
            }

            public Node() { }

            public Node(int val)
            {
                Foo = val;
            }

            public override string ToString()
            => Foo.ToString();
        }

        public void Dispose()
        {
            BoundedSharedChannel<Node>.ClearReusableCache();
        }

        [Fact]
        public async Task SimpleAsync()
        {
            const int TOTAL = 30;

            for (var i = 0; i < ITERATION_COUNT; i++)
            {
                var list = BoundedSharedChannel<Node>.Create();

                foreach (var x in Enumerable.Range(1, TOTAL))
                {
                    list.Append(new Node(x));
                }
                list.Complete();

                var read = new List<Node>();
                await foreach (var n in list.ReadUntilCompletedAsync().ConfigureAwait(false))
                {
                    read.Add(n);
                }

                Assert.True(Enumerable.Range(1, TOTAL).SequenceEqual(read.Select(t => t.Foo)));
            }
        }

        [Fact]
        public async Task ConcurrentEnumerableAsync()
        {
            const int NUM_NODES = 100_000;
            const int OFFSET = 123;

            for (var i = 0; i < ITERATION_COUNT; i++)
            {
                // enumerator will free when fully enumerated
                var list = BoundedSharedChannel<Node>.Create();

                var read = new List<Node>();

                var startReadSignal = new SemaphoreSlim(0);
                var readerTask = ReadAllAsync(startReadSignal, read, list);

                var startWriteSignal = new SemaphoreSlim(0);
                var writerTask = WriteAllAsync(startWriteSignal, list);

                var both = Task.WhenAll(readerTask, writerTask).ConfigureAwait(false);

                startReadSignal.Release();
                startWriteSignal.Release();

                await both;

                for (var x = 0; x < NUM_NODES; x++)
                {
                    Assert.Equal(x + OFFSET, read[x].Foo);
                }
            }

            static async Task ReadAllAsync(SemaphoreSlim startSignal, List<Node> into, BoundedSharedChannel<Node> list)
            {
                await startSignal.WaitAsync().ConfigureAwait(false);

                await foreach (var n in list.ReadUntilCompletedAsync().ConfigureAwait(false))
                {
                    into.Add(n);
                }
            }

            static async Task WriteAllAsync(SemaphoreSlim startSignal, BoundedSharedChannel<Node> list)
            {
                await startSignal.WaitAsync().ConfigureAwait(false);

                for (var i = 0; i < NUM_NODES; i++)
                {
                    var n = new Node(i + OFFSET);
                    list.Append(n);
                }

                list.Complete();
            }
        }
    }
}
