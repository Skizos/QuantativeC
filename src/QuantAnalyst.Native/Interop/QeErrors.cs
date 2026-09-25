using System.Text;

namespace QuantAnalyst.Native.Interop;

/// <summary>Maps <see cref="QeStatus"/> + <c>qe_last_error</c> to .NET exceptions.</summary>
internal static unsafe class QeErrors
{
    // qe_last_error messages are capped at 1023 bytes + NUL (native/src/api/last_error.hpp).
    private const int MaxMessageBytes = 1024;

    /// <summary>Reads the calling thread's last native error. Call immediately after the failure.</summary>
    internal static string GetLastErrorMessage()
    {
        int required = 0;
        _ = QeNative.LastError(null, 0, &required);
        if (required <= 1)
        {
            return string.Empty;
        }

        Span<byte> buffer = stackalloc byte[Math.Min(required, MaxMessageBytes)];
        fixed (byte* p = buffer)
        {
            if (QeNative.LastError(p, buffer.Length, &required) != QeStatus.Ok)
            {
                return string.Empty;
            }
        }

        return Encoding.UTF8.GetString(buffer[..(required - 1)]);
    }

    internal static QeException CreateException(QeStatus status, string operation) =>
        new(status, operation, GetLastErrorMessage());

    internal static void ThrowIfFailed(QeStatus status, string operation)
    {
        if (status != QeStatus.Ok)
        {
            throw CreateException(status, operation);
        }
    }
}
