using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QuantAnalyst.Native.Interop;

/// <summary>
/// Source-generated P/Invokes for native/include/qe_api.h. The only place that declares native
/// entry points (ADR 0001). Callers go through <see cref="QeEngine"/> / <see cref="QeAbi"/>.
/// </summary>
internal static unsafe partial class QeNative
{
    internal const string LibraryName = "qe";

#pragma warning disable CA1810 // The static constructor exists to install the resolver before the first P/Invoke.
    static QeNative() => QeNativeLibraryResolver.Register();
#pragma warning restore CA1810

    [LibraryImport(LibraryName, EntryPoint = "qe_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus AbiVersion(int* major, int* minor);

    [LibraryImport(LibraryName, EntryPoint = "qe_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus LastError(byte* buffer, int capacity, int* required);

    [LibraryImport(LibraryName, EntryPoint = "qe_struct_layout")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus StructLayout(QeStructId structId, QeStructLayoutInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "qe_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus EngineCreate(QeEngineConfig* config, out QeEngineHandle engine);

    [LibraryImport(LibraryName, EntryPoint = "qe_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus EngineDestroy(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "qe_bs_price_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BsPriceBatch(
        QeEngineHandle engine,
        BlackScholesInput* inputs,
        BlackScholesOutput* outputs,
        long count,
        long* failedCount);

    [LibraryImport(LibraryName, EntryPoint = "qe_bs_greeks_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BsGreeksBatch(QeEngineHandle engine, BlackScholesInput* inputs, BlackScholesGreeks* outputs, long count, long* failedCount);

    [LibraryImport(LibraryName, EntryPoint = "qe_implied_vol_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus ImpliedVolBatch(QeEngineHandle engine, ImpliedVolInput* inputs, ImpliedVolOutput* outputs, long count, long* failedCount);

    [LibraryImport(LibraryName, EntryPoint = "qe_lattice_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus LatticeBatch(QeEngineHandle engine, LatticeInput* inputs, BlackScholesOutput* outputs, long count, long* failedCount);

    [LibraryImport(LibraryName, EntryPoint = "qe_mc_european")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus McEuropean(QeEngineHandle engine, QeMcConfig* config, BlackScholesInput* option, MonteCarloResult* result);

    [LibraryImport(LibraryName, EntryPoint = "qe_covariance")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus Covariance(QeEngineHandle engine, QeCovConfig* config, double* returns, long observations, long assets, double* outCov, double* outShrinkage);

    [LibraryImport(LibraryName, EntryPoint = "qe_var_es_historical")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus VarEsHistorical(double* returns, long count, double confidence, VarEsResult* result);

    [LibraryImport(LibraryName, EntryPoint = "qe_var_es_parametric")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus VarEsParametric(double* weights, double* mean, double* cov, long assets, double confidence, VarEsResult* result);

    [LibraryImport(LibraryName, EntryPoint = "qe_var_es_monte_carlo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus VarEsMonteCarlo(QeEngineHandle engine, double* weights, double* mean, double* cov, long assets, long paths, ulong seed, double confidence, VarEsResult* result);

    [LibraryImport(LibraryName, EntryPoint = "qe_betas")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus Betas(double* returns, double* index, long observations, long assets, double* outBetas);

    [LibraryImport(LibraryName, EntryPoint = "qe_stress_pnl")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus StressPnl(double* values, double* shocks, long assets, long scenarios, double* outPnl);

    [LibraryImport(LibraryName, EntryPoint = "qe_optimize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus Optimize(QeEngineHandle engine, QeOptConfig* config, double* mu, double* cov, double* lower, double* upper, long assets, double* outWeights, QeOptResult* outResult);

    [LibraryImport(LibraryName, EntryPoint = "qe_rebalance")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus Rebalance(QeRebalanceConfig* config, RebalanceAsset* assets, long count, RebalanceTrade* outTrades, RebalanceSummary* outSummary);

    // ---- ABI 1.2: backtest

    [LibraryImport(LibraryName, EntryPoint = "qe_bt_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BtCreate(QeBtConfig* config, BacktestInstrument* instruments, long count, out QeBacktestHandle backtest);

    [LibraryImport(LibraryName, EntryPoint = "qe_bt_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BtDestroy(nint backtest);

    [LibraryImport(LibraryName, EntryPoint = "qe_bt_step")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BtStep(QeBacktestHandle backtest, BacktestBar* bars, long barCount, BacktestOrder* orders, long orderCount, BacktestFill* fills, long fillCapacity, long* fillCount, BacktestState* outState);

    [LibraryImport(LibraryName, EntryPoint = "qe_bt_positions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BtPositions(QeBacktestHandle backtest, long* positions, long count);

    // ---- ABI 1.3: per-instrument courtage (ADR 0005)

    [LibraryImport(LibraryName, EntryPoint = "qe_bt_set_courtage")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial QeStatus BtSetCourtage(QeBacktestHandle backtest, long instrument, double courtageMin, double courtageRate);
}
