using System.Reflection;
using System.Runtime.InteropServices;

namespace QuantAnalyst.Native.Interop;

/// <summary>
/// Locates the qe shared library for the current RID. Order: <c>QE_NATIVE_PATH</c> (file or
/// directory), <c>runtimes/&lt;rid&gt;/native/</c> under the app base directory, the app base
/// directory itself, then the runtime's default probing.
/// </summary>
internal static class QeNativeLibraryResolver
{
    internal const string OverrideVariable = "QE_NATIVE_PATH";

    private static int registered;

    internal static string LibraryFileName =>
        OperatingSystem.IsWindows() ? "qe.dll"
        : OperatingSystem.IsMacOS() ? "libqe.dylib"
        : "libqe.so";

    internal static void Register()
    {
        if (Interlocked.Exchange(ref registered, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(QeNativeLibraryResolver).Assembly, Resolve);
        }
    }

    internal static string CurrentRid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : throw new PlatformNotSupportedException("qe supports Windows, Linux and macOS.");
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture other => throw new PlatformNotSupportedException($"qe does not support {other}."),
        };
        return $"{os}-{arch}";
    }

    internal static IReadOnlyList<string> CandidatePaths(string? overridePath, string baseDirectory)
    {
        var candidates = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            candidates.Add(Directory.Exists(overridePath)
                ? Path.Combine(overridePath, LibraryFileName)
                : overridePath);
        }

        candidates.Add(Path.Combine(baseDirectory, "runtimes", CurrentRid(), "native", LibraryFileName));
        candidates.Add(Path.Combine(baseDirectory, LibraryFileName));
        return candidates;
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != QeNative.LibraryName)
        {
            return 0;
        }

        IReadOnlyList<string> candidates = CandidatePaths(
            Environment.GetEnvironmentVariable(OverrideVariable), AppContext.BaseDirectory);
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                // A file that exists but fails to load is a real error (wrong arch, missing deps).
                return NativeLibrary.Load(candidate);
            }
        }

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint handle))
        {
            return handle;
        }

        throw new DllNotFoundException(
            $"Could not find the qe native library ({LibraryFileName}). Build it with " +
            "'cmake --preset dev && cmake --build --preset dev' (see docs/setup.md) or set " +
            $"{OverrideVariable}. Searched: {string.Join(", ", candidates)} and default probing.");
    }
}
