using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DumpDiag.Impl
{
    /// <summary>
    /// An object pool optimized for cases where a thread is unlikely
    /// to take multiple objects at a time, but multiple threads will
    /// be taking and release objects at once.
    /// 
    /// Everything on this is meant to be inlined, and thus you should
    /// have VERY FEW calls to it for any single consumer type.  Typically
    /// one per method.
    /// </summary>
    internal sealed class ThreadAffinitizedObjectPool<T>
        where T : class, new()
    {
        private readonly int mask;
        private readonly T?[] pool;

        internal ThreadAffinitizedObjectPool()
        {
            // force everything to align to powers of two, so we have a fast mod option
            var twiceCores = Environment.ProcessorCount * 2;
            var nextPowerOfTwo = (int)Math.Pow(2, Math.Ceiling(Math.Log2(twiceCores)));

            mask = nextPowerOfTwo - 1;
            pool = new T[nextPowerOfTwo];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Obtain()
        {
            var offset = Thread.CurrentThread.ManagedThreadId;
            for (var i = 0; i < pool.Length; i++)
            {
                var ix = (i + offset) & mask;   // faster way to due % pool.Length

                var toReuse = Volatile.Read(ref pool[ix]);
                if (toReuse != null && Interlocked.CompareExchange(ref pool[ix], null, toReuse) == toReuse)
                {
                    return toReuse;
                }
            }

            return new T();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Return(T item)
        {
            var offset = Thread.CurrentThread.ManagedThreadId;
            for (var i = 0; i < pool.Length; i++)
            {
                var ix = (i + offset) & mask;   // faster way to due % pool.Length

                var empty = Volatile.Read(ref pool[ix]);
                if (empty == null && Interlocked.CompareExchange(ref pool[ix], item, null) == null)
                {
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Clear()
        {
            for (var i = 0; i < pool.Length; i++)
            {
                Interlocked.Exchange(ref pool[i], null);
            }
        }
    }
}
