using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native.Tests;

/// <summary>Managed struct layouts must match what the native library reports (ADR 0001).</summary>
public sealed class LayoutTests
{
    public static TheoryData<int, string, string[]> Structs => new()
    {
        {
            (int)QeStructId.EngineConfig,
            typeof(QeEngineConfig).AssemblyQualifiedName!,
            ["StructSize", "Flags", "Seed"]
        },
        {
            (int)QeStructId.BsInput,
            typeof(BlackScholesInput).AssemblyQualifiedName!,
            ["spot", "strike", "rate", "dividendYield", "volatility", "expiryYears", "optionType", "reserved"]
        },
        {
            (int)QeStructId.BsOutput,
            typeof(BlackScholesOutput).AssemblyQualifiedName!,
            ["price", "status", "reserved"]
        },
        {
            (int)QeStructId.LayoutInfo,
            typeof(QeStructLayoutInfo).AssemblyQualifiedName!,
            ["Size", "Alignment", "FieldCount", "Reserved", "Offsets"]
        },
        {
            (int)QeStructId.BsGreeks,
            typeof(BlackScholesGreeks).AssemblyQualifiedName!,
            ["price", "delta", "gamma", "vega", "theta", "rho", "status", "reserved"]
        },
        {
            (int)QeStructId.IvInput,
            typeof(ImpliedVolInput).AssemblyQualifiedName!,
            ["spot", "strike", "rate", "dividendYield", "expiryYears", "price", "optionType", "reserved"]
        },
        { (int)QeStructId.IvOutput, typeof(ImpliedVolOutput).AssemblyQualifiedName!, ["volatility", "status", "iterations"] },
        { (int)QeStructId.LatticeInput, typeof(LatticeInput).AssemblyQualifiedName!, ["option", "steps", "exercise"] },
        {
            (int)QeStructId.McConfig,
            typeof(QeMcConfig).AssemblyQualifiedName!,
            ["StructSize", "Flags", "Paths", "Seed", "Replications", "Steps"]
        },
        { (int)QeStructId.McResult, typeof(MonteCarloResult).AssemblyQualifiedName!, ["price", "stdError", "paths", "seed"] },
        { (int)QeStructId.CovConfig, typeof(QeCovConfig).AssemblyQualifiedName!, ["StructSize", "Method", "EwmaLambda"] },
        {
            (int)QeStructId.VarEs,
            typeof(VarEsResult).AssemblyQualifiedName!,
            ["valueAtRisk", "expectedShortfall", "observations", "seed"]
        },
        {
            (int)QeStructId.OptConfig,
            typeof(QeOptConfig).AssemblyQualifiedName!,
            ["StructSize", "Method", "RiskAversion", "Tolerance", "MaxIterations", "Reserved"]
        },
        { (int)QeStructId.OptResult, typeof(QeOptResult).AssemblyQualifiedName!, ["Objective", "Iterations", "Converged"] },
        {
            (int)QeStructId.RebalanceAsset,
            typeof(RebalanceAsset).AssemblyQualifiedName!,
            ["price", "targetWeight", "currentQuantity", "lotSize"]
        },
        {
            (int)QeStructId.RebalanceConfig,
            typeof(QeRebalanceConfig).AssemblyQualifiedName!,
            ["StructSize", "Reserved", "Cash", "CashBuffer", "MinTradeValue", "FeeMin", "FeeRate"]
        },
        {
            (int)QeStructId.RebalanceTrade,
            typeof(RebalanceTrade).AssemblyQualifiedName!,
            ["targetQuantity", "tradeQuantity", "tradeValue", "fee", "finalWeight"]
        },
        {
            (int)QeStructId.RebalanceSummary,
            typeof(RebalanceSummary).AssemblyQualifiedName!,
            ["portfolioValue", "cashAfter", "totalFees", "trackingError", "trades", "feasible"]
        },
    };

    [Theory]
    [MemberData(nameof(Structs))]
    public unsafe void ManagedLayout_MatchesNative(int structId, string typeName, string[] fields)
    {
        Type type = Type.GetType(typeName, throwOnError: true)!;
        QeStructLayoutInfo native = QeAbi.GetLayout((QeStructId)structId);

        Assert.Equal(native.Size, Marshal.SizeOf(type));
        Assert.Equal(native.FieldCount, fields.Length);
        for (int i = 0; i < fields.Length; i++)
        {
            Assert.Equal(native.Offsets[i], (int)Marshal.OffsetOf(type, fields[i]));
        }

        for (int i = fields.Length; i < QeStructLayoutInfo.MaxFields; i++)
        {
            Assert.Equal(-1, native.Offsets[i]);
        }
    }

    [Fact]
    public void BlittableSizes_AreThePinnedAbiSizes()
    {
        Assert.Equal(16, Unsafe.SizeOf<QeEngineConfig>());
        Assert.Equal(56, Unsafe.SizeOf<BlackScholesInput>());
        Assert.Equal(16, Unsafe.SizeOf<BlackScholesOutput>());
        Assert.Equal(80, Unsafe.SizeOf<QeStructLayoutInfo>());
        Assert.Equal(QeEngineConfig.QeEngineConfigSize, Unsafe.SizeOf<QeEngineConfig>());
    }

    [Fact]
    public void UnknownStructId_ThrowsWithNativeMessage()
    {
        QeException ex = Assert.Throws<QeException>(() => QeAbi.GetLayout((QeStructId)999));

        Assert.Equal(QeStatus.InvalidArgument, ex.Status);
        Assert.Equal("qe_struct_layout", ex.Operation);
        Assert.Contains("unknown struct_id", ex.NativeMessage, StringComparison.Ordinal);
    }
}
