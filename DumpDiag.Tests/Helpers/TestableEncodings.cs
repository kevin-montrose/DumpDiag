using DumpDiag.Impl;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DumpDiag.Tests.Helpers
{
    internal static class TestableEncodings
    {
        internal static IEnumerable<Encoding> Encodings =
            new Encoding[]
            {
                GetStandardOutputEncoding(),
                new ASCIIEncoding(),
                new UTF8Encoding(false),
                new UnicodeEncoding(false, false),
                new UnicodeEncoding(true, false),
                new UTF32Encoding(false, false),
                new UTF32Encoding(true, false),
            };

        private static Encoding GetStandardOutputEncoding()
        {
            if (!DotNetToolFinder.TryFind("dotnet-dump", out var executablePath, out var error))
            {
                throw new InvalidOperation(error);
            }

            var procInfo = new ProcessStartInfo();
            procInfo.RedirectStandardOutput = true;
            procInfo.UseShellExecute = false;
            procInfo.RedirectStandardOutput = true;
            procInfo.CreateNoWindow = true;
            procInfo.FileName = executablePath;
            procInfo.ArgumentList.Add("--help");

            using var proc = Process.Start(procInfo);

            Job.Instance.AssociateProcess(proc);

            var ret = proc.StandardOutput.CurrentEncoding;

            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            return ret;
        }
    }
}
