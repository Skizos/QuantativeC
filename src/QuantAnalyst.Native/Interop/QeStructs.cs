using System.Runtime.InteropServices;

namespace QuantAnalyst.Native.Interop;

/// <summary>Struct ids for <c>qe_struct_layout</c> (<c>QE_STRUCT_*</c>).</summary>
internal enum QeStructId
{
    EngineConfig = 1,
    BsInput = 2,
    BsOutput = 3,
    LayoutInfo = 4,
    BsGreeks = 5,
    IvInput = 6,
    IvOutput = 7,
    LatticeInput = 8,
    McConfig = 9,
    McResult = 10,
    CovConfig = 11,
    VarEs = 12,
    OptConfig = 13,
    OptResult = 14,
    RebalanceAsset = 15,
    RebalanceConfig = 16,
    RebalanceTrade = 17,
    RebalanceSummary = 18,
}

/// <summary>Mirror of <c>qe_mc_config</c> (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct QeMcConfig
{
    internal const int Antithetic = 1;
    internal const int ControlVariate = 2;
    internal const int Sobol = 4;

    internal readonly int StructSize;
    internal readonly int Flags;
    internal readonly long Paths;
    internal readonly ulong Seed;
    internal readonly int Replications;
    internal readonly int Steps;

    internal QeMcConfig(int flags, long paths, ulong seed, int replications, int steps)
    {
        StructSize = 32;
        Flags = flags;
        Paths = paths;
        Seed = seed;
        Replications = replications;
        Steps = steps;
    }
}

/// <summary>Mirror of <c>qe_cov_config</c> (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct QeCovConfig
{
    internal readonly int StructSize;
    internal readonly int Method;
    internal readonly double EwmaLambda;

    internal QeCovConfig(CovarianceMethod method, double ewmaLambda)
    {
        StructSize = 16;
        Method = (int)method;
        EwmaLambda = ewmaLambda;
    }
}

/// <summary>Mirror of <c>qe_opt_config</c> (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct QeOptConfig
{
    internal readonly int StructSize;
    internal readonly int Method;
    internal readonly double RiskAversion;
    internal readonly double Tolerance;
    internal readonly int MaxIterations;
    internal readonly int Reserved;

    internal QeOptConfig(OptimizationMethod method, double riskAversion, double tolerance, int maxIterations)
    {
        StructSize = 32;
        Method = (int)method;
        RiskAversion = riskAversion;
        Tolerance = tolerance;
        MaxIterations = maxIterations;
        Reserved = 0;
    }
}

/// <summary>Mirror of <c>qe_opt_result</c> (16 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct QeOptResult
{
    internal double Objective;
    internal int Iterations;
    internal int Converged;
}

/// <summary>Mirror of <c>qe_rebalance_config</c> (48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct QeRebalanceConfig
{
    internal readonly int StructSize;
    internal readonly int Reserved;
    internal readonly double Cash;
    internal readonly double CashBuffer;
    internal readonly double MinTradeValue;
    internal readonly double FeeMin;
    internal readonly double FeeRate;

    internal QeRebalanceConfig(RebalanceOptions options)
    {
        StructSize = 48;
        Reserved = 0;
        Cash = options.Cash;
        CashBuffer = options.CashBuffer;
        MinTradeValue = options.MinTradeValue;
        FeeMin = options.FeeMin;
        FeeRate = options.FeeRate;
    }
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
