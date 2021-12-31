using DumpDiag.Impl;
using System;

namespace DumpDiag.CommandLine
{
    internal sealed class ProgressWrapper : IProgress<DumpDiagnoserProgress>
    {
        private readonly Action<DumpDiagnoserProgress> del;

        internal ProgressWrapper(Action<DumpDiagnoserProgress> del)
        {
            this.del = del;
        }

        public void Report(DumpDiagnoserProgress progress)
        => del(progress);
    }
}
