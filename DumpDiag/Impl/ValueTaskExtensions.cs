using System.Collections.Immutable;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    internal static class ValueTaskExtensions
    {
        internal static async ValueTask WhenAll(this ValueTask[] tasks)
        {
            foreach(var task in tasks)
            {
                await task.ConfigureAwait(false);
            }
        }

        internal static async ValueTask<ImmutableArray<T>> WhenAll<T>(this ValueTask<T>[] tasks)
        {
            var builder = ImmutableArray.CreateBuilder<T>(tasks.Length);

            foreach(var task in tasks)
            {
                builder.Add(await task.ConfigureAwait(false));
            }

            return builder.ToImmutable();
        }
    }
}
