namespace DumpDiag.Impl
{
    internal readonly struct EEClassDetails
    {
        internal string ClassName { get; }
        internal long ParentEEClass { get; }

        internal EEClassDetails(string className, long parent)
        {
            ClassName = className;
            ParentEEClass = parent;
        }

        public override string ToString()
        => $"{ParentEEClass:X2} {ClassName}";
    }
}