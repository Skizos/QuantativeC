using System.Threading.Channels;

namespace QuantAnalyst.Trading.Confirm;

/// <summary>What the typed answer to an order card means. Only <see cref="Confirmed"/> can send an order.</summary>
public enum ConfirmationVerdict
{
    /// <summary>The ticker plus JA, typed while the card was valid.</summary>
    Confirmed,

    /// <summary>Anything else: Enter, JA alone, y, another ticker.</summary>
    Declined,

    /// <summary>An answer within the first second: typed ahead (or meant for the previous card), not read.</summary>
    TooFast,

    /// <summary>No answer within 30 s, or an answer after 30 s.</summary>
    Expired,

    /// <summary>The input closed (end of file): nobody can answer.</summary>
    NoInput,

    /// <summary>Trading halted (e.g. the kill switch) while the card was shown.</summary>
    Halted,
}

/// <summary>The outcome of one order card. <see cref="Reason"/> never repeats what was typed.</summary>
/// <param name="After">How long after the card appeared the answer came, when there was one.</param>
public sealed record ConfirmationAnswer(ConfirmationVerdict Verdict, string Reason, TimeSpan? After)
{
    public bool Confirmed => Verdict == ConfirmationVerdict.Confirmed;
}

/// <summary>
/// Reads the typed confirmation for an order card (ADR 0003 §5): exactly the ticker plus <c>JA</c> (case, hyphen and
/// spaces normalised: <c>ERIC-B JA</c>, <c>eric b ja</c>), at least <see cref="MinimumReadingTime"/> and at most
/// <see cref="Expiry"/> after the card appeared, both measured with the injected clock. Anything else skips the order.
/// Lines typed before the card appeared are discarded, never taken as its answer. One background reader owns the input,
/// so an unanswered card can time out while the terminal is still waiting for a line.
/// </summary>
public sealed class ConfirmationPrompt
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(30);

    /// <summary>No one reads a card in under a second: faster answers were typed ahead, or meant for the card before.</summary>
    public static readonly TimeSpan MinimumReadingTime = TimeSpan.FromSeconds(1);

    private readonly TextReader _input;
    private readonly TimeProvider _time;
    private readonly Channel<TimedLine> _lines = Channel.CreateUnbounded<TimedLine>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private bool _closed;
    private int _linesReceived;

    /// <summary>Starts reading <paramref name="input"/> on a background thread; create it only for a Confirm session.</summary>
    public ConfirmationPrompt(TextReader input, TimeProvider time)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _time = time ?? throw new ArgumentNullException(nameof(time));

        // A dedicated background thread: a console read can't be cancelled, so it must not hold up the caller.
        _ = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>Gets how many lines (and the end of input) the reader has taken; tests wait on it.</summary>
    internal int LinesReceived => Volatile.Read(ref _linesReceived);

    /// <summary>What to type for <paramref name="ticker"/>: <c>ERIC B</c> gives <c>ERIC-B JA</c>.</summary>
    public static string ExpectedAnswer(string ticker) => $"{Normalize(ticker).Replace(' ', '-')} JA";

    /// <summary>Waits for the answer to the card for <paramref name="ticker"/> that appeared at <paramref name="shownUtc"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the order is not sent.</exception>
    public async Task<ConfirmationAnswer> AskAsync(string ticker, DateTimeOffset shownUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        DateTimeOffset deadline = shownUtc + Expiry;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_lines.Reader.TryRead(out TimedLine? line))
            {
                if (line.Text is null)
                {
                    _closed = true;
                    return new ConfirmationAnswer(ConfirmationVerdict.NoInput, "the input is closed, so nobody can confirm", null);
                }

                if (line.AtUtc < shownUtc)
                {
                    continue; // typed before this card appeared: not an answer to it
                }

                return Judge(line.Text, ticker, line.AtUtc - shownUtc);
            }

            if (_closed)
            {
                return new ConfirmationAnswer(ConfirmationVerdict.NoInput, "the input is closed, so nobody can confirm", null);
            }

            TimeSpan remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return Expired();
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<bool> arrived = _lines.Reader.WaitToReadAsync(stop.Token).AsTask();
            Task timeout = Task.Delay(remaining, _time, stop.Token);
            Task first = await Task.WhenAny(arrived, timeout).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (first == arrived && arrived.IsCompletedSuccessfully && !arrived.Result)
            {
                _closed = true; // completed and empty
            }
            else if (first == timeout && !_lines.Reader.TryPeek(out _))
            {
                return Expired();
            }
        }
    }

    /// <summary>The verdict on one typed line, <paramref name="after"/> the card appeared. Never echoes the text.</summary>
    public static ConfirmationAnswer Judge(string typed, string ticker, TimeSpan after)
    {
        ArgumentNullException.ThrowIfNull(typed);
        if (after < MinimumReadingTime)
        {
            return new ConfirmationAnswer(ConfirmationVerdict.TooFast, "answered within 1 s of the card appearing (typed ahead, or meant for the card before)", after);
        }

        if (after > Expiry)
        {
            return new ConfirmationAnswer(ConfirmationVerdict.Expired, "answered after 30 s", after);
        }

        string[] words = Normalize(typed).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[^1] == "JA" && string.Join(' ', words[..^1]) == Normalize(ticker))
        {
            return new ConfirmationAnswer(ConfirmationVerdict.Confirmed, "confirmed", after);
        }

        string why = words.Length == 0 ? "nothing was typed"
            : words is ["JA"] ? "JA without the ticker"
            : words[^1] == "JA" ? "another ticker"
            : "not a confirmation";
        return new ConfirmationAnswer(ConfirmationVerdict.Declined, why, after);
    }

    private static string Normalize(string text) =>
        string.Join(' ', text.Trim().ToUpperInvariant().Replace('-', ' ').Replace('_', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ConfirmationAnswer Expired() => new(ConfirmationVerdict.Expired, "no answer within 30 s", null);

    private void ReadLoop()
    {
        while (true)
        {
            string? text;
            try
            {
                text = _input.ReadLine();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                text = null;
            }

            _lines.Writer.TryWrite(new TimedLine(text, _time.GetUtcNow()));
            Interlocked.Increment(ref _linesReceived);
            if (text is null)
            {
                _lines.Writer.TryComplete();
                return;
            }
        }
    }

    private sealed record TimedLine(string? Text, DateTimeOffset AtUtc);
}
