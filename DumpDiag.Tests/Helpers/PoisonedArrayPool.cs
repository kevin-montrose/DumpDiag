using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace DumpDiag.Tests.Helpers
{
    internal sealed class PoisonedArrayPool<T> : ArrayPool<T>
        where T : unmanaged
    {
        private static readonly ThreadLocal<Random> Random = new ThreadLocal<Random>(() => new Random());

        public override T[] Rent(int minimumLength)
        {
            var inner = ArrayPool<T>.Shared.Rent(minimumLength);

            FillWithRandom(inner);

            return inner;
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            FillWithRandom(array);

            ArrayPool<T>.Shared.Return(array, clearArray);
        }

        private static void FillWithRandom(T[] arr)
        {
            var asBytes = MemoryMarshal.AsBytes<T>(arr.AsSpan());

            var random = Random.Value;

            random.NextBytes(asBytes);
        }
    }
}
