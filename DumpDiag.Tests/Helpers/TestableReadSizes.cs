using System.Collections.Generic;

namespace DumpDiag.Tests.Helpers
{
    internal static class TestableReadSizes
    {

        internal static IEnumerable<int> ReadSizes =
            new int[]
            { 
                // convenient powers of 2
                1, 2, 4, 8, 16, 32, 64,
                // some primes to align things oddly
                3, 5, 7, 11,
            };
    }
}
