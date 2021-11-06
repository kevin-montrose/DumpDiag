using System;
using System.Diagnostics.CodeAnalysis;

namespace DumpDiag.Impl
{
    internal readonly struct HeapGCHandle : IEquatable<HeapGCHandle>
    {
        internal enum HandleTypes : byte
        {
            None = 0,

            Pinned,
            RefCounted,
            WeakShort,
            WeakLong,
            Strong,
            Variable,
            AsyncPinned,
            SizedRef,
            Dependent
        }

        internal long HandleAddress { get; }
        internal HandleTypes HandleType { get; }
        internal long ObjectAddress { get; }
        internal int Size { get; }


        private readonly string? type;
        internal string TypeHint
        {
            get
            {
                if (type == null)
                {
                    throw new InvalidOperationException($"{nameof(TypeHint)} not initialized");
                }

                return type;
            }
        }

        private readonly long? methodTable;
        internal long MethodTable
        {
            get
            {
                if (!MethodTableInitialized)
                {
                    throw new InvalidOperationException($"{nameof(MethodTable)} not initialized");
                }

                return methodTable.Value;
            }
        }

        [MemberNotNullWhen(true, nameof(methodTable))]
        internal bool MethodTableInitialized => methodTable != null;

        internal HeapGCHandle(long handleAddr, HandleTypes handleType, long objAddr, string type, int size) : this(handleAddr, handleType, objAddr, type, size, null)
        { }

        private HeapGCHandle(long handleAddr, HandleTypes handleType, long objAddr, string? type, int size, long? methodTable)
        {
            HandleAddress = handleAddr;
            HandleType = handleType;
            ObjectAddress = objAddr;
            Size = size;
            this.type = type;
            this.methodTable = methodTable;
        }

        public override string ToString()
        => $"{nameof(HandleAddress)}: {HandleAddress:X2}, {nameof(HandleType)}: {HandleType}, {nameof(ObjectAddress)}: {ObjectAddress:X2}, {nameof(TypeHint)}: {type}, {nameof(MethodTable)}: {methodTable}, {nameof(Size)}: {Size}";

        internal HeapGCHandle SetMethodTable(long methodTable)
        => new HeapGCHandle(HandleAddress, HandleType, ObjectAddress, null, Size, methodTable);

        public bool Equals(HeapGCHandle other)
        => other.HandleAddress == HandleAddress &&
           other.HandleType == HandleType &&
           other.ObjectAddress == ObjectAddress &&
           other.Size == Size &&
           other.type == type &&
           other.methodTable == methodTable;

        public override bool Equals(object? obj)
        => obj is HeapGCHandle other && Equals(other);

        public override int GetHashCode()
        => HashCode.Combine(HandleAddress, HandleType, ObjectAddress, type, methodTable, Size);
    }
}
