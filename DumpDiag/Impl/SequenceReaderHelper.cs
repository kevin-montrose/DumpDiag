﻿using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace DumpDiag.Impl
{
    internal static class SequenceReaderHelper
    {
        private const string FREE_STRING = "Free";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseHeapEntry(ReadOnlySequence<char> sequence, bool live, out HeapEntry entry, out bool free)
        {
            // pattern is: ^ (?<address> [0-9a-f]+) \s+ (?<methodTable> [0-9a-f]+) \s+ (?<size> \d+) \s*? (?<free> .\S*?)? $

            var reader = new SequenceReader<char>(sequence);

            // read address
            if (!reader.TryReadTo(out ReadOnlySequence<char> addrStr, ' '))
            {
                entry = default;
                free = default;
                return false;
            }

            if (!addrStr.TryParseHexLong(out var addr))
            {
                entry = default;
                free = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read method table
            if (!reader.TryReadTo(out ReadOnlySequence<char> methodTableStr, ' '))
            {
                entry = default;
                free = default;
                return false;
            }

            if (!methodTableStr.TryParseHexLong(out var methodTable))
            {
                entry = default;
                free = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read size
            bool reachedEnd;

            var startOfSize = reader.Position;
            if (!reader.TryReadTo(out ReadOnlySequence<char> sizeStr, ' '))
            {
                reachedEnd = true;
                reader.AdvanceToEnd();

                sizeStr = sequence.Slice(startOfSize, reader.Position);
            }
            else
            {
                reachedEnd = false;
            }

            if (!sizeStr.TryParseDecimalInt(out var size))
            {
                entry = default;
                free = default;
                return false;
            }

            if (!reachedEnd)
            {
                reader.AdvancePast(' ');
            }

            // read free
            var freeStr = reader.UnreadSequence;
            if (freeStr.Equals(FREE_STRING.AsSpan(), StringComparison.Ordinal))
            {
                // if it's free, don't bother with it
                entry = default;
                free = true;
                return true;
            }

            entry = new HeapEntry(addr, methodTable, size, live);
            free = false;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseFieldOffset(ReadOnlySequence<char> sequence, out FieldOffset fieldOfset)
        {
            // pattern is: $ (?<methodTable> [0-9a-f]+) \s+ (?<field> \S+) \s+ (?<offset> [0-9a-f]+) \s+ (?<type> \S+) \s+ (?<vt> \S+) \s+ (?<attr> \S+) \s+ (?<value> [0-9a-f]+) \s+ (?<name> \S+) ^

            var reader = new SequenceReader<char>(sequence);

            // skip method table
            if (!reader.TryReadTo(out ReadOnlySequence<char> methodTableStr, ' '))
            {
                fieldOfset = default;
                return false;
            }

            if (!methodTableStr.TryParseHexLong(out _))
            {
                fieldOfset = default;
                return false;
            }

            reader.AdvancePast(' ');

            // skip field
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                fieldOfset = default;
                return false;
            }
            reader.AdvancePast(' ');

            // read offset
            if (!reader.TryReadTo(out ReadOnlySequence<char> offsetStr, ' '))
            {
                fieldOfset = default;
                return false;
            }

            if (!offsetStr.TryParseHexInt(out var offset))
            {
                fieldOfset = default;
                return false;
            }

            reader.AdvancePast(' ');

            // skip type
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                fieldOfset = default;
                return false;
            }
            reader.AdvancePast(' ');

            // skip VT
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                fieldOfset = default;
                return false;
            }
            reader.AdvancePast(' ');

            // skip attr
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                fieldOfset = default;
                return false;
            }
            reader.AdvancePast(' ');

            // read value
            if (!reader.TryReadTo(out ReadOnlySequence<char> valueStr, ' '))
            {
                fieldOfset = default;
                return false;
            }

            if (!valueStr.TryParseHexLong(out _))
            {
                fieldOfset = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read name
            var nameStr = reader.UnreadSequence;

            fieldOfset = new FieldOffset(nameStr, offset);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseStackFrame(ReadOnlySequence<char> sequence, ArrayPool<char> arrayPool, out AnalyzerStackFrame stackFrame)
        {
            // pattern is: $ (?<childSP> [0-9a-f]+) \s+ (?<instructionPointer> [0-9a-f]) \s+ (?<callSite> .*?) ^

            var reader = new SequenceReader<char>(sequence);

            // read SP
            if (!reader.TryReadTo(out ReadOnlySequence<char> spStr, ' '))
            {
                stackFrame = default;
                return false;
            }

            if (!spStr.TryParseHexLong(out var sp))
            {
                stackFrame = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read IP
            if (!reader.TryReadTo(out ReadOnlySequence<char> ipStr, ' '))
            {
                stackFrame = default;
                return false;
            }

            if (!ipStr.TryParseHexLong(out var ip))
            {
                stackFrame = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read callSite
            var callSiteStr = reader.UnreadSequence;

            var callSiteStrStr = callSiteStr.AsString(arrayPool);

            stackFrame = new AnalyzerStackFrame(sp, ip, callSiteStrStr);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseStringLength(ReadOnlySequence<char> sequence, out int length)
        {
            // pattern is: ^ (?<addr> [0-9a-f]+) \s+ (?<length> [0-9a-f]+) $

            var reader = new SequenceReader<char>(sequence);

            // skip address
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read length
            if (!reader.TryReadTo(out ReadOnlySequence<char> lengthStr, ' '))   // have to check for trailing whitespace on this one
            {
                lengthStr = reader.UnreadSequence;
            }

            if (!lengthStr.TryParseHexInt(out length))
            {
                length = default;
                return false;
            }

            return true;
        }

        internal static bool TryParseCharacters(ReadOnlySequence<char> sequence, int length, ArrayPool<char> pool, out string chars)
        {
            const int MAX_ON_CHARS_ON_STACK = 128;

            // pattern is: ^ (?<addr> [0-9a-f]+): \s+ (?<repeatedChar> (?<char> [0-9a-f]+) \s* )+ $

            var reader = new SequenceReader<char>(sequence);

            // validate addr
            if (!reader.TryReadTo(out ReadOnlySequence<char> addrStr, ':'))
            {
                chars = null;
                return false;
            }

            if (!addrStr.TryParseHexLong(out _))
            {
                chars = null;
                return false;
            }

            reader.AdvancePast(' ');

            var arr = length <= MAX_ON_CHARS_ON_STACK ? null : pool.Rent(length);
            Span<char> writeTo = arr == null ? stackalloc char[length] : arr.AsSpan().Slice(0, length);

            // parse the chars (expect the last one)
            for (var i = 0; i < length - 1; i++)
            {
                if (!reader.TryReadTo(out ReadOnlySequence<char> cStr, ' '))
                {
                    chars = null;
                    return false;
                }

                if (!cStr.TryParseHexShort(out var cShort))
                {
                    chars = null;
                    return false;
                }

                var c = (char)cShort;
                writeTo[i] = c;

                reader.AdvancePast(' ');
            }

            var lastCharStr = reader.UnreadSequence;
            if (!lastCharStr.TryParseHexShort(out var lastCShort))
            {
                chars = null;
                return false;
            }

            writeTo[^1] = (char)lastCShort;

            chars = new string(writeTo);

            if (arr != null)
            {
                pool.Return(arr);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseDelegateMethodDetails(ReadOnlySequence<char> seq, ArrayPool<char> pool, out DelegateMethodDetails details)
        {
            // pattern is: ^ (?<target> [0-9a-f]+) \s+ (?<methodTable> [0-9a-f]+) \s+ (?<name> .*?) $

            var reader = new SequenceReader<char>(seq);

            // read target
            if (!reader.TryReadTo(out ReadOnlySequence<char> targetStr, ' '))
            {
                details = default;
                return false;
            }

            if (!targetStr.TryParseHexLong(out var target))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read method table
            if (!reader.TryReadTo(out ReadOnlySequence<char> methodTableStr, ' '))
            {
                details = default;
                return false;
            }

            if (!methodTableStr.TryParseHexLong(out var methodTable))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read name
            var nameStr = reader.UnreadSequence;
            var name = nameStr.AsString(pool);

            details = new DelegateMethodDetails(target, methodTable, name);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseEEClass(ReadOnlySequence<char> seq, out long eeClass)
        {
            // pattern is: ^ EEClass: \s+ (?<pointer> [0-9a-f]+) $

            var reader = new SequenceReader<char>(seq);

            // check EEClass:
            if (!reader.TryReadTo(out ReadOnlySequence<char> eeClassStr, ':'))
            {
                eeClass = default;
                return false;
            }

            if (!eeClassStr.Equals("EEClass", StringComparison.Ordinal))
            {
                eeClass = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse value
            var remainder = reader.UnreadSequence;
            return remainder.TryParseHexLong(out eeClass);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseClassName(ReadOnlySequence<char> seq, ArrayPool<char> pool, out string name)
        {
            // pattern is: ^ Class Name: \s+ (?<className> .*?) $

            var reader = new SequenceReader<char>(seq);

            // check Class Name:
            if (!reader.TryReadTo(out ReadOnlySequence<char> className, ':'))
            {
                name = null;
                return false;
            }

            if (!className.Equals("Class Name", StringComparison.Ordinal))
            {
                name = null;
                return false;
            }

            reader.AdvancePast(' ');

            // read className
            var remainder = reader.UnreadSequence;
            name = remainder.AsString(pool);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseParentClass(ReadOnlySequence<char> seq, out long parentClass)
        {
            // pattern is: ^ Parent Class: \s+ (?<pointer> [0-9a-f]+) $

            var reader = new SequenceReader<char>(seq);

            // check Parent Class:
            if (!reader.TryReadTo(out ReadOnlySequence<char> parentClassStr, ':'))
            {
                parentClass = default;
                return false;
            }

            if (!parentClassStr.Equals("Parent Class", StringComparison.Ordinal))
            {
                parentClass = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read pointer
            var remainder = reader.UnreadSequence;
            return remainder.TryParseHexLong(out parentClass);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseArrayAddress(ReadOnlySequence<char> seq, out long address)
        {
            // pattern is: ^ [0] \s+ (?<address> [0-9a-f]+) $

            var reader = new SequenceReader<char>(seq);

            // validate [0]
            if (!reader.TryReadTo(out ReadOnlySequence<char> elemStr, ' '))
            {
                address = default;
                return false;
            }

            if (!elemStr.Equals("[0]", StringComparison.Ordinal))
            {
                address = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse address
            var remainder = reader.UnreadSequence;
            return remainder.TryParseHexLong(out address);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseArrayLength(ReadOnlySequence<char> seq, out int length)
        {
            // patterns is: ^ Array: \s+ Rank  (?<rank> \d+), Number of elements (?<size> \d+), Type (?<type> .*?) $

            var reader = new SequenceReader<char>(seq);

            // validate Array:
            if (!reader.TryReadTo(out ReadOnlySequence<char> arrayStr, ' '))
            {
                length = default;
                return false;
            }

            if (!arrayStr.Equals("Array:", StringComparison.Ordinal))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // validate Rank
            if (!reader.TryReadTo(out ReadOnlySequence<char> rankStr, ' '))
            {
                length = default;
                return false;
            }

            if (!rankStr.Equals("Rank", StringComparison.Ordinal))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse rank
            if (!reader.TryReadTo(out ReadOnlySequence<char> rankValueStr, ','))
            {
                length = default;
                return false;
            }

            if (!rankValueStr.TryParseDecimalInt(out _))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // validate Number
            if (!reader.TryReadTo(out ReadOnlySequence<char> numberStr, ' '))
            {
                length = default;
                return false;
            }

            if (!numberStr.Equals("Number", StringComparison.Ordinal))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // validate of
            if (!reader.TryReadTo(out ReadOnlySequence<char> ofStr, ' '))
            {
                length = default;
                return false;
            }

            if (!ofStr.Equals("of", StringComparison.Ordinal))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // validate elements
            if (!reader.TryReadTo(out ReadOnlySequence<char> elementsStr, ' '))
            {
                length = default;
                return false;
            }

            if (!elementsStr.Equals("elements", StringComparison.Ordinal))
            {
                length = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse length
            if (!reader.TryReadTo(out ReadOnlySequence<char> lengthStr, ','))
            {
                length = default;
                return false;
            }

            return lengthStr.TryParseDecimalInt(out length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseTypeName(ReadOnlySequence<char> seq, ArrayPool<char> arrayPool, out string name)
        {
            // pattern is: ^ Name: \s+ (?<name> .*?) $


            var reader = new SequenceReader<char>(seq);

            // handle Name:
            if (!reader.TryReadTo(out ReadOnlySequence<char> nameStr, ' '))
            {
                name = default;
                return false;
            }

            if (!nameStr.Equals("Name:", StringComparison.Ordinal))
            {
                name = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read actual name
            name = reader.UnreadSequence.AsString(arrayPool);
            return true;
        }
    }
}
