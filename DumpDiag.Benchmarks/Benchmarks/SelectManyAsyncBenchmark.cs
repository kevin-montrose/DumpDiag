using BenchmarkDotNet.Attributes;
using DumpDiag.Impl;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace DumpDiag.Benchmarks.Benchmarks
{
    internal static class SelectManyAsyncBenchmarkExtensions
    {
        private static readonly Task<bool> Sentinel = Task.FromException<bool>(new Exception("Sentinel tasks should not be awaited"));
        private static readonly Task<bool> CompletedTrue = Task.FromResult(true);
        private static readonly Task<bool> CompletedFalse = Task.FromResult(false);

        internal static async IAsyncEnumerable<V> SelectManySimpleAsync<T, V>(this IEnumerable<T> e, Func<T, int, IAsyncEnumerable<V>> sel)
        {
            // basic gist is:
            // first we create all the IAsyncEnumerators
            // then we kick off MoveNextAsync() for all of them
            //  - if any complete immediately, we make a note to not Task.WhenAny() later
            // for each task that has completed, we either
            //  - queue the item to be yielded
            //    * by waiting to queue, we can kick off MoveNextAsync() before yielding
            //    * this also means we can complete synchronously a bunch of times in a row, which is better for branch prediction
            //  - remove the enumerator and dispose it
            // we then loop until we have removed all the enumerators

            var asyncEnumeratorsBuilder = ImmutableList.CreateBuilder<IAsyncEnumerator<V>>();

            var ix = 0;
            foreach (var i in e)
            {
                var sub = sel(i, ix);
                asyncEnumeratorsBuilder.Add(sub.GetAsyncEnumerator());

                ix++;
            }

            var allAsyncEnumerators = asyncEnumeratorsBuilder.ToImmutable();
            try
            {
                var activeAsyncEnumerators = allAsyncEnumerators;

                var toYieldIx = 0;
                var toYield = new V[activeAsyncEnumerators.Count];

                var moveNextTasks = Array.Empty<Task<bool>>();

                while (!activeAsyncEnumerators.IsEmpty)
                {
                    ResizeMoveNextTasks(activeAsyncEnumerators, ref moveNextTasks);
                    var needsAwait = StartNewMoveNexts(activeAsyncEnumerators, moveNextTasks);

                    // yield a bunch in a row (if possible) to minimize async transitions
                    for (var i = 0; i < toYieldIx; i++)
                    {
                        yield return toYield[i];
                    }

                    // only await if no tasks are already completed
                    if (needsAwait)
                    {
                        await Task.WhenAny(moveNextTasks).ConfigureAwait(false);
                    }

                    QueueResultsForYield(ref activeAsyncEnumerators, moveNextTasks, toYield, out toYieldIx, out var error);

                    if (error)
                    {
                        // await everything, so we propogate errors
                        var toAwait = moveNextTasks.Where(static t => !object.ReferenceEquals(t, null) && !object.ReferenceEquals(t, Sentinel));
                        await Task.WhenAll(toAwait).ConfigureAwait(false);

                        yield break;
                    }
                }
            }
            finally
            {
                // dispose of everything at the end
                foreach (var toDispose in allAsyncEnumerators)
                {
                    var disposeTask = toDispose.DisposeAsync();

                    // in the common case the dispose is going to be trivial, so 
                    // elide the await most of the time
                    if (!disposeTask.IsCompletedSuccessfully)
                    {
                        await disposeTask.ConfigureAwait(false);
                    }
                }
            }

            // at least one task will have finished, so handle any that have 
            // returns the number of values to yield (the values are placed in toYield) in toYieldCount
            // any enumerators that have completed are removed from enumerators
            // and the corresponding slot in moveNextTasks replaced with Sentinel
            //
            // if a task is cancelled or faulted, sets errorState to the first such task and bails
            static void QueueResultsForYield(ref ImmutableList<IAsyncEnumerator<V>> enumerators, Task<bool>[] moveNextTasks, V[] toYield, out int toYieldCount, out bool errorState)
            {
                toYieldCount = 0;
                errorState = false;

                for (var i = enumerators.Count - 1; i >= 0; i--)
                {
                    var task = moveNextTasks[i];

                    if (task.IsCompletedSuccessfully)
                    {
                        var enumerator = enumerators[i];
                        var res = task.Result;

                        Task<bool> updatedTask;

                        if (res)
                        {
                            toYield[toYieldCount] = enumerator.Current;
                            toYieldCount++;

                            updatedTask = null;
                        }
                        else
                        {
                            enumerators = enumerators.RemoveAt(i);
                            updatedTask = Sentinel;
                        }

                        moveNextTasks[i] = updatedTask;
                    }
                    else if (task.IsFaulted || task.IsCanceled)
                    {
                        errorState = true;
                        return;
                    }
                }
            }

            // spin up new MoveNextAsync() calls if there are any empty slots in moveNextTasks
            // returns true if all the tasks in moveNextTasks are incomplete
            static bool StartNewMoveNexts(ImmutableList<IAsyncEnumerator<V>> enumerators, Task<bool>[] moveNextTasks)
            {
                var needsAwait = true;

                var ix = 0;
                foreach (var enumerator in enumerators)
                {
                    var curTask = moveNextTasks[ix];
                    if (curTask == null)
                    {
                        var moveNextTask = enumerator.MoveNextAsync();

                        if (moveNextTask.IsCompletedSuccessfully)
                        {
                            curTask = moveNextTask.Result ? CompletedTrue : CompletedFalse;
                        }
                        else
                        {
                            curTask = moveNextTask.AsTask();
                        }

                        moveNextTasks[ix] = curTask;
                    }

                    if (curTask.IsCompleted)
                    {
                        needsAwait = false;
                    }

                    ix++;
                }

                return needsAwait;
            }

            // resizes moveNextTasks so only active enumerators have slots and all slots are null or not-yet-awaited tasks
            static void ResizeMoveNextTasks(ImmutableList<IAsyncEnumerator<V>> enumerators, ref Task<bool>[] moveNextTasks)
            {
                if (moveNextTasks.Length != enumerators.Count)
                {
                    var nextIx = 0;
                    var newTasks = new Task<bool>[enumerators.Count];
                    for (var i = 0; i < moveNextTasks.Length; i++)
                    {
                        var oldTask = moveNextTasks[i];
                        if (object.ReferenceEquals(oldTask, Sentinel))  // means the corresponding enumerator completed, so toss this slot
                        {
                            continue;
                        }

                        newTasks[nextIx] = oldTask;

                        nextIx++;
                    }

                    moveNextTasks = newTasks;
                }
            }
        }

        internal static async IAsyncEnumerable<TYield> SelectManyAsyncNaive<TItem, TYield>(this IEnumerable<TItem> e, Func<TItem, int, IAsyncEnumerable<TYield>> sel)
        {
            var enumeratorIndex = 0;
            foreach (var i in e)
            {
                await foreach (var toYield in sel(i, enumeratorIndex).ConfigureAwait(false))
                {
                    yield return toYield;
                }

                enumeratorIndex++;
            }
        }
    }

    [MemoryDiagnoser]
    public class SelectManyAsyncBenchmark
    {
        [Params(1, 2, 3)]
        public int CallbackType { get; set; }

        [Benchmark]
        public void Naive()
        {
            GoAsync().GetAwaiter().GetResult();

            async ValueTask GoAsync()
            {
                var sum = 0;

                await foreach (var i in Enumerable.Range(0, Environment.ProcessorCount).SelectManyAsyncNaive(GetCallback()).ConfigureAwait(false))
                {
                    sum += i;
                }

                if (sum == 0)
                {
                    throw new Exception();
                }
            }
        }

        [Benchmark]
        public void Simple()
        {
            GoAsync().GetAwaiter().GetResult();

            async ValueTask GoAsync()
            {
                var sum = 0;

                await foreach (var i in Enumerable.Range(0, Environment.ProcessorCount).SelectManySimpleAsync(GetCallback()).ConfigureAwait(false))
                {
                    sum += i;
                }

                if (sum == 0)
                {
                    throw new Exception();
                }
            }
        }

        [Benchmark]
        public void Fancy()
        {
            GoAsync().GetAwaiter().GetResult();

            async ValueTask GoAsync()
            {
                var sum = 0;

                await foreach (var i in Enumerable.Range(0, Environment.ProcessorCount).SelectManyAsync(GetCallback()).ConfigureAwait(false))
                {
                    sum += i;
                }

                if (sum == 0)
                {
                    throw new Exception();
                }
            }
        }

        private Func<int, int, IAsyncEnumerable<int>> GetCallback()
        {
            switch (CallbackType)
            {
                case 1: return Mixed;
                case 2: return AlwaysBlocks;
                case 3: return NeverBlocks;
                default: throw new InvalidOperationException();
            }

            static async IAsyncEnumerable<int> Mixed(int val, int ix)
            {
                for (var i = 0; i < (val + 10) / 2; i++)
                {
                    var mode = (i % (ix + 1));

                    if (mode == 0)
                    {
                        await Task.Yield();
                    }
                    else if (mode == 1)
                    {
                        await Task.Delay(ix / 10).ConfigureAwait(false);
                    }

                    yield return (i + ix) * val;
                    yield return (i + ix) * val + 1;
                }
            }

            static async IAsyncEnumerable<int> AlwaysBlocks(int val, int ix)
            {
                for (var i = 0; i < (val + 10) / 2; i++)
                {
                    var mode = (i % (ix + 1));

                    if (mode == 0)
                    {
                        await Task.Yield();
                    }
                    else if (mode == 1)
                    {
                        await Task.Delay(2).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                    }

                    yield return (i + ix) * val;
                    yield return (i + ix) * val + 1;
                }
            }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
            static async IAsyncEnumerable<int> NeverBlocks(int val, int ix)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
            {
                for (var i = 0; i < (val + 10) / 2; i++)
                {
                    yield return (i + ix) * val;
                    yield return (i + ix) * val + 1;
                }
            }
        }
    }
}
