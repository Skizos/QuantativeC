using System.Runtime.InteropServices;
using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native.Tests;

public sealed class ResolverTests
{
    [Fact]
    public void CurrentRid_MatchesPlatform()
    {
        string rid = QeNativeLibraryResolver.CurrentRid();

        string expectedArch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        Assert.EndsWith("-" + expectedArch, rid, StringComparison.Ordinal);
        Assert.StartsWith(
            OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-",
            rid,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CandidatePaths_PreferOverrideThenRuntimesFolderThenBaseDirectory()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "qe-base");
        string overrideFile = Path.Combine(Path.GetTempPath(), "custom", QeNativeLibraryResolver.LibraryFileName);

        IReadOnlyList<string> paths = QeNativeLibraryResolver.CandidatePaths(overrideFile, baseDir);

        Assert.Equal(
            [
                overrideFile,
                Path.Combine(baseDir, "runtimes", QeNativeLibraryResolver.CurrentRid(), "native", QeNativeLibraryResolver.LibraryFileName),
                Path.Combine(baseDir, QeNativeLibraryResolver.LibraryFileName),
            ],
            paths);
    }

    [Fact]
    public void CandidatePaths_WithoutOverride_StartWithRuntimesFolder()
    {
        IReadOnlyList<string> paths = QeNativeLibraryResolver.CandidatePaths(null, AppContext.BaseDirectory);

        Assert.Equal(2, paths.Count);
        Assert.Contains($"runtimes{Path.DirectorySeparatorChar}", paths[0], StringComparison.Ordinal);
    }

    [Fact]
    public void StagedLibrary_IsCopiedIntoTestOutput()
    {
        string expected = QeNativeLibraryResolver.CandidatePaths(null, AppContext.BaseDirectory)[0];

        Assert.True(File.Exists(expected), $"Expected the staged native library at {expected}.");
    }
}
