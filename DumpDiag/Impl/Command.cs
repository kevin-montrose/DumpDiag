using System;
using System.Diagnostics;
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
        private readonly string prefix;
        private readonly int? count;
        private readonly string? infix;
        private readonly long? addr;

        private Command(string command, int? count, string? infix, long? addr)
        {
            this.prefix = command;
            this.count = count;
            this.infix = infix;
            this.addr = addr;
        }

        private Command(string command) : this(command, null, null, null) { }

        private Command(string command, long addr) : this(command, null, null, addr) { }

        private Command(string command, int count) : this(command, count, null, null) { }

        internal static Command CreateCommand(string command)
        => new Command(command);

        /// <summary>
        /// (command) (addr)
        /// </summary>
        internal static Command CreateCommandWithAddress(string command, long addr)
        => new Command(command, addr);

        /// <summary>
        /// (command) (count)
        /// </summary>
        internal static Command CreateCommandWithCount(string command, int count)
        => new Command(command, count);

        /// <summary>
        /// (prefix) (count) (infix) (count) (addr)
        /// 
        /// note that count is written twice
        /// </summary>
        internal static Command CreateCommandWithCountAndAdress(string commandPrefix, int count, string commandInfix, long addr)
        => new Command(commandPrefix, count, commandInfix, addr);

        internal void Write(TextWriter writer)
        {
            var newLine = writer.NewLine;

            var len = prefix.Length; // command

            if (count != null)
            {
                len += 1;               // space
                len += 10;              // length of int (decimal) [for count]
            }

            if (infix != null)
            {
                len += 1;               // space
                len += infix.Length;    // infix

                len += 1;               // space
                len += 10;              // length of int (decimal) [for count (again)]
            }

            if (addr != null)
            {
                len += 1;               // space
                len += 16;              // length of long (hex) [for addr]
            }

            len += newLine.Length;

            Span<char> data = stackalloc char[len];

            var dataLength = 0;
            prefix.AsSpan().CopyTo(data);
            dataLength += prefix.Length;

            if (count != null)
            {
                data[dataLength] = ' ';
                dataLength++;

                var formatRes = count.Value.TryFormat(data[dataLength..], out var writtenChars);
                Debug.Assert(formatRes);

                dataLength += writtenChars;
            }

            if (infix != null)
            {
                data[dataLength] = ' ';
                dataLength++;

                infix.AsSpan().CopyTo(data[dataLength..]);
                dataLength += infix.Length;

                data[dataLength] = ' ';
                dataLength++;

                if (count == null)
                {
                    throw new Exception("Expectation is count is always set if infix is set, this shouldn't be possible");
                }

                var formatRes = count.Value.TryFormat(data[dataLength..], out var writtenChars);
                Debug.Assert(formatRes);

                dataLength += writtenChars;
            }

            if (addr != null)
            {
                data[dataLength] = ' ';
                dataLength++;

                var formatRes = addr.Value.TryFormat(data[dataLength..], out var writtenChars, "X2");
                Debug.Assert(formatRes);

                dataLength += writtenChars;
            }

            newLine.AsSpan().CopyTo(data[dataLength..]);
            dataLength += newLine.Length;

            var toWrite = data[0..dataLength];

            writer.Write(toWrite);
        }

        public override string ToString()
        {
            using var writer = new StringWriter();
            Write(writer);

            return writer.ToString();
        }
    }
}
