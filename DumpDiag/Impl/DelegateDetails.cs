using System;
using System.Collections.Immutable;
using System.Linq;

namespace DumpDiag.Impl
{
    internal readonly struct DelegateDetails : IEquatable<DelegateDetails>
    {
        internal HeapEntry HeapEntry { get; }
        internal ImmutableArray<DelegateMethodDetails> MethodDetails { get; }

        internal DelegateDetails(HeapEntry he, ImmutableArray<DelegateMethodDetails> mds)
        {
            HeapEntry = he;
            MethodDetails = mds;
        }

        public bool Equals(DelegateDetails other)
        {
            if (!other.HeapEntry.Equals(HeapEntry))
            {
                return false;
            }

            return other.MethodDetails.SequenceEqual(MethodDetails);
        }

        public override bool Equals(object obj)
        => obj is DelegateDetails other && Equals(other);

        public override int GetHashCode()
        {
            var ret = new HashCode();
            ret.Add(HeapEntry);

            foreach (var item in MethodDetails)
            {
                ret.Add(item);
            }

            return ret.ToHashCode();
        }

        public override string ToString()
        => $"{HeapEntry} {string.Join(", ", MethodDetails)}";
    }

    internal readonly struct DelegateMethodDetails : IEquatable<DelegateMethodDetails>
    {
        internal long TargetAddress { get; }
        internal long MethodTable { get; }
        internal string BackingMethodName { get; }

        internal DelegateMethodDetails(long target, long mtd, string mtdName)
        {
            TargetAddress = target;
            MethodTable = mtd;
            BackingMethodName = mtdName;
        }

        public bool Equals(DelegateMethodDetails other)
        => other.TargetAddress == TargetAddress && other.MethodTable == MethodTable && other.BackingMethodName == BackingMethodName;

        public override bool Equals(object obj)
        => obj is DelegateMethodDetails other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(TargetAddress, MethodTable, BackingMethodName);

        public override string ToString()
        => $"{TargetAddress:X2} {MethodTable:X2} {BackingMethodName}";
    }
}
