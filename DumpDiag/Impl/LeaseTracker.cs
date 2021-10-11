using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal sealed class LeaseTracker<T> : IAsyncDisposable
        where T : class, IAsyncDisposable
    {
        private readonly SemaphoreSlim signal;

        private ConcurrentQueue<T> claimed;

        private int maxLeases;
        private bool disposed;

        internal LeaseTracker()
        {
            signal = new SemaphoreSlim(0);
            claimed = new ConcurrentQueue<T>();
            disposed = false;
            maxLeases = 0;
        }

        internal void SetTrackedValues(T[] values)
        {
            Debug.Assert(claimed.IsEmpty);

            for (var i = 0; i < values.Length; i++)
            {
                claimed.Enqueue(values[i]);
            }

            maxLeases = claimed.Count;
            signal.Release(maxLeases);
        }

        internal async ValueTask<V> RunWithLeasedAsync<V>(Func<T, ValueTask<V>> del, [CallerMemberName]string? caller = null)
        {
            var leased = await LeaseAsync().ConfigureAwait(false);

            V ret;
            try
            {
                ret = await del(leased).ConfigureAwait(false);
            }
            finally
            {
                Return(leased);
            }

            return ret;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask<T> LeaseAsync()
        {
            Debug.Assert(!disposed);

            await signal.WaitAsync().ConfigureAwait(false);
            if (!claimed.TryDequeue(out var toUse))
            {
                throw new Exception("Shouldn't be possible");
            }

            return toUse;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Return(T leased)
        {
            claimed.Enqueue(leased);

            signal.Release();
        }

        public async ValueTask DisposeAsync()
        {
            Debug.Assert(!disposed);
            disposed = true;

            if (claimed == null)
            {
                return;
            }

            await signal.WaitAsync(maxLeases).ConfigureAwait(false);

            for (var i = 0; i < maxLeases; i++)
            {
                if (!claimed.TryDequeue(out var toFree))
                {
                    var errorName = "Dispose failed, implies invalid state";
                    throw new InvalidOperationException(errorName);
                }

                await toFree.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
