using System.Runtime.InteropServices;

namespace QuantAnalyst.Native.Interop;

/// <summary>Struct ids for <c>qe_struct_layout</c> (<c>QE_STRUCT_*</c>).</summary>
internal enum QeStructId
{
    EngineConfig = 1,
    BsInput = 2,
    BsOutput = 3,
    LayoutInfo = 4,
}

/// <summary>Mirror of <c>qe_engine_config</c> (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct QeEngineConfig
{
    internal readonly int StructSize;
    internal readonly int Flags;
    internal readonly ulong Seed;

    internal QeEngineConfig(ulong seed)
        : this(QeEngineConfigSize, 0, seed)
    {
    }

    internal QeEngineConfig(int structSize, int flags, ulong seed)
    {
        StructSize = structSize;
        Flags = flags;
        Seed = seed;
    }

    internal static int QeEngineConfigSize => 16;
}

/// <summary>Mirror of <c>qe_struct_layout_info</c> (80 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct QeStructLayoutInfo
{
    internal const int MaxFields = 16;

    internal int Size;
    internal int Alignment;
    internal int FieldCount;
    internal int Reserved;
    internal fixed int Offsets[MaxFields];
}
