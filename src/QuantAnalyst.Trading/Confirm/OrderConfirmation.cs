namespace QuantAnalyst.Trading.Confirm;

/// <summary>
/// The Confirm mode gate (ADR 0003 §2 <c>ModeGate</c>, §5): shows an order card and returns the typed answer. Only
/// <see cref="OrderGateway"/> asks it, one card at a time, and sends nothing unless the answer is
/// <see cref="ConfirmationVerdict.Confirmed"/> and the re-check after it passes.
/// </summary>
public interface IOrderConfirmation
{
    /// <summary>Shows <paramref name="card"/> and waits for the answer. Cancelled when trading halts or the session stops.</summary>
    Task<ConfirmationAnswer> ConfirmAsync(OrderCard card, CancellationToken ct);

    /// <summary>Tells the person what happened next (the re-check, the send, the broker's answer).</summary>
    void Tell(string line);
}

/// <summary>The terminal's order cards: the card, a <c>&gt;</c> prompt, then the verdict.</summary>
public sealed class ConsoleOrderConfirmation(TextWriter output, ConfirmationPrompt prompt, TimeProvider time) : IOrderConfirmation
{
    private int _cards;

    public async Task<ConfirmationAnswer> ConfirmAsync(OrderCard card, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);
        int number = Interlocked.Increment(ref _cards);
        foreach (string line in card.Render(number))
        {
            output.WriteLine(line);
        }

        output.Write(" > ");
        output.Flush();
        ConfirmationAnswer answer = await prompt.AskAsync(card.Ticker, time.GetUtcNow(), ct).ConfigureAwait(false);
        if (answer.After is null)
        {
            output.WriteLine(); // no line was typed, so the prompt is still open
        }

        output.WriteLine(answer.Confirmed ? " Confirmed. Re-checking before sending …" : $" Skipped: {answer.Reason}. Nothing was sent.");
        return answer;
    }

    public void Tell(string line) => output.WriteLine(" " + line);
}
