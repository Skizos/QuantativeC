namespace QuantAnalyst.Native;

/// <summary>The loaded qe library implements an ABI version this assembly cannot use.</summary>
public class NativeAbiMismatchException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public NativeAbiMismatchException()
        : this("The qe native library ABI version is incompatible.")
    {
    }

    /// <summary>Initializes a new instance.</summary>
    public NativeAbiMismatchException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance.</summary>
    public NativeAbiMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
