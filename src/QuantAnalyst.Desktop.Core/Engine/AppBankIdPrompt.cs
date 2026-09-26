using QRCoder;
using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Desktop.Core.Mvvm;

namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>
/// Shows the BankID login in the window: the QR code as a PNG image (redrawn whenever Avanza rotates the token), the
/// status text, and whether a login is waiting for you. The payload itself is never logged or kept beyond the image.
/// </summary>
public sealed class AppBankIdPrompt(IUiDispatcher ui) : ObservableObject, IBankIdPrompt
{
    private byte[]? _qrPng;
    private string _status = string.Empty;
    private bool _isWaiting;

    /// <summary>Gets the current QR code as PNG bytes, or null when no login is waiting.</summary>
    public byte[]? QrPng
    {
        get => _qrPng;
        private set => Set(ref _qrPng, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Gets a value indicating whether a BankID login is waiting for the phone (the overlay is shown).</summary>
    public bool IsWaiting
    {
        get => _isWaiting;
        private set => Set(ref _isWaiting, value);
    }

    public void ShowQrCode(string qrPayload)
    {
        byte[] png = RenderPng(qrPayload);
        ui.Post(() =>
        {
            QrPng = png;
            IsWaiting = true;
            if (Status.Length == 0)
            {
                Status = "Open the BankID app, choose 'Scan QR code' and scan this code.";
            }
        });
    }

    public void ShowStatus(string message) => ui.Post(() => Status = message);

    public void Completed() => ui.Post(Reset);

    /// <summary>Hides the overlay (login done, failed or cancelled).</summary>
    public void Reset()
    {
        QrPng = null;
        IsWaiting = false;
        Status = string.Empty;
    }

    /// <summary>The QR code as a PNG: 10 pixels per module, black on white, with the standard quiet zone.</summary>
    internal static byte[] RenderPng(string payload)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        return new PngByteQRCode(data).GetGraphic(10);
    }
}
