using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal sealed class BoundedSharedChannel<T>
        where T : class
    {
        internal readonly struct AsyncEnumerable : IAsyncEnumerable<T>
        {
            private readonly BoundedSharedChannel<T> inner;

            internal AsyncEnumerable(BoundedSharedChannel<T> inner)
            {
                this.inner = inner;
            }

            public AsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken)
            {
                Debug.Assert(!cancellationToken.CanBeCanceled);

                return new AsyncEnumerator(inner);
            }

            IAsyncEnumerator<T> IAsyncEnumerable<T>.GetAsyncEnumerator(CancellationToken cancellationToken)
            => GetAsyncEnumerator(cancellationToken);
        }

        internal struct AsyncEnumerator : IAsyncEnumerator<T>
        {
            private readonly BoundedSharedChannel<T> inner;

            public T Current
            {
                get
                {
                    var ret = inner.enumeratorCurrent;

                    if (ret == null)
                    {
                        throw new InvalidOperationException("Enumerator in invalid state");
                    }

                    return ret;
                }
            }

            internal AsyncEnumerator(BoundedSharedChannel<T> inner)
            {
                this.inner = inner;
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                await inner.signal.WaitAsync().ConfigureAwait(false);

                var res = inner.inner.TryDequeue(out var cur);
                Debug.Assert(res);

                if (cur == null)
                {
                    return false;
                }

                inner.enumeratorCurrent = cur;
                return true;
            }

            public ValueTask DisposeAsync()
            {
                Debug.Assert(inner.inner.IsEmpty);

                ObjectPool.Return(inner);
                return default;
            }
        }

        private static readonly ThreadAffinitizedObjectPool<BoundedSharedChannel<T>> ObjectPool = new ThreadAffinitizedObjectPool<BoundedSharedChannel<T>>();

        private readonly SemaphoreSlim signal;
        private readonly ConcurrentQueue<T?> inner;

        // we only support one reader, so we can re-use this allocation for tracking our enumerator's
        // Current value - AND make all the enumerator and enumerable's structs accordingly
        private T? enumeratorCurrent;

        [Obsolete("Do not use directly, meant for internal object pooling")]
        public BoundedSharedChannel()
        {
            this.signal = new SemaphoreSlim(0);
            this.inner = new ConcurrentQueue<T?>();
        }

        internal AsyncEnumerable ReadUntilCompletedAsync()
        => new AsyncEnumerable(this);

        internal void Append(T value)
        {
            Debug.Assert(value != null);

            AppendInternal(value);
        }

        private void AppendInternal(T? value)
        {
            inner.Enqueue(value);
            signal.Release();
        }

        internal void Complete()
        => AppendInternal(null);

        internal static void ClearReusableCache()
        => ObjectPool.Clear();

        internal static BoundedSharedChannel<T> Create()
        {
            var ret = ObjectPool.Obtain();

            return ret;
        }
    }
}