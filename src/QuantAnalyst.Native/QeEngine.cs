using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native;

/// <summary>
/// A native QuantEngine instance. Thread-safe for concurrent pricing calls; dispose once.
/// </summary>
public sealed class QeEngine : IDisposable
{
    private readonly QeEngineHandle handle;

    private QeEngine(QeEngineHandle handle, ulong seed)
    {
        this.handle = handle;
        Seed = seed;
    }

    /// <summary>Gets the base seed for all randomness created by this engine.</summary>
    public ulong Seed { get; }

    /// <summary>Creates an engine after verifying the native ABI version.</summary>
    /// <param name="seed">Base seed; stored with every seeded result.</param>
    public static unsafe QeEngine Create(ulong seed = 0)
    {
        QeAbi.EnsureCompatible();
        var config = new QeEngineConfig(seed);
        QeStatus status = QeNative.EngineCreate(&config, out QeEngineHandle created);
        if (status != QeStatus.Ok)
        {
            QeException error = QeErrors.CreateException(status, "qe_engine_create");
            created.Dispose();
            throw error;
        }

        return new QeEngine(created, seed);
    }

    /// <summary>
    /// Prices a batch of European options. Invalid elements do not fail the call: they come back
    /// with a non-OK <see cref="BlackScholesOutput.Status"/> and a NaN price.
    /// </summary>
    /// <returns>The number of elements whose status is not <see cref="QeStatus.Ok"/>.</returns>
    public unsafe long PriceBlackScholes(ReadOnlySpan<BlackScholesInput> inputs, Span<BlackScholesOutput> outputs)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (inputs.Length != outputs.Length)
        {
            throw new ArgumentException(
                $"outputs.Length ({outputs.Length}) must equal inputs.Length ({inputs.Length}).", nameof(outputs));
        }

        long failed;
        fixed (BlackScholesInput* pInputs = inputs)
        fixed (BlackScholesOutput* pOutputs = outputs)
        {
            QeStatus status = QeNative.BsPriceBatch(handle, pInputs, pOutputs, inputs.Length, &failed);
            QeErrors.ThrowIfFailed(status, "qe_bs_price_batch");
        }

        return failed;
    }

    /// <inheritdoc/>
    public void Dispose() => handle.Dispose();
}
