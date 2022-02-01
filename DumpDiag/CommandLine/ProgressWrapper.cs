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

            writer.Write($"executed commands {progress.TotalCommandsExecuted:N0}");

            var hasWrittenOutput = true;

            WriteLine(writer, "starting", progress.PercentStartingTasks, ref hasWrittenOutput, ref startingDone);
            WriteLine(writer, "char[]s", progress.PercentCharacterArrays, ref hasWrittenOutput, ref charsDone);
            WriteLine(writer, "delegates stats", progress.PercentDelegateDetails, ref hasWrittenOutput, ref delegateStatsDone);
            WriteLine(writer, "finding delegates", progress.PercentDeterminingDelegates, ref hasWrittenOutput, ref delegateFindingDone);
            WriteLine(writer, "scanning heap", progress.PercentLoadHeap, ref hasWrittenOutput, ref heapScanDone);
            WriteLine(writer, "strings", progress.PercentStrings, ref hasWrittenOutput, ref stringsDone);
            WriteLine(writer, "thread count", progress.PercentThreadCount, ref hasWrittenOutput, ref threadCountDone);
            WriteLine(writer, "thread details", progress.PercentThreadDetails, ref hasWrittenOutput, ref threadDetailsDone);
            WriteLine(writer, "type details", progress.PercentTypeDetails, ref hasWrittenOutput, ref typeDetailsDone);
            WriteLine(writer, "async details", progress.PercentAsyncDetails, ref hasWrittenOutput, ref asyncDetailsDone);
            WriteLine(writer, "heap assignments", progress.PercentHeapAssignments, ref hasWrittenOutput, ref heapAssignmentDone);
            WriteLine(writer, "pins", progress.PercentAnalyzingPins, ref hasWrittenOutput, ref pinsDone);

            if (hasWrittenOutput)
            {
                writer.WriteLine();
            }

            static void WriteLine(TextWriter writer, string prefix, double percent, ref bool needsComma, ref bool done)
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

                if (needsComma)
                {
                    writer.Write(", ");
                }
                else
                {
                    // first part needs the time
                    writer.Write($"[{DateTime.UtcNow:u}]: ");
                }

                writer.Write(prefix);
                writer.Write(": ");
                writer.Write(percent);
                writer.Write("%");

                needsComma = true;

                if (isOneHundred)
                {
                    done = true;
                }
            }
        }
    }
}
