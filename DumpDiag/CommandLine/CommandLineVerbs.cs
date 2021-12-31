using System.Runtime.Versioning;

namespace DumpDiag.CommandLine
{
    internal static class CommandLineVerbs
    {
        internal static readonly string DOTNET_DUMP = "dotnet-dump";

        [SupportedOSPlatform("windows")]
        internal static readonly string WINDBG = "windbg";
    }
}
