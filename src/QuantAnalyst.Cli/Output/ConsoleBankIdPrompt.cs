using System.Collections;
using System.Text;
using QRCoder;
using QuantAnalyst.Avanza.Auth;

namespace QuantAnalyst.Cli.Output;

/// <summary>
/// Draws the BankID QR code in the terminal (two modules per character row, dark on light so it scans on dark
/// themes too) and redraws it in place whenever Avanza hands out a fresh token. The payload itself is never
/// printed or logged.
/// </summary>
internal sealed class ConsoleBankIdPrompt(TextWriter writer, bool interactive) : IBankIdPrompt
{
    private const string DarkOnLight = "\u001b[30;107m";
    private const string Reset = "\u001b[0m";
    private const string ClearToEnd = "\u001b[K";
    private string _status = "Open the BankID app and scan the QR code.";
    private int _linesDrawn;

    public int Refreshes { get; private set; }

    public void ShowQrCode(string qrPayload)
    {
        Refreshes++;
        var lines = new List<string> { "Log in to Avanza: open the BankID app, choose 'Scan QR code' and scan this code." };
        lines.AddRange(Render(qrPayload, interactive));
        lines.Add(_status);
        Draw(lines);
    }

    public void ShowStatus(string message)
    {
        _status = message;
        if (!interactive)
        {
            writer.WriteLine(message);
            return;
        }

        // Rewrite only the status line (the last line drawn).
        writer.Write($"\u001b[1F{ClearToEnd}{message}\n");
        writer.Flush();
    }

    public void Completed()
    {
        writer.WriteLine("BankID approved. Finishing the login …");
        writer.Flush();
    }

    /// <summary>QR modules (incl. the quiet zone) as half-block text; dark modules are drawn, light ones are blank.</summary>
    internal static IReadOnlyList<string> Render(string payload, bool ansiColours)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.L);
        List<BitArray> m = data.ModuleMatrix;
        var lines = new List<string>((m.Count + 1) / 2);
        for (int y = 0; y < m.Count; y += 2)
        {
            var sb = new StringBuilder(m.Count + 16);
            if (ansiColours)
            {
                sb.Append(DarkOnLight);
            }

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

            if (ansiColours)
            {
                sb.Append(Reset);
            }

            lines.Add(sb.ToString());
        }

        return lines;
    }

    private void Draw(List<string> lines)
    {
        if (interactive && _linesDrawn > 0)
        {
            writer.Write($"\u001b[{_linesDrawn}F"); // back to the first line of the previous drawing
        }

        foreach (string line in lines)
        {
            writer.Write(line);
            writer.Write(interactive ? ClearToEnd + "\n" : "\n");
        }

        _linesDrawn = lines.Count;
        writer.Flush();
    }
}
