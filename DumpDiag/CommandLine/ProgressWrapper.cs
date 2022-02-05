using DumpDiag.Impl;
using System;
using System.IO;

namespace DumpDiag.CommandLine
{
    internal sealed class ProgressWrapper : IProgress<DumpDiagnoserProgress>
    {
        private readonly bool quiet;
        private readonly TextWriter writer;

        private bool startingDone;
        private bool charsDone;
        private bool delegateStatsDone;
        private bool delegateFindingDone;
        private bool heapScanDone;
        private bool stringsDone;
        private bool threadCountDone;
        private bool threadDetailsDone;
        private bool typeDetailsDone;
        private bool asyncDetailsDone;
        private bool heapAssignmentDone;
        private bool pinsDone;

        internal ProgressWrapper(bool quiet, TextWriter writer)
        {
            this.quiet = quiet;
            this.writer = writer;
        }

        public void Report(DumpDiagnoserProgress progress)
        {
            if (quiet)
            {
                return;
            }

            writer.Write($"[{DateTime.UtcNow:u}] ");
            writer.Write($"executed commands {progress.TotalCommandsExecuted:N0}");

            WriteProgress(writer, "starting", progress.PercentStartingTasks, ref startingDone);
            WriteProgress(writer, "char[]s", progress.PercentCharacterArrays, ref charsDone);
            WriteProgress(writer, "delegates stats", progress.PercentDelegateDetails, ref delegateStatsDone);
            WriteProgress(writer, "finding delegates", progress.PercentDeterminingDelegates, ref delegateFindingDone);
            WriteProgress(writer, "scanning heap", progress.PercentLoadHeap, ref heapScanDone);
            WriteProgress(writer, "strings", progress.PercentStrings, ref stringsDone);
            WriteProgress(writer, "thread count", progress.PercentThreadCount, ref threadCountDone);
            WriteProgress(writer, "thread details", progress.PercentThreadDetails, ref threadDetailsDone);
            WriteProgress(writer, "type details", progress.PercentTypeDetails, ref typeDetailsDone);
            WriteProgress(writer, "async details", progress.PercentAsyncDetails, ref asyncDetailsDone);
            WriteProgress(writer, "heap assignments", progress.PercentHeapAssignments, ref heapAssignmentDone);
            WriteProgress(writer, "pins", progress.PercentAnalyzingPins, ref pinsDone);

            writer.WriteLine();

            static void WriteProgress(TextWriter writer, string prefix, double percent, ref bool done)
            {
                var isStarted = percent > 0;
                var isOneHundred = percent >= 100;

                // write the result if we're started but not finished, OR
                // we just hit 100 for the first time
                var shouldWrite = (isStarted && !isOneHundred) || (isOneHundred && !done);

                if (!shouldWrite)
                {
                    return;
                }

                writer.Write(", ");

                writer.Write(prefix);
                writer.Write(": ");
                writer.Write(percent);
                writer.Write("%");

                if (isOneHundred)
                {
                    done = true;
                }
            }
        }
    }
}
