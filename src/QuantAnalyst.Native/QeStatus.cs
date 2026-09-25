namespace QuantAnalyst.Native;

/// <summary>Status codes of the qe C ABI (<c>qe_status</c> in native/include/qe_api.h).</summary>
public enum QeStatus
{
    /// <summary><c>QE_OK</c>.</summary>
    Ok = 0,

    /// <summary><c>QE_E_INVALID_ARG</c>: null pointer, bad size or out-of-domain input.</summary>
    InvalidArgument = 1,

    /// <summary><c>QE_E_NUMERIC</c>: non-finite result from finite inputs.</summary>
    Numeric = 2,

    /// <summary><c>QE_E_OUT_OF_MEMORY</c>.</summary>
    OutOfMemory = 3,

    /// <summary><c>QE_E_BUFFER_TOO_SMALL</c>.</summary>
    BufferTooSmall = 4,

    /// <summary><c>QE_E_INTERNAL</c>: unexpected C++ exception or invariant violation.</summary>
    Internal = 5,
}
