using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DumpDiag.Impl
{
    /// <summary>
    /// Used to take a full process dump using dotnet-dump with the collect command.
    /// </summary>
    internal sealed class DumpProcess
    {
        /// <summary>
        /// Take a dump of the given process (identified by id), storing it to the given file.
        /// </summary>
        internal static async ValueTask<(bool Success, string Log)> TakeDumpAsync(string dotNetDumpExecutable, int targetProcessId, string outputFile)
        {
            var info = new ProcessStartInfo();
            info.FileName = dotNetDumpExecutable;
            info.UseShellExecute = false;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.CreateNoWindow = true;
            info.Arguments = $"collect --process-id {targetProcessId} --type Full --output \"{ outputFile}\"";

            var proc = Process.Start(info);

            if (proc == null)
            {
                throw new Exception("Could not start process, this shouldn't be possible");
            }

            Job.Instance.AssociateProcess(proc);

            var readToEndTask = Task.Run(() => proc.StandardOutput.ReadToEndAsync());
            var waitForExitTask = proc.WaitForExitAsync();

            await Task.WhenAll(readToEndTask, waitForExitTask).ConfigureAwait(false);

            var success = proc.ExitCode == 0 && File.Exists(outputFile);

            var log = await readToEndTask.ConfigureAwait(false);

            return (success, log);
        }
    }
}
