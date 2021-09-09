using DumpDiag.Impl;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace DumpDiag.Tests
{
    public class AsyncLinqTests
    {
        [Fact]
        public async Task SelectManyAsync()
        {
            // all block
            {
                var e = Enumerable.Range(0, 10);

                var expected = new List<string>();
                var ix = 0;
                foreach (var i in e)
                {
                    await foreach (var item in AsyncEnumeratorAsync(i, ix).ConfigureAwait(false))
                    {
                        expected.Add(item);
                    }
                    ix++;
                }

                var actual = new List<string>();
                await foreach (var item in e.SelectManyAsync(AsyncEnumeratorAsync).ConfigureAwait(false))
                {
                    actual.Add(item);
                }

                var expectedInOrder = expected.OrderBy(x => x).ToList();
                var actualInOrder = actual.OrderBy(x => x).ToList();

                Assert.Equal(expectedInOrder, actualInOrder);

                static async IAsyncEnumerable<string> AsyncEnumeratorAsync(int val, int ix)
                {
                    var ret = val.ToString();

                    for (var i = 0; i < 10 + val; i++)
                    {
                        await Task.Delay(10 + val);

                        yield return ret;

                        ret += "." + i + "_" + ix;
                    }
                }
            }

            // no blocking
            {
                var e = Enumerable.Range(0, 10);

                var expected = new List<string>();
                var ix = 0;
                foreach (var i in e)
                {
                    await foreach (var item in AsyncEnumeratorAsync(i, ix).ConfigureAwait(false))
                    {
                        expected.Add(item);
                    }
                    ix++;
                }

                var actual = new List<string>();
                await foreach (var item in e.SelectManyAsync(AsyncEnumeratorAsync).ConfigureAwait(false))
                {
                    actual.Add(item);
                }

                var expectedInOrder = expected.OrderBy(x => x).ToList();
                var actualInOrder = actual.OrderBy(x => x).ToList();

                Assert.Equal(expectedInOrder, actualInOrder);

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
                static async IAsyncEnumerable<string> AsyncEnumeratorAsync(int val, int ix)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
                {
                    var ret = val.ToString();

                    for (var i = 0; i < 10 + val; i++)
                    {
                        yield return ret;

                        ret += "." + i + "_" + ix;
                    }
                }
            }

            // random-ish
            {
                var e = Enumerable.Range(0, 10);

                var expected = new List<string>();
                var ix = 0;
                foreach (var i in e)
                {
                    await foreach (var item in AsyncEnumeratorAsync(i, ix).ConfigureAwait(false))
                    {
                        expected.Add(item);
                    }
                    ix++;
                }

                var actual = new List<string>();
                await foreach (var item in e.SelectManyAsync(AsyncEnumeratorAsync).ConfigureAwait(false))
                {
                    actual.Add(item);
                }

                var expectedInOrder = expected.OrderBy(x => x).ToList();
                var actualInOrder = actual.OrderBy(x => x).ToList();

                Assert.Equal(expectedInOrder, actualInOrder);

                static async IAsyncEnumerable<string> AsyncEnumeratorAsync(int val, int ix)
                {
                    var ret = val.ToString();

                    for (var i = 0; i < 10 + val; i++)
                    {
                        if ((i % 2) == 0)
                        {
                            await Task.Delay(i + 1).ConfigureAwait(false);
                        }

                        yield return ret;

                        ret += "." + i + "_" + ix;
                    }
                }
            }
        }
    }
}
