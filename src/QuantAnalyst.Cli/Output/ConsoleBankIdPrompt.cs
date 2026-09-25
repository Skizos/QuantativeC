using System.Collections;
using System.Text;
using QRCoder;
using QuantAnalyst.Avanza.Auth;

namespace QuantAnalyst.Cli.Output;

/// <summary>
/// Draws the BankID QR code in the terminal: two modules per character row, and dark on light via
/// <see cref="Console"/> colours so it scans on dark themes too. It is redrawn in place whenever Avanza hands out a
/// fresh token.
/// Redrawing uses <see cref="Console.SetCursorPosition"/> rather than raw escape codes, because the classic Windows
/// console doesn't interpret those (seen live 2026-09-25 as a stray "[K"). The payload itself is never printed or
/// logged.
/// </summary>
internal sealed class ConsoleBankIdPrompt(TextWriter writer, bool interactive) : IBankIdPrompt
{
    private const string Header = "Log in to Avanza: open the BankID app, choose 'Scan QR code' and scan this code.";
    private string _status = "Open the BankID app and scan the QR code.";
    private int? _top;
    private int _statusRow = -1;
    private int _statusWidth;

    public void ShowQrCode(string qrPayload)
    {
        IReadOnlyList<string> qr = Render(qrPayload);
        if (!interactive || !TryDrawInPlace(qr))
        {
            writer.WriteLine(Header);
            foreach (string line in qr)
            {
                writer.WriteLine(line);
            }

            writer.WriteLine(_status);
        }

        writer.Flush();
    }

    public void ShowStatus(string message)
    {
        _status = message;
        if (interactive && _statusRow >= 0 && TryRewriteStatus())
        {
            return;
        }

        writer.WriteLine(message);
        writer.Flush();
    }

    public void Completed()
    {
        writer.WriteLine("BankID approved. Finishing the login …");
        writer.Flush();
    }

    /// <summary>QR modules (incl. the quiet zone) as half-block text; dark modules are drawn, light ones are blank.</summary>
    internal static IReadOnlyList<string> Render(string payload)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        List<BitArray> m = data.ModuleMatrix;
        var lines = new List<string>((m.Count + 1) / 2);
        for (int y = 0; y < m.Count; y += 2)
        {
            var sb = new StringBuilder(m.Count);
            for (int x = 0; x < m.Count; x++)
            {
                bool top = m[y][x];
                bool bottom = y + 1 < m.Count && m[y + 1][x];
                sb.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }

            lines.Add(sb.ToString());
        }

        return lines;
    }

    private bool TryDrawInPlace(IReadOnlyList<string> qr)
    {
        try
        {
            _top ??= Console.CursorTop;
            Console.SetCursorPosition(0, _top.Value);
            writer.WriteLine(Header);
            foreach (string line in qr)
            {
                writer.Flush();
                Console.BackgroundColor = ConsoleColor.White;
                Console.ForegroundColor = ConsoleColor.Black;
                writer.Write(line);
                writer.Flush();
                Console.ResetColor();
                writer.WriteLine();
            }

            _statusRow = Console.CursorTop;
            WriteStatusLine();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            Console.ResetColor();
            return false;
        }
    }

    private bool TryRewriteStatus()
    {
        try
        {
            Console.SetCursorPosition(0, _statusRow);
            WriteStatusLine();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // Pads over a longer previous status instead of an erase-line escape code.
    private void WriteStatusLine()
    {
        writer.WriteLine(_status.PadRight(_statusWidth));
        _statusWidth = Math.Max(_statusWidth, _status.Length);
        writer.Flush();
    }
}
