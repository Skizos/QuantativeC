using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QuantAnalyst.Native.Interop;

/// <summary>
/// Source-generated P/Invokes for native/include/qe_api.h. The only place that declares native
/// entry points (ADR 0001). Callers go through <see cref="QeEngine"/> / <see cref="QeAbi"/>.
/// </summary>
internal static unsafe partial class QeNative
{
    internal const string LibraryName = "qe";

#pragma warning disable CA1810 // The static constructor exists to install the resolver before the first P/Invoke.
    static QeNative() => QeNativeLibraryResolver.Register();
#pragma warning restore CA1810

    [LibraryImport(LibraryName, EntryPoint = "qe_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus AbiVersion(int* major, int* minor);

    [LibraryImport(LibraryName, EntryPoint = "qe_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus LastError(byte* buffer, int capacity, int* required);

    [LibraryImport(LibraryName, EntryPoint = "qe_struct_layout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus StructLayout(QeStructId structId, QeStructLayoutInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "qe_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus EngineCreate(QeEngineConfig* config, out QeEngineHandle engine);

    [LibraryImport(LibraryName, EntryPoint = "qe_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus EngineDestroy(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "qe_bs_price_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BsPriceBatch(
        QeEngineHandle engine,
        BlackScholesInput* inputs,
        BlackScholesOutput* outputs,
        long count,
        long* failedCount);
}
