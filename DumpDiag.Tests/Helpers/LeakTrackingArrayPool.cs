using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DumpDiag.Tests.Helpers
{
    internal sealed class LeakTrackingArrayPool<T> : ArrayPool<T>
    {
        private int Outstanding = 0;

        internal readonly ArrayPool<T> inner;

        private readonly Dictionary<T[], string> callerInfo;

        internal LeakTrackingArrayPool(ArrayPool<T> inner)
        {
            this.inner = inner;
            callerInfo = new Dictionary<T[], string>();
        }

        public override T[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref Outstanding);
            var ret = inner.Rent(minimumLength);
            var stack = new StackTrace();
            string caller;
            if(stack.FrameCount > 1)
            {
                var frame = stack.GetFrame(1);
                caller = frame.GetFileName() + " " + frame.GetMethod().Name;
            }
            else
            {
                caller = "--UNKNOWN--";
            }

            lock (callerInfo)
            {
                callerInfo[ret] = caller;                
            }

            return ret;
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            var newVal = Interlocked.Decrement(ref Outstanding);
            if (newVal < 0)
            {
                throw new InvalidOperation("A double free has occurred");
            }

            lock (callerInfo)
            {
                if (!callerInfo.Remove(array))
                {
                    throw new InvalidOperation("Freed array with wrong pool");
                }
            }

            inner.Return(array, clearArray);
        }

        public void AssertEmpty()
        {
            if (Outstanding != 0)
            {
                throw new InvalidOperation("There are still outstanding rented arrays");
            }
        }
    }
}
