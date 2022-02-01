using DumpDiag.Impl;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class LeaseTrackerTests
    {
        private sealed class Node : IAsyncDisposable ,IHasCommandCount
        {
            private int activeReaders;

            private readonly int _value;
            internal int Value
            {
                get
                {
                    Assert.Equal(1, Interlocked.Increment(ref activeReaders));

                    var ret = _value;

                    Assert.Equal(0, Interlocked.Decrement(ref activeReaders));

                    return ret;
                }
            }


            private int _disposed;
            internal bool Disposed => Volatile.Read(ref _disposed) != 0;

            public ulong TotalExecutedCommands { get; } = 0;

            internal Node(int v)
            {
                _value = v;
                _disposed = 0;
            }

            public ValueTask DisposeAsync()
            {
                var res = Interlocked.Exchange(ref _disposed, 1);

                if (res != 0)
                {
                    throw new Exception("Double disposed");
                }

                return default;
            }

            public override string ToString()
            => $"{Value:N0} - {_disposed}";
        }

        [Fact]
        public async Task SimpleAsync()
        {
            var toOwn = Enumerable.Range(0, 16).Select(t => new Node(t)).ToArray();

            var tracker = new LeaseTracker<Node>();
            tracker.SetTrackedValues(toOwn.ToArray());

            await using (tracker.ConfigureAwait(false))
            {
                using var signal = new SemaphoreSlim(0);

                var allTasks = new ValueTask<int>[toOwn.Length];

                for (var i = 0; i < toOwn.Length; i++)
                {
                    var copyI = i;
                    var task =
                        tracker.RunWithLeasedAsync(
                            async node =>
                            {
                                Assert.Equal(copyI, node.Value);
                                Assert.False(node.Disposed);

                                await signal.WaitAsync().ConfigureAwait(false);

                                return node.Value;
                            }
                        );
                    allTasks[i] = task;
                }

                Assert.All(allTasks, t => Assert.False(t.IsCompleted));

                signal.Release(toOwn.Length);

                var res = await allTasks.WhenAll().ConfigureAwait(false);

                for(var i = 0; i < 16; i++)
                {
                    Assert.Contains(i, res);
                }
            }

            Assert.All(toOwn, t => Assert.True(t.Disposed));
        }
    }
}
