using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DumpDiag.Impl
{
    /// <summary>
    /// A chunk of memory as a ring buffer.
    /// 
    /// Meant for moving bytes from a stream and then
    /// quickly converting them to chars.
    /// </summary>
    internal unsafe struct RingBuffer
    {
        private const int ByteBufferSize = 1024 * 4;

        private fixed byte buffer[ByteBufferSize];
        private int writeToIndex;
        private int readFromIndex;

        internal bool HasData { get; private set; }

        /// <summary>
        /// Returns true if there may be more data on the stream, and false if the stream has ended.
        /// 
        /// After every successfull read, it is expected that at least one call to <see cref="Convert(Decoder, Span{char}, out int)"/> will be made.
        /// </summary>
        internal bool Read(Stream stream)
        {
            var startWritingIndex = writeToIndex;
            int stopWritingIndex;

            if (readFromIndex > writeToIndex)
            {
                // blah <uninitialized> blah blah blah
                //      ^               ^
                //      |               +--readFromIndex
                //      +--writeToIndex

                stopWritingIndex = readFromIndex;
            }
            else if (readFromIndex < writeToIndex)
            {
                // <uninitialize>  blah blah <uninitialized>
                //                 ^         ^
                //                 |         +--writeToIndex
                //                 +--readFromIndex

                stopWritingIndex = ByteBufferSize;
            }
            else
            {
                // writeIndex == readFromIndex
                // which means we _could_ have data... but then calling Read is illegal
                // since there's no space in the buffer
                Debug.Assert(!HasData);

                // so assume the buffer is empty and we can write to the whole thing
                stopWritingIndex = ByteBufferSize;
            }

            int read;
            fixed (byte* bufferPtr = buffer)
            {
                var asSpan = new Span<byte>(bufferPtr, ByteBufferSize);
                var writeToSpan = asSpan[startWritingIndex..stopWritingIndex];

                read = stream.Read(writeToSpan);
            }

            if (read == 0)
            {
                return false;
            }

            writeToIndex += read;
            HasData = true;

            // can't issue a second read, because we might not make progress if we do

            if (writeToIndex == ByteBufferSize)
            {
                writeToIndex = 0;
            }

            return true;
        }

        /// <summary>
        /// Converts some amount of what is in the ring buffer into characters stored in <paramref name="writeInto"/>.
        /// 
        /// It is only legally to call this while <see cref="HasData"/> is true.
        /// 
        /// <paramref name="charsConsumed"/> is set to the number of newly converted characters stored into <paramref name="writeInto"/>.
        /// 
        /// Set <paramref name="doneReading"/> if no more calls to <see cref="Read(Stream)"/> will be made.
        /// 
        /// <paramref name="finished"/> will be set if <paramref name="doneReading"/> is set, all data is consumed, and <paramref name="decoder"/> 
        /// indicates it has finished converting all provided data.
        /// </summary>
        internal void Convert(Decoder decoder, Span<char> writeInto, bool doneReading, out int charsConsumed, out bool finished)
        {
            charsConsumed = 0;

            var remainingToWriteInto = writeInto;

            var lastCompleted = false;
            fixed (byte* bufferPtr = buffer)
            {
                // loop until we've filled the char buffer...
                while (!remainingToWriteInto.IsEmpty)
                {
                    DetermineReadIndexes(HasData, readFromIndex, writeToIndex, HasData, out var startReadingIndex, out var stopReadingIndex, out var totalDataAvailable);

                    var asSpan = new ReadOnlySpan<byte>(bufferPtr, ByteBufferSize);
                    var fullReadableSpan = asSpan[startReadingIndex..stopReadingIndex];

                    // convert (one pass)
                    decoder.Convert(fullReadableSpan, remainingToWriteInto, doneReading, out var bytesUsed, out var charsWritten, out var completed);

                    charsConsumed += charsWritten;
                    lastCompleted = completed;

                    // update where we can write...
                    remainingToWriteInto = remainingToWriteInto.Slice(charsWritten);

                    // update our read pointer
                    readFromIndex += bytesUsed;
                    if (readFromIndex == ByteBufferSize)
                    {
                        // wrapping around if need be
                        readFromIndex = 0;
                    }

                    if (readFromIndex == writeToIndex)
                    {
                        // we're out of data, so we can't put anything else in the char buffer

                        // use the whole buffer next time we read
                        readFromIndex = writeToIndex = 0;

                        HasData = false;
                        break;
                    }
                }
            }

            finished = !HasData && doneReading && lastCompleted;

            // figure out the next contiguous piece of the buffer we can try and convert
            static void DetermineReadIndexes(
                bool hasData,
                int readFromIndex,
                int writeToIndex,
                bool assumeHasData,
                out int startReadingIndex,
                out int stopReadingIndex,
                out int totalDataAvailable)
            {
                startReadingIndex = readFromIndex;

                if (readFromIndex > writeToIndex)
                {
                    // blah <uninitialized> blah blah blah
                    //      ^               ^
                    //      |               +--readFromIndex
                    //      +--writeToIndex

                    stopReadingIndex = ByteBufferSize;
                    totalDataAvailable = (ByteBufferSize - readFromIndex) + writeToIndex;
                }
                else if (readFromIndex < writeToIndex)
                {
                    // <uninitialize>  blah blah <uninitialized>
                    //                 ^         ^
                    //                 |         +--writeToIndex
                    //                 +--readFromIndex

                    stopReadingIndex = writeToIndex;
                    totalDataAvailable = writeToIndex - readFromIndex;
                }
                else
                {
                    // writeIndex == readFromIndex

                    if (hasData)
                    {
                        // there is data, so it must fill the buffer
                        stopReadingIndex = ByteBufferSize;
                        totalDataAvailable = ByteBufferSize;
                    }
                    else
                    {
                        // there is no data
                        stopReadingIndex = startReadingIndex;
                        totalDataAvailable = 0;
                    }
                }
            }
        }

        /// <summary>
        /// Intialize a new <see cref="RingBuffer"/>.
        /// </summary>
        internal static void Intialize(ref RingBuffer ret)
        {
            ret = new RingBuffer();
            ret.writeToIndex = 0;
            ret.readFromIndex = 0;
        }
    }
}
