using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal sealed class LeaseTracker<T> : IAsyncDisposable, IHasCommandCount
        where T : class, IAsyncDisposable, IHasCommandCount
    {
        public ulong TotalExecutedCommands
        {
            get
            {
                var ret = 0UL;

                if (allValues != null)
                {
                    foreach (var val in allValues)
                    {
                        ret += val.TotalExecutedCommands;
                    }
                }

                return ret;
            }
        }

        private readonly SemaphoreSlim signal;

        private ConcurrentQueue<T> claimed;

        private int maxLeases;
        private bool disposed;

        private T[]? allValues;

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

            allValues = values;

            for (var i = 0; i < allValues.Length; i++)
            {
                claimed.Enqueue(values[i]);
            }

            maxLeases = claimed.Count;
            signal.Release(maxLeases);
        }

        internal async ValueTask<V> RunWithLeasedAsync<V>(Func<T, ValueTask<V>> del)
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
