using BenchmarkDotNet.Running;
using System.Threading.Tasks;

namespace DumpDiag.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        }
    }
}
