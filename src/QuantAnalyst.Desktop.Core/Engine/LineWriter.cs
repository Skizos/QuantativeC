using System.Text;

namespace QuantAnalyst.Desktop.Core.Engine;

/// <summary>A writer that hands every completed line to a callback (a trailing partial line on <see cref="Complete"/>).</summary>
internal sealed class LineWriter(Action<string> onLine) : TextWriter
{
    private readonly StringBuilder _line = new();
    private readonly Lock _gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        string? done = null;
        lock (_gate)
        {
            if (value == '\n')
            {
                done = _line.ToString().TrimEnd('\r');
                _line.Clear();
            }
            else
            {
                _line.Append(value);
            }
        }

        if (done is not null)
        {
            onLine(done);
        }
    }

    public override void Write(string? value)
    {
        foreach (char c in value ?? string.Empty)
        {
            Write(c);
        }
    }

    /// <summary>Emits what is left after the last newline.</summary>
    public void Complete()
    {
        string rest;
        lock (_gate)
        {
            rest = _line.ToString().TrimEnd('\r');
            _line.Clear();
        }

        if (rest.Length > 0)
        {
            onLine(rest);
        }
    }
}
