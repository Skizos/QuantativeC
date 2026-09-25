using Microsoft.Win32.SafeHandles;

namespace QuantAnalyst.Native.Interop;

/// <summary>Owns a native <c>qe_engine*</c>; released exactly once via <c>qe_engine_destroy</c>.</summary>
internal sealed class QeEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new instance. Called by the P/Invoke marshaller for out parameters.</summary>
    public QeEngineHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => QeNative.EngineDestroy(handle) == QeStatus.Ok;
}
