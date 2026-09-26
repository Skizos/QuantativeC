using Microsoft.Win32.SafeHandles;

namespace QuantAnalyst.Native.Interop;

/// <summary>Owns a native <c>qe_backtest*</c>; released exactly once via <c>qe_bt_destroy</c>.</summary>
internal sealed class QeBacktestHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes a new instance. Called by the P/Invoke marshaller for out parameters.</summary>
    public QeBacktestHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => QeNative.BtDestroy(handle) == QeStatus.Ok;
}
