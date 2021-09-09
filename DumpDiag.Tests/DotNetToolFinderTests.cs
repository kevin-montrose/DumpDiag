using DumpDiag.Impl;
using System.IO;
using Xunit;

namespace DumpDiag.Tests
{
    public class DotNetToolFinderTests
    {
        [Fact]
        public void Found()
        {
            // if this is failing for you, make sure you've actually installed dotnet-dump

            var res = DotNetToolFinder.TryFind("dotnet-dump", out var path, out var error);
            Assert.True(res);
            Assert.True(File.Exists(path));
            Assert.Null(error);
        }

        [Fact]
        public void NotFound()
        {
            var res = DotNetToolFinder.TryFind("dotnet-does-not-exist", out var path, out var error);
            Assert.False(res);
            Assert.Null(path);
            Assert.Equal("Tool (dotnet-does-not-exist) not found in (C:\\Users\\kevin\\.dotnet\\tools); install with `dotnet tool install --global dotnet-does-not-exist`", error);
        }
    }
}
