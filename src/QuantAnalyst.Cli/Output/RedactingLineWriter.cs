using System.Text;
using QuantAnalyst.Avanza.Logging;

namespace QuantAnalyst.Cli.Output;

/// <summary>
/// Writes each completed line through the <see cref="Redactor"/> to the real output as soon as it is complete, for
/// long-running verbs like <c>qa stream</c>. Thread-safe: lines from concurrent printers never interleave.
/// </summary>
internal sealed class RedactingLineWriter(TextWriter inner, Redactor redactor) : TextWriter
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                EmitLine();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            foreach (char c in value ?? string.Empty)
            {
                Write(c);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            Write(value);
            EmitLine();
        }
    }

    public override void WriteLine() => Write('\n');

    public override void Flush()
    {
        lock (_gate)
        {
            if (_line.Length > 0)
            {
                inner.Write(redactor.Redact(_line.ToString()));
                _line.Clear();
            }

            inner.Flush();
        }
    }

    private void EmitLine()
    {
        inner.WriteLine(redactor.Redact(_line.ToString()));
        inner.Flush();
        _line.Clear();
    }
}
