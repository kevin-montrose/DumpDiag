using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Represents a sequence of bytes pulled from some series of arrays.
    /// 
    /// The underlying arrays are used to track ownership, so once all the yielded
    /// OwnedSequence's are freed the underlying array is also freed.
    /// </summary>
    public sealed class OwnedSequence<T> : ReadOnlySequenceSegment<T>, IDisposable
        where T : unmanaged
    {
#pragma warning disable CS0618 // We're internal, we're fine
        internal static readonly OwnedSequence<T> Empty = new OwnedSequence<T>();
#pragma warning restore CS0618

        private static readonly int BytesPerT = MemoryMarshal.AsBytes<T>(new T[1]).Length;

        private static readonly ThreadAffinitizedObjectPool<OwnedSequence<T>> ObjectPool = new ThreadAffinitizedObjectPool<OwnedSequence<T>>();

        private ArrayPool<T> pool;
        private T[]? root;

        [Obsolete("Do not use directly, only for internal use")]
        public OwnedSequence()
        {
            pool = ArrayPool<T>.Shared;
            root = null;

            Next = null;
            RunningIndex = 0;
            Memory = ReadOnlyMemory<T>.Empty;
        }

#pragma warning disable CS0618
        private OwnedSequence(ArrayPool<T> pool, T[] root, ReadOnlyMemory<T> part) : this()
#pragma warning restore CS0618
        {
            Initialize(pool, root, part);
        }

        private void Initialize(ArrayPool<T> pool, T[] root, ReadOnlyMemory<T> part)
        {
            Debug.Assert(part.Length > 0);

            this.pool = pool;
            this.root = root;

            Next = null;
            RunningIndex = 0;
            Memory = part;
        }

        /// <summary>
        /// Set the next <see cref="OwnedSequence{T}"/> that follows this one.
        /// </summary>
        internal void SetNext(OwnedSequence<T> next)
        {
            Debug.Assert(Next == null, $"Next was {Next}");
            Next = next;

            next.RunningIndex = RunningIndex + Memory.Length;
        }

        /// <summary>
        /// In the case where we discover a new line was partially recorded into a piece of memory,
        /// we have this to cut those erroneous characters off.
        /// </summary>
        internal void TruncateMemoryTail(int by)
        {
            Memory = Memory[0..^(by)];
        }

        /// <summary>
        /// Setup an array to be ref counted.
        /// 
        /// Note that this sets the initial ref count to 1, as the array is held by _something_.
        /// 
        /// The caller should pair this with a call to <see cref="DecrRefCount(T[])"/> accordingly.
        /// </summary>
        internal static Memory<T> InitRefCount(T[] root)
        {
            var countSpan = MemoryMarshal.Cast<T, int>(root.AsSpan());

            countSpan[0] = 1;

            var intInTs = sizeof(int) / BytesPerT;
            intInTs = Math.Max(intInTs, 1);         // always need to skip at least one entry, no matter what

            return root.AsMemory().Slice(intInTs);
        }

        /// <summary>
        /// Mark an array as being owned by incrementing it's ref count.
        /// </summary>
        internal static void IncrRefCount(T[] root)
        {
            if (root == null)
            {
                return;
            }

            var countSpan = MemoryMarshal.Cast<T, int>(root.AsSpan());

            Interlocked.Increment(ref countSpan[0]);
        }

        /// <summary>
        /// Only used for debugging purposes.
        /// </summary>
        private static int GetRefCount(T[]? root)
        {
            if (root == null)
            {
                return int.MaxValue;
            }

            var countSpan = MemoryMarshal.Cast<T, int>(root.AsSpan());

            return countSpan[0];
        }

        /// <summary>
        /// Removes a reference count.
        /// 
        /// Returns true if the underlying array has 0 outstanding references.
        /// </summary>
        internal static bool DecrRefCount(
            [NotNullWhen(returnValue:true)]
            T[]? root
        )
        {
            if (root == null)
            {
                return false;
            }

            var countSpan = MemoryMarshal.Cast<T, int>(root.AsSpan());

            var newCount = Interlocked.Decrement(ref countSpan[0]);

            Debug.Assert(newCount >= 0);

            return newCount == 0;
        }

        internal ReadOnlySequence<T> GetSequence()
        {
            if (root == null)
            {
                return ReadOnlySequence<T>.Empty;
            }

            if (Next == null)
            {
                return new ReadOnlySequence<T>(this.Memory);
            }

            ReadOnlySequenceSegment<T> last = this;
            while (last.Next != null)
            {
                last = last.Next;
            }

            return new ReadOnlySequence<T>(this, 0, last, last.Memory.Length);
        }

        /// <summary>
        /// Create an <see cref="OwnedSequence{T}"/> which points to the given subset of the given array.
        /// </summary>
        internal static OwnedSequence<T> Create(ArrayPool<T> pool, T[] root, ReadOnlyMemory<T> part)
        {
            Debug.Assert(MemoryMarshal.TryGetArray(part, out var underlying) && ReferenceEquals(underlying.Array, root));
            Debug.Assert(GetRefCount(root) > 0);

            // claim root so it won't be freed while this instance exists
            IncrRefCount(root);

            var ret = ObjectPool.Obtain();

            ret.Initialize(pool, root, part);
            return ret;
        }

        public void Dispose()
        {
            Debug.Assert(GetRefCount(root) > 0);

            if (root == null)
            {
                return;
            }

            (this.Next as IDisposable)?.Dispose();

            var needsFreed = DecrRefCount(this.root);
            if (needsFreed)
            {
                this.pool.Return(this.root);
            }

            ObjectPool.Return(this);
        }

        public override string ToString()
        {
            var ret = new StringBuilder();
            ReadOnlySequenceSegment<T>? cur = this;
            while (cur != null)
            {
                ret.AppendJoin("", cur.Memory.ToArray());
                cur = cur.Next;
            }

            return ret.ToString();
        }
    }
}
