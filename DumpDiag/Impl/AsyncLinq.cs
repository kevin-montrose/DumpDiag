using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal static class AsyncLinq
    {
        internal sealed class SelectManyAsyncEnumerable<TItem, TYield> : IAsyncEnumerable<TYield>
        {
            private enum Mode: byte
            {
                FirstCall,
                EnterLoop,
                TryYieldResult,
                MoveCompletedAsyncResults,
                WaitForProgress,
                DisposeEnumerators,
            }

            internal readonly struct AsyncEnumerator : IAsyncEnumerator<TYield>
            {
                private readonly SelectManyAsyncEnumerable<TItem, TYield> inner;

                public TYield Current => inner.current;

                internal AsyncEnumerator(SelectManyAsyncEnumerable<TItem, TYield> inner)
                {
                    this.inner = inner;
                }

                public ValueTask<bool> MoveNextAsync()
                => inner.MoveNextAsync();

                public ValueTask DisposeAsync()
                => default;
            }

            private const byte TASK_NEEDED = 0;
            private const byte TASK_RUNNING = 1;
            private const byte TASK_COMPLETE_ASYNC_TRUE = 2;
            private const byte TASK_COMPLETE_ASYNC_FALSE = 3;
            private const byte ENUMERATOR_COMPLETE = 4;

            private readonly IEnumerable<TItem> enumerable;
            private readonly Func<TItem, int, IAsyncEnumerable<TYield>> selector;

            private bool enumeratorCreated;

            // enumerator state
            private Mode mode;
            private TYield current;

            private ReadOnlyMemory<IAsyncEnumerator<TYield>> enumerators;
            private int completedEnumerators;
            private byte[] states;
            private Action[] callbacks;
            private ValueTaskAwaiter<bool>[] awaiters;
            private SemaphoreSlim signal;
            private bool indulgence;
            private TYield[] toYield;
            private int toYieldIndex;
            private bool madeProgress;

            internal SelectManyAsyncEnumerable(IEnumerable<TItem> e, Func<TItem, int, IAsyncEnumerable<TYield>> sel)
            {
                enumerable = e;
                selector = sel;

                mode = Mode.FirstCall;
                current = default;
            }

            public AsyncEnumerator GetAsyncEnumerator(CancellationToken cancellationToken)
            {
                Debug.Assert(!cancellationToken.CanBeCanceled);
                Debug.Assert(!enumeratorCreated);

                enumeratorCreated = true;

                return new AsyncEnumerator(this);
            }

            IAsyncEnumerator<TYield> IAsyncEnumerable<TYield>.GetAsyncEnumerator(CancellationToken cancellationToken)
            => GetAsyncEnumerator(cancellationToken);

            private ValueTask<bool> MoveNextAsync()
            {
                return RunStateMachine(this);

                // entry point for the state machine
                static ValueTask<bool> RunStateMachine(SelectManyAsyncEnumerable<TItem, TYield> self)
                {
                    while (true)
                    {
                        switch (self.mode)
                        {
                            case Mode.FirstCall:
                                FirstCall(self);
                                break;

                            case Mode.EnterLoop:
                                EnterLoop(self);
                                break;

                            case Mode.TryYieldResult:
                                if (TryYieldResult(self))
                                {
                                    return new ValueTask<bool>(true);
                                }
                                break;

                            case Mode.MoveCompletedAsyncResults:
                                MoveCompletedAsyncResults(self);
                                break;

                            case Mode.WaitForProgress:
                                var waitTask = self.signal.WaitAsync();
                                if (waitTask.IsCompletedSuccessfully)
                                {
                                    self.indulgence = true;
                                    self.mode = Mode.EnterLoop;
                                    break;
                                }

                                return GoAsync(waitTask, self);

                            case Mode.DisposeEnumerators:
                                return DisposeEnumeratorsAsync(self);

                            default:
                                throw new InvalidOperationException($"Unexpected mode: {self.mode}");
                        }
                    }

                    // evaluate the rest of this call asynchronously
                    static async ValueTask<bool> GoAsync(Task waitFor, SelectManyAsyncEnumerable<TItem, TYield> self)
                    {
                        await waitFor.ConfigureAwait(false);
                        self.indulgence = true;
                        self.mode = Mode.EnterLoop;

                        return await RunStateMachine(self).ConfigureAwait(false);
                    }
                }

                // clean everything up, and return false
                static ValueTask<bool> DisposeEnumeratorsAsync(SelectManyAsyncEnumerable<TItem, TYield> self)
                {
                    var enumeratorSpan = self.enumerators.Span;
                    for(var i = 0; i < enumeratorSpan.Length; i++)
                    {
                        var disposeTask = enumeratorSpan[i].DisposeAsync();
                        if(!disposeTask.IsCompletedSuccessfully)
                        {
                            return GoAsync(disposeTask, self, i);
                        }
                    }

                    return new ValueTask<bool>(false);

                    // make disposal go async
                    static async ValueTask<bool> GoAsync(ValueTask waitFor, SelectManyAsyncEnumerable<TItem, TYield> self, int i)
                    {
                        await waitFor.ConfigureAwait(false);

                        for(var j = i+1; j < self.enumerators.Length; j++)
                        {
                            await self.enumerators.Span[j].DisposeAsync().ConfigureAwait(false);
                        }

                        return false;
                    }
                }

                // move completed tasks over to later yield
                static void MoveCompletedAsyncResults(SelectManyAsyncEnumerable<TItem, TYield> self)
                {
                    MoveCompletedResults(
                        self.enumerators.Span,
                        self.signal,
                        self.states,
                        ref self.toYield,
                        ref self.toYieldIndex,
                        ref self.completedEnumerators,
                        ref self.indulgence,
                        ref self.madeProgress
                    );

                    if(!self.madeProgress)
                    {
                        self.mode = Mode.WaitForProgress;
                    }
                    else if(self.completedEnumerators != self.enumerators.Length)
                    {
                        self.mode = Mode.EnterLoop;
                    }
                    else
                    {
                        self.mode = Mode.DisposeEnumerators;
                    }

                    // handle any completed tasks that completed asynchronously, moving them into the next available slot in toYield
                    // also resets any state so we can spin up a new MoveNextAsync() task
                    static void MoveCompletedResults(
                        ReadOnlySpan<IAsyncEnumerator<TYield>> enumerators,
                        SemaphoreSlim signal,
                        byte[] states,
                        ref TYield[] toYield,
                        ref int toYieldIndex,
                        ref int completedEnumerators,
                        ref bool indulgence,
                        ref bool madeProgress)
                    {
                        while (indulgence || (signal?.Wait(0) ?? false))    // Wait(0) is try and wait, but don't block
                        {
                            indulgence = false;
                            madeProgress = true;

                            // one pulse, means one task completed 
                            for (var enumeratorIndex = 0; enumeratorIndex < states.Length; enumeratorIndex++)
                            {
                                var enumeratorState = states[enumeratorIndex];

                                if (enumeratorState == TASK_COMPLETE_ASYNC_TRUE)
                                {
                                    madeProgress = true;

                                    RecordToYield(enumerators[enumeratorIndex].Current, ref toYield, ref toYieldIndex);

                                    states[enumeratorIndex] = TASK_NEEDED;

                                }
                                else if (enumeratorState == TASK_COMPLETE_ASYNC_FALSE)
                                {
                                    madeProgress = true;

                                    states[enumeratorIndex] = ENUMERATOR_COMPLETE;
                                    completedEnumerators++;
                                }
                            }
                        }
                    }
                }

                // yield some of the results back
                static bool TryYieldResult(SelectManyAsyncEnumerable<TItem, TYield> self)
                {
                    if (self.toYieldIndex != 0)
                    {
                        // we yield these backwards so we don't have to track extra state
                        self.toYieldIndex--;
                        self.current = self.toYield[self.toYieldIndex];

                        self.mode = self.toYieldIndex == 0 ? Mode.MoveCompletedAsyncResults : Mode.TryYieldResult;
                        return true;
                    }

                    self.mode = Mode.MoveCompletedAsyncResults;
                    return false;
                }

                // start the while loop
                static void EnterLoop(SelectManyAsyncEnumerable<TItem, TYield> self)
                {
                    self.madeProgress = false;

                    RefreshMoveNexts(self.enumerators.Span, ref self.signal, self.states, ref self.callbacks, ref self.awaiters, ref self.toYield, ref self.toYieldIndex, ref self.madeProgress, ref self.completedEnumerators);

                    self.mode = Mode.TryYieldResult;

                    // spins up new MoveNextAsync() tasks anywhere there's a slot
                    // may also allocate callbacks and save awaiters if any tasks don't
                    // complete synchronously
                    static void RefreshMoveNexts(
                        ReadOnlySpan<IAsyncEnumerator<TYield>> enumerators,
                        ref SemaphoreSlim signal,
                        byte[] states,
                        ref Action[] completionCallbacks,
                        ref ValueTaskAwaiter<bool>[] awaiters,
                        ref TYield[] toYield,
                        ref int toYieldIndex,
                        ref bool madeProgress,
                        ref int completedEnumerators
                    )
                    {
                        for (var i = 0; i < enumerators.Length; i++)
                        {
                            if (states == null || states[i] == TASK_NEEDED)
                            {
                                var enumerator = enumerators[i];
                                var moveNextTask = enumerator.MoveNextAsync();
                                if (moveNextTask.IsCompletedSuccessfully)
                                {
                                    madeProgress = true;

                                    if (moveNextTask.Result)
                                    {
                                        RecordToYield(enumerator.Current, ref toYield, ref toYieldIndex);

                                        states[i] = TASK_NEEDED;
                                    }
                                    else
                                    {
                                        states[i] = ENUMERATOR_COMPLETE;
                                        completedEnumerators++;
                                    }
                                }
                                else
                                {
                                    if (signal == null)
                                    {
                                        signal = new SemaphoreSlim(0);
                                    }

                                    var callback = GetOrCreateCallback(signal, states, enumerators.Length, i, ref completionCallbacks, ref awaiters);

                                    states[i] = TASK_RUNNING;
                                    awaiters[i] = moveNextTask.GetAwaiter();
                                    awaiters[i].OnCompleted(callback);
                                }
                            }
                        }
                    }



                    // grabs a callback (or creates one if needed) that updates
                    // states as appropriate if registered with an awaiter 
                    static Action GetOrCreateCallback(SemaphoreSlim signal, byte[] states, int numEnumerators, int enumeratorIndex, ref Action[] callbacks, ref ValueTaskAwaiter<bool>[] awaiters)
                    {
                        if (callbacks == null)
                        {
                            callbacks = new Action[numEnumerators];
                            awaiters = new ValueTaskAwaiter<bool>[numEnumerators];
                        }

                        var exists = callbacks[enumeratorIndex];
                        if (exists == null)
                        {
                            var statesCopy = states;
                            var awaitersCopy = awaiters;
                            Action callback =
                                () =>
                                {
                                    var awaiter = awaitersCopy[enumeratorIndex];

                            // we're making a hard assumption that tasks don't fail and aren't cancelled
                            var res = awaiter.GetResult();

                                    statesCopy[enumeratorIndex] = res ? TASK_COMPLETE_ASYNC_TRUE : TASK_COMPLETE_ASYNC_FALSE;

                                    signal.Release();
                                };
                            callbacks[enumeratorIndex] = exists = callback;
                        }

                        return exists;
                    }
                }

                // spin up initial state
                static void FirstCall(SelectManyAsyncEnumerable<TItem, TYield> self)
                {
                    self.enumerators = CreateEnumerators(self.enumerable, self.selector);

                    self.completedEnumerators = 0;
                    self.states = new byte[self.enumerators.Length];

                    // we only allocate these if we actually have to wait for a task to complete
                    self.callbacks = null;
                    self.awaiters = null;
                    self.signal = null;

                    // if we block using signal, we need to track that we "consumed" a release already
                    self.indulgence = false;

                    self.toYieldIndex = 0;
                    self.toYield = null;

                    if (self.enumerators.Length != self.completedEnumerators)
                    {
                        // there's work to do, loop!
                        self.mode = Mode.EnterLoop;
                    }
                    else
                    {
                        // otherwise there's nothing to do, bail!
                        self.mode = Mode.DisposeEnumerators;
                    }

                    // grab enumerators
                    static ReadOnlyMemory<IAsyncEnumerator<TYield>> CreateEnumerators(IEnumerable<TItem> e, Func<TItem, int, IAsyncEnumerable<TYield>> sel)
                    {
                        if (e is ICollection<TItem> c)
                        {
                            var ret = new IAsyncEnumerator<TYield>[c.Count];
                            var ix = 0;
                            foreach (var i in e)
                            {
                                ret[ix] = sel(i, ix).GetAsyncEnumerator();
                                ix++;
                            }

                            return ret;
                        }
                        else
                        {
                            var oversized = new IAsyncEnumerator<TYield>[1];
                            var ix = 0;
                            foreach (var i in e)
                            {
                                if (ix == oversized.Length)
                                {
                                    Array.Resize(ref oversized, oversized.Length * 2);
                                }

                                oversized[ix] = sel(i, ix).GetAsyncEnumerator();
                                ix++;
                            }

                            return oversized.AsMemory().Slice(0, ix);
                        }
                    }
                }

                // record an item to yield
                static void RecordToYield(TYield item, ref TYield[] toYield, ref int toYieldIndex)
                {
                    if (toYield == null)
                    {
                        toYield = new TYield[1];
                    }
                    else if (toYield.Length == toYieldIndex)
                    {
                        Array.Resize(ref toYield, toYield.Length * 2);
                    }

                    toYield[toYieldIndex] = item;
                    toYieldIndex++;
                }
            }
        }

        /// <summary>
        /// An async equivalent of SelectMany.
        /// 
        /// Meaningful differences:
        ///  - yields elements in arbitrary order
        ///  - can only be enumerated once
        ///  - assumes enumerables cannot be cancelled or faulted
        /// </summary>
        internal static SelectManyAsyncEnumerable<TItem, TYield> SelectManyAsync<TItem, TYield>(this IEnumerable<TItem> e, Func<TItem, int, IAsyncEnumerable<TYield>> sel)
        => new SelectManyAsyncEnumerable<TItem, TYield>(e, sel);

        internal static async ValueTask<ImmutableDictionary<TKey, TValue>> GroupByToImmutableDictionaryAsync<TItem, TKey, TValue>(
            this IAsyncEnumerable<TItem> e,
            Func<TItem, TKey> groupBy,
            TValue seed,
            Func<TItem, TValue, TValue> update
        )
        {
            var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();

            await foreach (var item in e.ConfigureAwait(false))
            {
                var key = groupBy(item);
                if (!builder.TryGetValue(key, out var curVal))
                {
                    curVal = seed;
                }

                builder[key] = update(item, curVal);
            }

            return builder.ToImmutable();
        }

        internal static async ValueTask<ImmutableList<T>> ToImmutableListAsync<T>(this IAsyncEnumerable<T> e)
        {
            var ret = ImmutableList.CreateBuilder<T>();

            await foreach(var i in e.ConfigureAwait(false))
            {
                ret.Add(i);
            }

            return ret.ToImmutable();
        }
    }
}
