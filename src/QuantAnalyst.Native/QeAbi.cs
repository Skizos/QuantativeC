using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native;

/// <summary>ABI version handshake with the qe native library (ADR 0001).</summary>
public static class QeAbi
{
    /// <summary>The <c>QE_ABI_MAJOR</c> this assembly was written against.</summary>
    public const int ExpectedMajor = 1;

    /// <summary>The minimum <c>QE_ABI_MINOR</c> this assembly requires (1.2: backtest engine; 1.3: per-instrument courtage; 1.4: intraday fills, per-instrument spread).</summary>
    public const int ExpectedMinor = 4;

    private static readonly Lazy<Version> NativeVersionLazy = new(ReadNativeVersion);

    /// <summary>Gets the ABI version reported by the loaded native library.</summary>
    public static Version NativeVersion => NativeVersionLazy.Value;

    /// <summary>Same major and at least the expected minor.</summary>
    public static bool IsCompatible(int nativeMajor, int nativeMinor) =>
        nativeMajor == ExpectedMajor && nativeMinor >= ExpectedMinor;

    /// <summary>Throws <see cref="NativeAbiMismatchException"/> unless the loaded library is compatible.</summary>
    public static void EnsureCompatible()
    {
        Version native = NativeVersion;
        if (!IsCompatible(native.Major, native.Minor))
        {
            throw new NativeAbiMismatchException(
                $"qe native library implements ABI {native.Major}.{native.Minor}, but QuantAnalyst.Native " +
                $"requires {ExpectedMajor}.{ExpectedMinor}+ with the same major version. Rebuild the native library: .\\build.ps1 on Windows " +
                "(cmake --build --preset dev elsewhere), or run qa through the .\\qa launcher, which rebuilds it when its sources changed.");
        }
    }

    internal static unsafe QeStructLayoutInfo GetLayout(QeStructId id)
    {
        QeStructLayoutInfo info;
        QeErrors.ThrowIfFailed(QeNative.StructLayout(id, &info), "qe_struct_layout");
        return info;
    }

    private static unsafe Version ReadNativeVersion()
    {
        int major;
        int minor;
        QeErrors.ThrowIfFailed(QeNative.AbiVersion(&major, &minor), "qe_abi_version");
        return new Version(major, minor);
    }
}
