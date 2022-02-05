using System;

namespace DumpDiag.Impl
{
    // todo: don't love this
    /// <summary>
    /// Inverse of <see cref="IBufferWriter{T}"/>, which doesn't exist in .NET...
    /// </summary>
    internal interface IBufferReader<T>
    {
        /// <summary>
        /// Returns true if the stream has ended.
        /// 
        /// Repeated calls to this return the same data, until
        /// <see cref="Advance(int)"/> is called to mark the data
        /// consumed.
        /// </summary>
        bool Read(ref Span<T> readInto);
        
        /// <summary>
        /// Marks some amount of data as consumed.
        /// 
        /// If consumed is larger than the data returned in the last call to
        /// <see cref="Read(ref Span{T})"/> then behavior is undefined.
        /// </summary>
        void Advance(int consumed);
    }
}
