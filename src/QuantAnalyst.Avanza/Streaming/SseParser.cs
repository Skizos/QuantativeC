using System.Text;

namespace QuantAnalyst.Avanza.Streaming;

/// <summary>One dispatched server-sent event. <see cref="Id"/> is the stream's last event id at dispatch time.</summary>
internal sealed record SseEvent(string Event, string Data, string Id, int? Retry);

/// <summary>The event stream broke the protocol (e.g. a line over the size limit). The connection is dropped and retried.</summary>
internal sealed class SseProtocolException(string message) : Exception(message);

/// <summary>
/// Incremental <c>text/event-stream</c> parser following the WHATWG HTML "event stream interpretation" rules
/// (https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation):
/// <list type="bullet">
/// <item>lines end in CRLF, LF or CR, also when split across chunks; one leading BOM is dropped</item>
/// <item><c>:</c> lines are comments; one space after the field colon is stripped</item>
/// <item><c>data</c> lines are joined with LF; an event with no data is not dispatched; an empty event name means <c>message</c></item>
/// <item><c>id</c> containing NUL is ignored; <c>retry</c> is accepted only as ASCII digits</item>
/// </list>
/// A line or an event's data over <see cref="MaxLineChars"/> is a protocol error (ADR 0002 §3: events up to 1 MB).
/// </summary>
internal sealed class SseParser(int maxLineChars = SseParser.DefaultMaxChars)
{
    public const int DefaultMaxChars = 1024 * 1024;

    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string _eventType = string.Empty;
    private int? _retry;
    private bool _afterCr;
    private bool _atStart = true;

    public int MaxLineChars { get; } = maxLineChars;

    /// <summary>The last event id seen on this stream; kept across reconnects and sent as <c>Last-Event-ID</c>.</summary>
    public string LastEventId { get; private set; } = string.Empty;

    /// <summary>The server's reconnection time in milliseconds, if it sent one.</summary>
    public int? RetryMilliseconds { get; private set; }

    /// <summary>Parses one chunk of decoded text and appends the events it completes to <paramref name="output"/>.</summary>
    public void Feed(ReadOnlySpan<char> chunk, List<SseEvent> output)
    {
        foreach (char c in chunk)
        {
            if (_atStart)
            {
                _atStart = false;
                if (c == '﻿')
                {
                    continue;
                }
            }

            if (_afterCr)
            {
                _afterCr = false;
                if (c == '\n')
                {
                    continue; // second half of CRLF
                }
            }

            switch (c)
            {
                case '\r':
                    _afterCr = true;
                    EndLine(output);
                    break;
                case '\n':
                    EndLine(output);
                    break;
                default:
                    if (_line.Length >= MaxLineChars)
                    {
                        throw new SseProtocolException($"An event-stream line exceeds {MaxLineChars} characters.");
                    }

                    _line.Append(c);
                    break;
            }
        }
    }

    /// <summary>
    /// The connection ended: a partial line or event is discarded (WHATWG: pending data at end of stream is not
    /// dispatched). The last event id and retry are kept for the reconnect.
    /// </summary>
    public void ResetConnection()
    {
        _line.Clear();
        _data.Clear();
        _eventType = string.Empty;
        _retry = null;
        _afterCr = false;
        _atStart = true;
    }

    private void EndLine(List<SseEvent> output)
    {
        if (_line.Length == 0)
        {
            Dispatch(output);
            return;
        }

        string line = _line.ToString();
        _line.Clear();
        if (line[0] == ':')
        {
            return; // comment
        }

        int colon = line.IndexOf(':', StringComparison.Ordinal);
        string field = colon < 0 ? line : line[..colon];
        string value = colon < 0 ? string.Empty : line[(colon + 1)..];
        if (value.Length > 0 && value[0] == ' ')
        {
            value = value[1..];
        }

        switch (field)
        {
            case "event":
                _eventType = value;
                break;
            case "data":
                if (_data.Length + value.Length + 1 > MaxLineChars)
                {
                    throw new SseProtocolException($"An event's data exceeds {MaxLineChars} characters.");
                }

                _data.Append(value).Append('\n');
                break;
            case "id":
                if (!value.Contains('\0', StringComparison.Ordinal))
                {
                    LastEventId = value;
                }

                break;
            case "retry":
                if (value.Length is > 0 and <= 9 && value.All(char.IsAsciiDigit))
                {
                    _retry = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    RetryMilliseconds = _retry;
                }

                break;
            default:
                break; // unknown fields are ignored by the spec
        }
    }

    private void Dispatch(List<SseEvent> output)
    {
        int? retry = _retry;
        _retry = null;
        if (_data.Length == 0)
        {
            _eventType = string.Empty;
            return;
        }

        _data.Length--; // trailing LF
        output.Add(new SseEvent(_eventType.Length == 0 ? "message" : _eventType, _data.ToString(), LastEventId, retry));
        _data.Clear();
        _eventType = string.Empty;
    }
}
