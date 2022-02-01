using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace DumpDiag.Impl
{
    /// <summary>
    /// A command to send to a process.
    /// Wraps up patterns we care about, so we don't have to actually allocate
    /// to represent a command.
    /// </summary>
    internal readonly struct Command
    {
        private enum Style : byte
        {
            Literal,
            Address,
            AddressSuffix,
            Count,
            CountSuffix,
            CountInfixCountAddress,
            AddressAndHexCountSuffix
        }

        private readonly Style style;
        private readonly string prefix;
        private readonly int? count;
        private readonly string? infixOrSuffix;
        private readonly long? addr;

        private bool IsLiteral => style == Style.Literal;

        [MemberNotNullWhen(true, nameof(addr))]
        private bool IsAddress => style == Style.Address;

        [MemberNotNullWhen(true, nameof(addr), nameof(infixOrSuffix))]
        private bool IsAddressSuffix => style == Style.AddressSuffix;

        [MemberNotNullWhen(true, nameof(count))]
        private bool IsCount => style == Style.Count;

        [MemberNotNullWhen(true, nameof(count), nameof(infixOrSuffix))]
        private bool IsCountSuffix => style == Style.CountSuffix;

        [MemberNotNullWhen(true, nameof(count), nameof(addr), nameof(infixOrSuffix))]
        private bool IsCountInfixCountAddress => style == Style.CountInfixCountAddress;

        [MemberNotNullWhen(true, nameof(addr), nameof(count))]
        private bool IsAddressAndHexCountSuffix => style == Style.AddressAndHexCountSuffix;

        private Command(Style style, string prefix, int? count, string? infixOrSuffix, long? addr)
        {
            this.style = style;
            this.prefix = prefix;
            this.count = count;
            this.infixOrSuffix = infixOrSuffix;
            this.addr = addr;
        }

        internal static Command CreateCommand(string command)
        => new Command(Style.Literal, command, null, null, null);

        /// <summary>
        /// (command) (addr)
        /// </summary>
        internal static Command CreateCommandWithAddress(string command, long addr)
        => new Command(Style.Address, command, null, null, addr);

        /// <summary>
        /// (command) (count)
        /// </summary>
        internal static Command CreateCommandWithCount(string command, int count)
        => new Command(Style.Count, command, count, null, null);

        /// <summary>
        /// (command) (count) (suffix)
        /// </summary>
        internal static Command CreateCommandWithCountAndSuffix(string command, int count, string suffix)
        => new Command(Style.CountSuffix, command, count, suffix, null);

        /// <summary>
        /// (command) (address) (suffix)
        /// </summary>
        internal static Command CreateCommandWithAddressAndSuffix(string command, long addr, string suffix)
        => new Command(Style.AddressSuffix, command, null, suffix, addr);

        /// <summary>
        /// (prefix) (count) (infix) (count) (addr)
        /// 
        /// note that count is written twice
        /// </summary>
        internal static Command CreateCommandWithCountAndAddress(string command, int count, string infix, long addr)
        => new Command(Style.CountInfixCountAddress, command, count, infix, addr);

        /// <summary>
        /// (prefix) (addr) L(count)
        /// 
        /// note the L before count (which is then in hex)
        /// </summary>
        internal static Command CreateCommandWithAddressAndHexCountSuffix(string command, long addr, int count)
        => new Command(Style.AddressAndHexCountSuffix, command, count, null, addr);

        internal void Write(Stream stream, string newLine, System.Text.Encoding encoding)
        {
            const int MAX_BYTES_PER_CHAR = 4;

            const int MAX_ADDRESS_LENGTH = 16;
            const int MAX_COUNT_HEX_LENGTH = 8;
            const int MAX_COUNT_DECIMAL_LENGTH = 10;

            int commandLength;

            if (IsLiteral)
            {
                commandLength = prefix.Length;  // command
            }
            else if (IsAddress)
            {
                commandLength =
                    prefix.Length +             // command
                    1 +                         // space
                    MAX_ADDRESS_LENGTH;         // address
            }
            else if (IsAddressSuffix)
            {
                commandLength =
                    prefix.Length +             // command
                    1 +                         // space
                    MAX_ADDRESS_LENGTH +        // address
                    1 +                         // space
                    infixOrSuffix.Length;       // suffix
            }
            else if (IsCount)
            {
                commandLength =
                    prefix.Length +             // command
                    1 +                         // space
                    MAX_COUNT_DECIMAL_LENGTH;           // count
            }
            else if(IsCountSuffix)
            {
                commandLength =
                    prefix.Length +             // command
                    1 +                         // space
                    MAX_COUNT_DECIMAL_LENGTH +          // count
                    1 +                         // space
                    infixOrSuffix.Length;       // suffix
            }
            else if(IsCountInfixCountAddress)
            {
                commandLength =
                    prefix.Length +             // command
                    1 +                         // space
                    MAX_COUNT_DECIMAL_LENGTH +          // count
                    1 +                         // space
                    infixOrSuffix.Length +      // infix
                    1 +                         // space
                    MAX_COUNT_DECIMAL_LENGTH +          // count (again)
                    1 +                         // space
                    MAX_ADDRESS_LENGTH;         // address
            }
            else if(IsAddressAndHexCountSuffix)
            {
                commandLength =
                    prefix.Length +             // command
                    1 +                         // space
                    MAX_ADDRESS_LENGTH +        // address
                    1 +                         // space
                    1 +                         // L
                    MAX_COUNT_HEX_LENGTH;       // count
            }
            else
            {
                throw new InvalidOperationException($"Unexpected style {style}");
            }

            Span<char> data = stackalloc char[commandLength + newLine.Length];

            int written;
            if (IsLiteral)
            {
                prefix.AsSpan().CopyTo(data);
                written = prefix.Length;
            }
            else if (IsAddress)
            {
                var remainder = data;
                
                // command
                prefix.AsSpan().CopyTo(remainder);
                remainder = remainder[prefix.Length..];
                
                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // address
                var res = addr.Value.TryFormat(remainder, out var charsWritten, "X2");
                Debug.Assert(res);
                
                written = prefix.Length + 1 + charsWritten;
            }
            else if (IsAddressSuffix)
            {
                var remainder = data;

                // command
                prefix.AsSpan().CopyTo(remainder);
                remainder = remainder[prefix.Length..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // address
                var res = addr.Value.TryFormat(remainder, out var charsWritten, "X2");
                Debug.Assert(res);
                remainder = remainder[charsWritten..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // suffix
                infixOrSuffix.AsSpan().CopyTo(remainder);

                written = prefix.Length + 1 + charsWritten + 1 + infixOrSuffix.Length;
            }
            else if (IsCount)
            {
                var remainder = data;

                // command
                prefix.AsSpan().CopyTo(remainder);
                remainder = remainder[prefix.Length..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // count
                var res = count.Value.TryFormat(remainder, out var charsWritten);
                Debug.Assert(res);

                written =
                    prefix.Length +             // command
                    1 +                         // space
                    charsWritten;               // count
            }
            else if (IsCountSuffix)
            {
                var remainder = data;

                // command
                prefix.AsSpan().CopyTo(remainder);
                remainder = remainder[prefix.Length..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // count
                var res = count.Value.TryFormat(remainder, out var charsWritten);
                Debug.Assert(res);
                remainder = remainder[charsWritten..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // suffix
                infixOrSuffix.AsSpan().CopyTo(remainder);

                written =
                    prefix.Length +
                    1 +
                    charsWritten +
                    1 +
                    infixOrSuffix.Length;
            }
            else if (IsCountInfixCountAddress)
            {
                var remainder = data;

                // command
                prefix.AsSpan().CopyTo(remainder);
                remainder = remainder[prefix.Length..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // count
                var res1 = count.Value.TryFormat(remainder, out var countCharsWritten);
                Debug.Assert(res1);
                remainder = remainder[countCharsWritten..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // infix
                infixOrSuffix.AsSpan().CopyTo(remainder);
                remainder = remainder[infixOrSuffix.Length..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // count (again)
                var countStart = prefix.Length + 1;
                var countEnd = countStart + countCharsWritten;
                data[countStart..countEnd].CopyTo(remainder);
                remainder = remainder[countCharsWritten..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // address
                var res3 = addr.Value.TryFormat(remainder, out var addrCharsWritten, "X2");
                Debug.Assert(res3);
                remainder = remainder[addrCharsWritten..];

                written =
                    prefix.Length +             // command
                    1 +                         // space
                    countCharsWritten +         // count
                    1 +                         // space
                    infixOrSuffix.Length +      // infix
                    1 +                         // space
                    countCharsWritten +         // count (again)
                    1 +                         // space
                    addrCharsWritten;           // address
            }
            else if(IsAddressAndHexCountSuffix)
            {
                var remainder = data;

                // command
                prefix.AsSpan().CopyTo(remainder);
                remainder = remainder[prefix.Length..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // address
                var res1 = addr.Value.TryFormat(remainder, out var charsWrittenAddr, "X2");
                Debug.Assert(res1);
                remainder = remainder[charsWrittenAddr..];

                // space
                remainder[0] = ' ';
                remainder = remainder[1..];

                // L
                remainder[0] = 'L';
                remainder = remainder[1..];

                // length (hex)
                var res2 = count.Value.TryFormat(remainder, out var charsWrittenLength, "X2");
                remainder = remainder[charsWrittenLength..];

                written =
                    prefix.Length +
                    1 +
                    charsWrittenAddr +
                    1 +
                    1 +
                    charsWrittenLength;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected style {style}");
            }

            newLine.AsSpan().CopyTo(data[written..]);

            var toWriteChars = data[0..(written + newLine.Length)];

            // an exact match isn't important, just something like close
            // since we're going to be writing pretty small messages, an inflation
            // of 
            var sizeBytes = toWriteChars.Length * MAX_BYTES_PER_CHAR;
            Span<byte> toWriteBytes = stackalloc byte[sizeBytes];

            var converted = encoding.GetBytes(toWriteChars, toWriteBytes);
            toWriteBytes = toWriteBytes[0..converted];
            
            stream.Write(toWriteBytes);
            stream.Flush();
        }

        public override string ToString()
        {
            // just for debugging, this can be crummy and allocate-y
            using var writer = new MemoryStream();
            Write(writer, Environment.NewLine, System.Text.Encoding.Unicode);

            var bytes = writer.ToArray();
            var str = System.Text.Encoding.Unicode.GetString(bytes);

            return str;
        }
    }
}
