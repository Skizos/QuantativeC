namespace QuantAnalyst.Native;

/// <summary>A qe C ABI call returned a non-OK <see cref="QeStatus"/>.</summary>
public class QeException : Exception
{
    /// <summary>Initializes a new instance with status <see cref="QeStatus.Internal"/>.</summary>
    public QeException()
        : this("qe native call failed.")
    {
    }

    /// <summary>Initializes a new instance with status <see cref="QeStatus.Internal"/>.</summary>
    public QeException(string message)
        : base(message)
    {
        Status = QeStatus.Internal;
        Operation = string.Empty;
        NativeMessage = string.Empty;
    }

    /// <summary>Initializes a new instance with status <see cref="QeStatus.Internal"/>.</summary>
    public QeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = QeStatus.Internal;
        Operation = string.Empty;
        NativeMessage = string.Empty;
    }

    /// <summary>Initializes a new instance for a failed native operation.</summary>
    public QeException(QeStatus status, string operation, string nativeMessage)
        : base(FormatMessage(status, operation, nativeMessage))
    {
        Status = status;
        Operation = operation;
        NativeMessage = nativeMessage;
    }

    /// <summary>Gets the status the native call returned.</summary>
    public QeStatus Status { get; }

    /// <summary>Gets the C ABI function that failed, e.g. <c>qe_bs_price_batch</c>.</summary>
    public string Operation { get; }

    /// <summary>Gets the message from <c>qe_last_error</c>, empty if none was recorded.</summary>
    public string NativeMessage { get; }

    private static string FormatMessage(QeStatus status, string operation, string nativeMessage) =>
        string.IsNullOrEmpty(nativeMessage)
            ? $"{operation} failed with {status}."
            : $"{operation} failed with {status}: {nativeMessage}";
}
