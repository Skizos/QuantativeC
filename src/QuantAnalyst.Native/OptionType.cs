namespace QuantAnalyst.Native;

/// <summary>Option type as encoded in the C ABI (<c>QE_OPTION_CALL</c> / <c>QE_OPTION_PUT</c>).</summary>
public enum OptionType
{
    /// <summary>European call.</summary>
    Call = 0,

    /// <summary>European put.</summary>
    Put = 1,
}
