using System;
using System.Buffers;
using System.Globalization;

namespace DumpDiag.Impl
{
    internal static class ReadOnlySequenceExtensions
    {
        internal static bool Equals(this ReadOnlySequence<char> sequence, ReadOnlySpan<char> to, StringComparison comparison)
        {
            if (sequence.IsSingleSegment)
            {
                // special case the common case
                var segment = sequence.FirstSpan;
                return segment.Equals(to, comparison);
            }

            var remaining = to;
            foreach (var part in sequence)
            {
                if (part.IsEmpty)
                {
                    continue;
                }

                if (remaining.IsEmpty)
                {
                    // found the whole sequence, but there's more left in the sequence... so it's not equal
                    return false;
                }

                var partSpan = part.Span;

                if (!remaining.StartsWith(partSpan, comparison))
                {
                    // doesn't have the part we're expecting, not equal
                    return false;
                }

                remaining = remaining[partSpan.Length..];
            }

            // if the sequence is over AND we found all of remaining, then it IS equal
            return remaining.IsEmpty;
        }

        internal static bool StartsWith(this ReadOnlySequence<char> sequence, ReadOnlySpan<char> with, StringComparison comparison)
        {
            if (sequence.IsSingleSegment)
            {
                // special case the common case
                var segment = sequence.FirstSpan;
                return segment.StartsWith(with, comparison);
            }

            var remaining = with;
            foreach (var part in sequence)
            {
                if (part.IsEmpty)
                {
                    continue;
                }

                if (remaining.IsEmpty)
                {
                    // last bit we looked at consumed all of remaining, so we're gold
                    return true;
                }

                var partSpan = part.Span;
                if (partSpan.Length > remaining.Length)
                {
                    return partSpan.StartsWith(remaining, comparison);
                }
                else
                {
                    var remainingToCheck = remaining[0..partSpan.Length];

                    if (!remainingToCheck.Equals(partSpan, comparison))
                    {
                        return false;
                    }

                    remaining = remaining[remainingToCheck.Length..];
                }
            }

            return remaining.IsEmpty;
        }

        internal static bool TryParseHexLong(this ReadOnlySequence<char> sequence, out long value)
        {
            if (sequence.IsSingleSegment)
            {
                var segment = sequence.FirstSpan;
                return long.TryParse(segment, NumberStyles.HexNumber, null, out value);
            }

            var len = sequence.Length;
            if (len > 16)
            {
                value = default;
                return false;
            }

            Span<char> longSpan = stackalloc char[(int)len];
            sequence.CopyTo(longSpan);

            return long.TryParse(longSpan, NumberStyles.HexNumber, null, out value);
        }

        internal static bool TryParseHexInt(this ReadOnlySequence<char> sequence, out int value)
        {
            if (sequence.IsSingleSegment)
            {
                var segment = sequence.FirstSpan;
                return int.TryParse(segment, NumberStyles.HexNumber, null, out value);
            }

            var len = sequence.Length;
            if (len > 8)
            {
                value = default;
                return false;
            }

            Span<char> longSpan = stackalloc char[(int)len];
            sequence.CopyTo(longSpan);

            return int.TryParse(longSpan, NumberStyles.HexNumber, null, out value);
        }

        internal static bool TryParseHexShort(this ReadOnlySequence<char> sequence, out short value)
        {
            if (sequence.IsSingleSegment)
            {
                var segment = sequence.FirstSpan;
                return short.TryParse(segment, NumberStyles.HexNumber, null, out value);
            }

            var len = sequence.Length;
            if (len > 8)
            {
                value = default;
                return false;
            }

            Span<char> longSpan = stackalloc char[(int)len];
            sequence.CopyTo(longSpan);

            return short.TryParse(longSpan, NumberStyles.HexNumber, null, out value);
        }

        internal static bool TryParseDecimalInt(this ReadOnlySequence<char> sequence, out int value)
        {
            if (sequence.IsSingleSegment)
            {
                var segment = sequence.FirstSpan;
                return int.TryParse(segment, out value);
            }

            var len = sequence.Length;
            if (len > 10)
            {
                value = default;
                return false;
            }

            Span<char> intSpan = stackalloc char[(int)len];
            sequence.CopyTo(intSpan);

            return int.TryParse(intSpan, out value);
        }

        internal static bool TryParseDecimalLong(this ReadOnlySequence<char> sequence, out long value)
        {
            if (sequence.IsSingleSegment)
            {
                var segment = sequence.FirstSpan;
                return long.TryParse(segment, out value);
            }

            var len = sequence.Length;
            if (len > 20)
            {
                value = default;
                return false;
            }

            Span<char> longSpan = stackalloc char[(int)len];
            sequence.CopyTo(longSpan);

            return long.TryParse(longSpan, out value);
        }

        internal static string AsString(this ReadOnlySequence<char> sequence, ArrayPool<char> pool)
        {
            const int MAX_STRING_STACKALLOC_SIZE = 512;

            if (sequence.IsSingleSegment)
            {
                return new string(sequence.FirstSpan);
            }

            var len = (int)sequence.Length;
            if (len <= MAX_STRING_STACKALLOC_SIZE)
            {
                Span<char> span = stackalloc char[len];
                sequence.CopyTo(span);

                return new string(span);
            }

            // too large to safely put on the stack, use an array and make a copy
            var arr = pool.Rent(len);
            var arrSpan = arr.AsSpan()[0..len];
            sequence.CopyTo(arrSpan);
            var ret = new string(arrSpan);

            pool.Return(arr);
            return ret;
        }

        internal static int LastIndexOf(this ReadOnlySequence<char> sequence, char c)
        {
            if (sequence.IsSingleSegment)
            {
                return sequence.FirstSpan.LastIndexOf(c);
            }

            var e = sequence.GetEnumerator();

            return LastIndexOfRecurse(ref e, c);

            static int LastIndexOfRecurse(ref ReadOnlySequence<char>.Enumerator e, char c)
            {
                // we're at the end...
                if (!e.MoveNext())
                {
                    return -1;
                }

                var cur = e.Current.Span;

                var indexAhead = LastIndexOfRecurse(ref e, c);
                if (indexAhead != -1)
                {
                    return indexAhead + cur.Length;
                }

                return cur.LastIndexOf(c);
            }
        }
    }
}
