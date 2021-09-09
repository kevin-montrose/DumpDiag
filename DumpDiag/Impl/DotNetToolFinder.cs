using System;
using System.IO;
using System.Linq;

namespace DumpDiag.Impl
{
    internal static class DotNetToolFinder
    {
        internal static bool TryFind(string toolName, out string executablePath, out string error)
        {
            var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var toolsDir = Path.Combine(userDir, ".dotnet", "tools");

            if(!Directory.Exists(toolsDir))
            {
                executablePath = null;
                error = $"Could not find ({toolsDir}), which should contain .NET tools";
                return false;
            }

            var candidates = Directory.GetFiles(toolsDir).Where(x => Path.GetFileNameWithoutExtension(x) == toolName).ToList();

            if(candidates.Count == 0)
            {
                executablePath = null;
                error = $"Tool ({toolName}) not found in ({toolsDir}); install with `dotnet tool install --global {toolName}`";
                return false;
            }
            else if(candidates.Count > 1)
            {
                executablePath = null;
                error = $"Multiple candidates for ({toolName}) found in ({toolsDir}): {string.Join(", ", candidates.Select(static x => $"({x})"))}";
                return false;
            }

            executablePath = candidates.Single();
            error = null;
            return true;
        }
    }
}
