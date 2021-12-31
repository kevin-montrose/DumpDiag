using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DumpDiag.Impl
{
    internal static class SequenceReaderHelper
    {
        private const string FREE_STRING = "Free";
        private const string INSTANCE_ATTR_STRING = "instance";

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseWinDbgCharacters(
            ReadOnlySequence<char> sequence,
            int length,
            Span<char> writeTo
        )
        {
            // pattern is: ^ (?<addr> ([0-9a-f]+ `)? [0-9a-f]+ ) \s+ (?<repeatedChar> (?<char> [0-9a-f]+) \s* )+ $

            var reader = new SequenceReader<char>(sequence);

            // validate addr
            if (reader.TryReadTo(out ReadOnlySequence<char> frontAddrStr, '`'))
            {
                // two part address
                if (!frontAddrStr.TryParseHexLong(out _))
                {
                    return false;
                }

                if (!reader.TryReadTo(out ReadOnlySequence<char> backAddrStr, ' '))
                {
                    return false;
                }

                if (!backAddrStr.TryParseHexLong(out _))
                {
                    return false;
                }
            }
            else
            {
                if (!reader.TryReadTo(out ReadOnlySequence<char> addrStr, ' '))
                {
                    return false;
                }

                if (!addrStr.TryParseHexLong(out _))
                {
                    return false;
                }
            }

            reader.AdvancePast(' ');

            // parse the chars (expect the last one)
            for (var i = 0; i < length - 1; i++)
            {
                if (!reader.TryReadTo(out ReadOnlySequence<char> cStr, ' '))
                {
                    return false;
                }

                if (!cStr.TryParseHexShort(out var cShort))
                {
                    return false;
                }

                var c = (char)cShort;
                writeTo[i] = c;

                reader.AdvancePast(' ');
            }

            var lastCharStr = reader.UnreadSequence;
            if (!lastCharStr.TryParseHexShort(out var lastCShort))
            {
                return false;
            }

            writeTo[length - 1] = (char)lastCShort;

            return true;
        }

        internal static bool TryParseCharacters(
            ReadOnlySequence<char> sequence,
            int length,
            ArrayPool<char> pool,
            [NotNullWhen(returnValue: true)]
            out string? chars
        )
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
        internal static bool TryParseClassName(
            ReadOnlySequence<char> seq,
            ArrayPool<char> pool,
            [NotNullWhen(returnValue: true)]
            out string? name
        )
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
        internal static bool TryParseTypeName(
            ReadOnlySequence<char> seq,
            ArrayPool<char> arrayPool,
            [NotNullWhen(returnValue: true)]
            out string? name
        )
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseBaseSize(
            ReadOnlySequence<char> seq,
            out int sizeBytes
        )
        {
            // pattern is: ^ BaseSize: \s+ 0x(?<size> [0-9a-f]+) $

            var reader = new SequenceReader<char>(seq);

            // handle BaseSize:
            if (!reader.TryReadTo(out ReadOnlySequence<char> baseSizeStr, ' '))
            {
                sizeBytes = default;
                return false;
            }

            if (!baseSizeStr.Equals("BaseSize:", StringComparison.Ordinal))
            {
                sizeBytes = default;
                return false;
            }

            reader.AdvancePast(' ');

            // handle 0x
            if (!reader.TryReadTo(out ReadOnlySequence<char> zeroStr, 'x'))
            {
                sizeBytes = default;
                return false;
            }

            if (!zeroStr.Equals("0", StringComparison.Ordinal))
            {
                sizeBytes = default;
                return false;
            }

            // read actual size
            var sizeStr = reader.UnreadSequence;
            if (!sizeStr.TryParseHexInt(out sizeBytes))
            {
                sizeBytes = default;
                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseComponentSize(
            ReadOnlySequence<char> seq,
            out int sizeBytes
        )
        {
            // pattern is: ^ ComponentSize: \s+ 0x(?<size> [0-9a-f]+) $

            var reader = new SequenceReader<char>(seq);

            // handle BaseSize:
            if (!reader.TryReadTo(out ReadOnlySequence<char> baseSizeStr, ' '))
            {
                sizeBytes = default;
                return false;
            }

            if (!baseSizeStr.Equals("ComponentSize:", StringComparison.Ordinal))
            {
                sizeBytes = default;
                return false;
            }

            reader.AdvancePast(' ');

            // handle 0x
            if (!reader.TryReadTo(out ReadOnlySequence<char> zeroStr, 'x'))
            {
                sizeBytes = default;
                return false;
            }

            if (!zeroStr.Equals("0", StringComparison.Ordinal))
            {
                sizeBytes = default;
                return false;
            }

            // read actual size
            var sizeStr = reader.UnreadSequence;
            if (!sizeStr.TryParseHexInt(out sizeBytes))
            {
                sizeBytes = default;
                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseAsyncStateMachineDetails(ReadOnlySequence<char> seq, ArrayPool<char> arrayPool, out AsyncStateMachineDetails details)
        {
            // pattern is: ^ (?<addr> [0-9a-f]+) \s+ (?<mt> [0-9a-f]+) \s+ (?<size> \d+) \s+ (?<status> \S+) \s+ (?<state> \-?\d+) \s+ (?<description> .*?) $

            var reader = new SequenceReader<char>(seq);

            // parse addr
            if (!reader.TryReadTo(out ReadOnlySequence<char> addrStr, ' '))
            {
                details = default;
                return false;
            }

            if (!addrStr.TryParseHexLong(out var addr))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse mt
            if (!reader.TryReadTo(out ReadOnlySequence<char> mtStr, ' '))
            {
                details = default;
                return false;
            }

            if (!mtStr.TryParseHexLong(out var mt))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse size
            if (!reader.TryReadTo(out ReadOnlySequence<char> sizeStr, ' '))
            {
                details = default;
                return false;
            }

            if (!sizeStr.TryParseDecimalInt(out var size))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // skip status
            if (!reader.TryReadTo(out ReadOnlySequence<char> statusStr, ' '))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // validate state
            if (!reader.TryReadTo(out ReadOnlySequence<char> stateStr, ' '))
            {
                details = default;
                return false;
            }

            if (!stateStr.TryParseDecimalInt(out _))
            {
                details = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read description
            var descStr = reader.UnreadSequence;
            var descStrStr = descStr.AsString(arrayPool);

            details = new AsyncStateMachineDetails(addr, mt, size, descStrStr);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseInstanceFieldWithValue(ReadOnlySequence<char> sequence, ArrayPool<char> arrayPool, out InstanceFieldWithValue field)
        {
            // pattern is: ^ (?<methodTable> [0-9a-f]+) \s+ (?<field> \S+) \s+ (?<offset> [0-9a-f]+) \s+ (?<type> \S+) \s+ (?<vt> \S+) \s+ (?<attr> \S+) \s+ (?<value> [0-9a-f]+) \s+ (?<name> \S+) $

            var reader = new SequenceReader<char>(sequence);

            // skip method table
            if (!reader.TryReadTo(out ReadOnlySequence<char> methodTableStr, ' '))
            {
                field = default;
                return false;
            }

            if (!methodTableStr.TryParseHexLong(out var mt))
            {
                field = default;
                return false;
            }

            reader.AdvancePast(' ');

            // skip field
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                field = default;
                return false;
            }
            reader.AdvancePast(' ');

            // skip offset
            if (!reader.TryReadTo(out ReadOnlySequence<char> offsetStr, ' '))
            {
                field = default;
                return false;
            }

            if (!offsetStr.TryParseHexInt(out _))
            {
                field = default;
                return false;
            }

            // skip type
            reader.Advance(20);
            reader.AdvancePast(' ');

            // skip VT
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                field = default;
                return false;
            }
            reader.AdvancePast(' ');

            // parse attr
            if (!reader.TryReadTo(out ReadOnlySequence<char> attrStr, ' '))
            {
                field = default;
                return false;
            }

            if (!attrStr.Equals(INSTANCE_ATTR_STRING, StringComparison.Ordinal))
            {
                field = default;
                return false;
            }

            reader.AdvancePast(' ');

            // parse value
            if (!reader.TryReadTo(out ReadOnlySequence<char> valueStr, ' '))
            {
                field = default;
                return false;
            }

            if (!valueStr.TryParseHexLong(out var value))
            {
                field = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read name
            var nameStr = reader.UnreadSequence.AsString(arrayPool);

            field = new InstanceFieldWithValue(new InstanceField(nameStr, mt), value);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseInstanceFieldNoValue(ReadOnlySequence<char> sequence, ArrayPool<char> arrayPool, out InstanceField field)
        {
            var reader = new SequenceReader<char>(sequence);

            // skip method table
            if (!reader.TryReadTo(out ReadOnlySequence<char> methodTableStr, ' '))
            {
                field = default;
                return false;
            }

            if (!methodTableStr.TryParseHexLong(out var mt))
            {
                field = default;
                return false;
            }

            reader.AdvancePast(' ');

            // skip field
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                field = default;
                return false;
            }
            reader.AdvancePast(' ');

            // skip offset
            if (!reader.TryReadTo(out ReadOnlySequence<char> offsetStr, ' '))
            {
                field = default;
                return false;
            }

            if (!offsetStr.TryParseHexInt(out _))
            {
                field = default;
                return false;
            }

            // skip type
            reader.Advance(20);
            reader.AdvancePast(' ');

            // skip VT
            if (!reader.TryAdvanceTo(' ', advancePastDelimiter: false))
            {
                field = default;
                return false;
            }
            reader.AdvancePast(' ');

            // parse attr
            if (!reader.TryReadTo(out ReadOnlySequence<char> attrStr, ' '))
            {
                field = default;
                return false;
            }

            if (!attrStr.Equals(INSTANCE_ATTR_STRING, StringComparison.Ordinal))
            {
                field = default;
                return false;
            }

            reader.AdvancePast(' ');

            // get name
            var unread = reader.UnreadSequence;
            var ix = unread.LastIndexOf(' ');

            var name = unread.Slice(ix + 1);
            var nameStr = name.AsString(arrayPool);

            field = new InstanceField(nameStr, mt);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseWinDbgLongs(ReadOnlySequence<char> seq, ImmutableArray<long>.Builder into)
        {
            // pattern is: ^ (?<addr> [0-9a-f]+ ` [0-9a-f]+) ( \s+  (?<value> [0-9a-f]+ ` [0-9a-f]+) )* $

            var reader = new SequenceReader<char>(seq);

            // handle address
            if (!reader.TryReadTo(out ReadOnlySequence<char> addrFront, '`'))
            {
                return false;
            }

            if (!addrFront.TryParseHexLong(out _))
            {
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> addrBack, ' '))
            {
                return false;
            }

            if (!addrBack.TryParseHexLong(out _))
            {
                return false;
            }

            reader.AdvancePast(' ');

            // handle values
            while(reader.Remaining > 0)
            {
                if(!reader.TryReadTo(out ReadOnlySequence<char> valueFront, '`'))
                {
                    return false;
                }

                if(!valueFront.TryParseHexLong(out var highBitsLong))
                {
                    return false;
                }
                var highBits = (uint)highBitsLong;

                if(!reader.TryReadTo(out ReadOnlySequence<char> valueBack, ' '))
                {
                    valueBack = reader.UnreadSequence;
                    reader.AdvanceToEnd();
                }
                else
                {
                    reader.AdvancePast(' ');
                }

                if(!valueBack.TryParseHexLong(out var lowBitsLong))
                {
                    return false;
                }
                var lowBits = (uint)lowBitsLong;

                var value = ((ulong)highBits << 32) | lowBits;

                into.Add((long)value);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseLongs(ReadOnlySequence<char> seq, ImmutableArray<long>.Builder into)
        {
            // pattern is: ^ (?<addr> [0-9a-f]+): (?<value> [0-9a-f]+)+ $ 

            var reader = new SequenceReader<char>(seq);

            // handle address
            if (!reader.TryReadTo(out ReadOnlySequence<char> addr, ':'))
            {
                return false;
            }

            if (!addr.TryParseHexLong(out _))
            {
                return false;
            }

            reader.AdvancePast(' ');

            // handle values
            while (reader.Remaining > 0)
            {
                if (!reader.TryReadTo(out ReadOnlySequence<char> valueStr, ' '))
                {
                    valueStr = reader.UnreadSequence;
                    reader.Advance(valueStr.Length);
                }
                else
                {
                    reader.AdvancePast(' ');
                }

                if (!valueStr.TryParseHexLong(out var value))
                {
                    return false;
                }

                into.Add(value);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsSectionBreak(ReadOnlySequence<char> seq)
        {
            // pattern is: ^ (\-)+ $

            var reader = new SequenceReader<char>(seq);

            reader.AdvancePast('-');

            return reader.UnreadSequence.IsEmpty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseWinDbgHeapSegment(ReadOnlySequence<char> seq, out long startAddr, out long sizeBytes)
        {
            // pattern is: ^ (?<segment> [0-9a-f]+) \s+ (?<begin> [0-9a-f]+) \s+ (?<allocated> [0-9a-f]+) \s+ \( (?<allocatedSize> \d+) \) \s+ 0x([0-9a-f]+) $

            var reader = new SequenceReader<char>(seq);

            // skip segment
            if (!reader.TryReadTo(out ReadOnlySequence<char> segmentStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!segmentStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // skip begin
            if (!reader.TryReadTo(out ReadOnlySequence<char> beginStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!beginStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read allocated
            if (!reader.TryReadTo(out ReadOnlySequence<char> allocatedStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!allocatedStr.TryParseHexLong(out startAddr))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read allocated size
            if (!reader.TryReadTo(out ReadOnlySequence<char> allocatedZeroStr, 'x'))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (allocatedZeroStr.Length != 1 || !allocatedZeroStr.StartsWith("0", StringComparison.Ordinal))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> allocSizeHexStr, '('))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!allocSizeHexStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> allocatedSizeStr, ')'))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!allocatedSizeStr.TryParseDecimalLong(out sizeBytes))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            return reader.UnreadSequence.IsEmpty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseHeapSegment(ReadOnlySequence<char> seq, out long startAddr, out long sizeBytes)
        {
            // pattern is: ^ (?<segment> [0-9a-f]+) \s+ (?<begin> [0-9a-f]+) \s+ (?<allocated> [0-9a-f]+) \s+ (?<committed> [0-9a-f]+) \s+ 0x([0-9a-f]+) \( (?<allocatedSize> \d+) \) \s+ 0x([0-9a-f]+) \( (?<committedSize> \d+) \) $

            var reader = new SequenceReader<char>(seq);

            // skip segment
            if (!reader.TryReadTo(out ReadOnlySequence<char> segmentStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!segmentStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // skip begin
            if (!reader.TryReadTo(out ReadOnlySequence<char> beginStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!beginStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read allocated
            if (!reader.TryReadTo(out ReadOnlySequence<char> allocatedStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!allocatedStr.TryParseHexLong(out startAddr))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // skip committed
            if (!reader.TryReadTo(out ReadOnlySequence<char> committedStr, ' '))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!committedStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read allocated size
            if (!reader.TryReadTo(out ReadOnlySequence<char> allocatedZeroStr, 'x'))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (allocatedZeroStr.Length != 1 || !allocatedZeroStr.StartsWith("0", StringComparison.Ordinal))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> allocSizeHexStr, '('))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!allocSizeHexStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> allocatedSizeStr, ')'))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!allocatedSizeStr.TryParseDecimalLong(out sizeBytes))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // skip committed size
            if (!reader.TryReadTo(out ReadOnlySequence<char> committedZeroStr, 'x'))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (committedZeroStr.Length != 1 || !committedZeroStr.StartsWith("0", StringComparison.Ordinal))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> committedSizeHexStr, '('))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!committedSizeHexStr.TryParseHexLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!reader.TryReadTo(out ReadOnlySequence<char> committedSizeStr, ')'))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            if (!committedSizeStr.TryParseDecimalLong(out _))
            {
                startAddr = sizeBytes = 0;
                return false;
            }

            reader.AdvancePast(' ');

            return reader.UnreadSequence.IsEmpty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseGCHandle(ReadOnlySequence<char> seq, ArrayPool<char> arrayPool, out HeapGCHandle gcHandle)
        {
            const string PINNED = "Pinned";
            const string REF_COUNTED = "RefCounted";
            const string WEAK_SHORT = "WeakShort";
            const string WEAK_LONG = "WeakLong";
            const string STRONG = "Strong";
            const string VARIABLE = "Variable";
            const string ASYNC_PINNED = "AsyncPinned";
            const string SIZED_REF = "SizedRef";
            const string DEPENDENT = "Dependent";

            // pattern is: ^ (?<handle> [a-f0-9]+) \s+ (?<type> \S+) \s+ (?<obj> [a-f0-9]+) \s+ (?<size> \d+) \s+ ((?<refCount> [a-f0-9]+) \s+)? (?<name> .*) $

            var reader = new SequenceReader<char>(seq);

            // read handle

            if (!reader.TryReadTo(out ReadOnlySequence<char> handleStr, ' '))
            {
                gcHandle = default;
                return false;
            }

            if (!handleStr.TryParseHexLong(out var handle))
            {
                gcHandle = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read type

            if (!reader.TryReadTo(out ReadOnlySequence<char> typeStr, ' '))
            {
                gcHandle = default;
                return false;
            }

            HeapGCHandle.HandleTypes type;
            if (typeStr.Equals(PINNED, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.Pinned;
            }
            else if (typeStr.Equals(REF_COUNTED, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.RefCounted;
            }
            else if (typeStr.Equals(WEAK_SHORT, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.WeakShort;
            }
            else if (typeStr.Equals(WEAK_LONG, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.WeakLong;
            }
            else if (typeStr.Equals(STRONG, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.Strong;
            }
            else if (typeStr.Equals(VARIABLE, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.Variable;
            }
            else if (typeStr.Equals(ASYNC_PINNED, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.AsyncPinned;
            }
            else if (typeStr.Equals(SIZED_REF, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.SizedRef;
            }
            else if (typeStr.Equals(DEPENDENT, StringComparison.Ordinal))
            {
                type = HeapGCHandle.HandleTypes.Dependent;
            }
            else
            {
                gcHandle = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read obj

            if (!reader.TryReadTo(out ReadOnlySequence<char> objStr, ' '))
            {
                gcHandle = default;
                return false;
            }

            if (!objStr.TryParseHexLong(out var obj))
            {
                gcHandle = default;
                return false;
            }

            reader.AdvancePast(' ');

            // read size

            if (!reader.TryReadTo(out ReadOnlySequence<char> sizeStr, ' '))
            {
                gcHandle = default;
                return false;
            }

            if (!sizeStr.TryParseDecimalInt(out var size))
            {
                gcHandle = default;
                return false;
            }

            reader.AdvancePast(' ');

            // skip ref count, if it's present
            if (reader.UnreadSequence.IsEmpty)
            {
                gcHandle = default;
                return false;
            }

            var nextChar = reader.UnreadSequence.First.Span[0];
            if (nextChar >= '0' && nextChar <= '9')
            {
                if (!reader.TryReadTo(out ReadOnlySequence<char> refCountStr, ' '))
                {
                    gcHandle = default;
                    return false;
                }

                if (!refCountStr.TryParseHexLong(out _))
                {
                    gcHandle = default;
                    return false;
                }

                reader.AdvancePast(' ');
            }

            // read type!

            var objTypeStr = reader.UnreadSequence;
            if (objTypeStr.IsEmpty)
            {
                gcHandle = default;
                return false;
            }

            var objType = objTypeStr.AsString(arrayPool);

            gcHandle = new HeapGCHandle(handle, type, obj, objType, size);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseGCHandleStats(ReadOnlySequence<char> seq, ArrayPool<char> arrayPool, out long methodTable, [NotNullWhen(returnValue: true)] out string? typeName)
        {
            // pattern is: ^ (?<mt> [a-f0-9]+) \s+ (?<count> \d+) \s+ (?<size> \d+) \s+ (?<type> .*) $

            var reader = new SequenceReader<char>(seq);

            // read mt

            if (!reader.TryReadTo(out ReadOnlySequence<char> mtStr, ' '))
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            if (!mtStr.TryParseHexLong(out methodTable))
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            reader.AdvancePast(' ');

            // skip count

            if (!reader.TryReadTo(out ReadOnlySequence<char> countStr, ' '))
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            if (!countStr.TryParseDecimalLong(out _))
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            reader.AdvancePast(' ');

            // skip size

            if (!reader.TryReadTo(out ReadOnlySequence<char> sizeStr, ' '))
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            if (!sizeStr.TryParseDecimalLong(out _))
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            reader.AdvancePast(' ');

            // read type

            var typeStr = reader.UnreadSequence;

            if (typeStr.IsEmpty)
            {
                methodTable = 0;
                typeName = null;
                return false;
            }

            typeName = typeStr.AsString(arrayPool);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseMethodTable(ReadOnlySequence<char> seq, out long mt)
        {
            const string METHOD_TABLE_STR = "MethodTable: ";

            // pattern is: ^ MethodTable: (?<mt> [a-f0-9]+) $

            if (!seq.StartsWith(METHOD_TABLE_STR, StringComparison.Ordinal))
            {
                mt = 0;
                return false;
            }

            var mtStr = seq.Slice(METHOD_TABLE_STR.Length);

            if (!mtStr.TryParseHexLong(out mt))
            {
                mt = 0;
                return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryParseHeapSpace(ReadOnlySequence<char> seq, out long gen0, out long gen1, out long gen2, out long loh, out long poh)
        {
            const string TOTAL = "Total";

            // pattern is: ^ (?<heapName> \S+) \s+ (?<gen0> \d+) \s+ (?<gen1> \d+) \s+ (?<gen2> \d+) \s+ (?<loh> \d+) \s+ (?<poh> \d+)? .* $

            var reader = new SequenceReader<char>(seq);

            // skip heap name

            if (!reader.TryReadTo(out ReadOnlySequence<char> heapName, ' '))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            // total isn't a valid heap
            if (heapName.Equals(TOTAL, StringComparison.Ordinal))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read gen0

            if (!reader.TryReadTo(out ReadOnlySequence<char> gen0Str, ' '))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            if (!gen0Str.TryParseDecimalLong(out gen0))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read gen1

            if (!reader.TryReadTo(out ReadOnlySequence<char> gen1Str, ' '))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            if (!gen1Str.TryParseDecimalLong(out gen1))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read gen2

            if (!reader.TryReadTo(out ReadOnlySequence<char> gen2Str, ' '))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            if (!gen2Str.TryParseDecimalLong(out gen2))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read loh

            if (!reader.TryReadTo(out ReadOnlySequence<char> lohStr, ' '))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            if (!lohStr.TryParseDecimalLong(out loh))
            {
                gen0 = gen1 = gen2 = loh = poh = 0;
                return false;
            }

            reader.AdvancePast(' ');

            // read poh, but optionally

            if (!reader.TryReadTo(out ReadOnlySequence<char> pohStr, ' '))
            {
                pohStr = reader.UnreadSequence;
            }

            if (!pohStr.TryParseDecimalLong(out poh))
            {
                poh = 0;
                return true;
            }

            // we're good, there can be trailing junk
            return true;
        }
    }
}
