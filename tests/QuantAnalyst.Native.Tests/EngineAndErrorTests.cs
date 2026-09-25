using QuantAnalyst.Native.Interop;

namespace QuantAnalyst.Native.Tests;

public sealed class EngineAndErrorTests
{
    [Fact]
    public void Create_StoresSeed_AndDisposeIsIdempotent()
    {
        QeEngine engine = QeEngine.Create(seed: 20260925);

        Assert.Equal(20260925UL, engine.Seed);
        engine.Dispose();
        engine.Dispose();
    }

    [Fact]
    public void UseAfterDispose_ThrowsObjectDisposedException()
    {
        QeEngine engine = QeEngine.Create();
        engine.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            engine.PriceBlackScholes(new BlackScholesInput[1], new BlackScholesOutput[1]));
    }

    [Fact]
    public unsafe void NativeCreate_WithBadStructSize_ReturnsInvalidArgument_AndMessage()
    {
        var config = new QeEngineConfig(structSize: 8, flags: 0, seed: 0);

        QeStatus status = QeNative.EngineCreate(&config, out QeEngineHandle handle);
        QeException ex = QeErrors.CreateException(status, "qe_engine_create");

        Assert.Equal(QeStatus.InvalidArgument, status);
        Assert.True(handle.IsInvalid);
        Assert.Contains("struct_size", ex.NativeMessage, StringComparison.Ordinal);
        Assert.Contains("qe_engine_create failed with InvalidArgument", ex.Message, StringComparison.Ordinal);
        handle.Dispose();
    }

    [Fact]
    public unsafe void LastError_IsClearedBySuccessfulCall_AndThreadLocal()
    {
        var config = new QeEngineConfig(structSize: 8, flags: 0, seed: 0);
        _ = QeNative.EngineCreate(&config, out QeEngineHandle handle);
        handle.Dispose();
        Assert.NotEmpty(QeErrors.GetLastErrorMessage());

        string otherThread = "unset";
        var thread = new Thread(() => otherThread = QeErrors.GetLastErrorMessage());
        thread.Start();
        thread.Join();
        Assert.Equal(string.Empty, otherThread);

        int major;
        int minor;
        Assert.Equal(QeStatus.Ok, QeNative.AbiVersion(&major, &minor));
        Assert.Equal(string.Empty, QeErrors.GetLastErrorMessage());
    }

    [Fact]
    public void QeException_FormatsStatusOperationAndMessage()
    {
        var ex = new QeException(QeStatus.Numeric, "qe_bs_price_batch", "non-finite");

        Assert.Equal("qe_bs_price_batch failed with Numeric: non-finite", ex.Message);
        Assert.Equal(QeStatus.Numeric, ex.Status);
    }
}
