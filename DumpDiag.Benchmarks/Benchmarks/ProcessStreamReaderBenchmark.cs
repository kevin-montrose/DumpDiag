using BenchmarkDotNet.Attributes;
using DumpDiag.Impl;
using System;
using System.Buffers;
using System.Diagnostics;

namespace DumpDiag.Benchmarks.Benchmarks
{
    [MemoryDiagnoser]
    public class ProcessStreamReaderBenchmark
    {
        const int NUM_LINES = 10_000;

        private static Process StartProcess()
        {
            var procInfo = new ProcessStartInfo();
            procInfo.FileName = "powershell.exe";
            procInfo.Arguments = "-Command \"for($i=0; $i -lt " + NUM_LINES + "; $i++) { Write-Output \\\"line #$i\\\" }\"";
            procInfo.UseShellExecute = false;
            procInfo.CreateNoWindow = true;
            procInfo.RedirectStandardOutput = true;

            var proc = Process.Start(procInfo);

            Job.Instance.AssociateProcess(proc);

            return proc;
        }

        [Benchmark]
        public void StreamReader()
        {
            using var proc = StartProcess();

            var reader = proc.StandardOutput;

            var expectedValue = 0;

            string line = null;
            while ((line = reader.ReadLine()) != null)
            {
                var sequence = new ReadOnlySequence<char>(line.AsMemory());
                if (!TryParse(sequence, out var val))
                {
                    throw new Exception("Unexpected line");
                }

                if (expectedValue != val)
                {
                    throw new Exception("Unexpected value");
                }

                expectedValue++;
            }

            if (expectedValue != NUM_LINES)
            {
                throw new Exception("Not enough lines");
            }
        }

        [Benchmark]
        public void ProcessStreamReader()
        {
            using var proc = StartProcess();

            using var reader = new ProcessStreamReader(ArrayPool<char>.Shared, proc.StandardOutput.BaseStream, proc.StandardOutput.CurrentEncoding, Environment.NewLine);

            var expectedValue = 0;

            foreach (var line in reader.ReadAllLines())
            {
                using var lineRef = line;
                var sequence = lineRef.GetSequence();
                if (!TryParse(sequence, out var val))
                {
                    throw new Exception("Unexpected line");
                }

                if (expectedValue != val)
                {
                    throw new Exception("Unexpected value");
                }

                expectedValue++;
            }


            if (expectedValue != NUM_LINES)
            {
                throw new Exception("Not enough lines");
            }
        }

        private static bool TryParse(ReadOnlySequence<char> seq, out int line)
        {
            // pattern is: ^ line \s #\d+

            var reader = new SequenceReader<char>(seq);
            if (!reader.TryReadTo(out ReadOnlySequence<char> lineStr, ' ', advancePastDelimiter: true))
            {
                line = default;
                return false;
            }

            if (!lineStr.Equals("line", StringComparison.Ordinal))
            {
                line = default;
                return false;
            }

            if (!reader.TryAdvanceTo('#', advancePastDelimiter: true))
            {
                line = default;
                return false;
            }

            var digits = reader.UnreadSequence;

            if (!digits.TryParseDecimalInt(out line))
            {
                line = default;
                return false;
            }

            return true;
        }
    }
}
