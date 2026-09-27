using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Confirm;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Pipeline;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Lines the test types, one at a time: <see cref="ReadLine"/> blocks until the test types one (null ends the input).</summary>
internal sealed class TypedInput : TextReader
{
    private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

    public void Type(string? line) => _lines.Writer.TryWrite(line);

    public void EndInput() => _lines.Writer.TryComplete();

    public override string? ReadLine() => _lines.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>
/// Phase 7 step 3: the typed confirmation (ADR 0003 §5). Only the ticker plus JA, typed between 1 s and 30 s after the
/// card appeared, confirms; everything else skips. Times come from the injected clock.
/// </summary>
public sealed class ConfirmationPromptTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly TypedInput _input = new();
    private readonly ConfirmationPrompt _prompt;

    public ConfirmationPromptTests() => _prompt = new ConfirmationPrompt(_input, _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _input.EndInput();

    [Theory]
    [InlineData("ERIC-B JA")]
    [InlineData("ERIC B JA")]
    [InlineData("eric-b ja")]
    [InlineData("  Eric_B    Ja  ")]
    public void TheTickerPlusJA_Confirms_WhateverTheCaseHyphenOrSpacing(string typed)
    {
        ConfirmationAnswer a = ConfirmationPrompt.Judge(typed, "ERIC B", TimeSpan.FromSeconds(5));
        Assert.Equal(ConfirmationVerdict.Confirmed, a.Verdict);
        Assert.True(a.Confirmed);
    }

    [Theory]
    [InlineData("", "nothing was typed")]
    [InlineData("   ", "nothing was typed")]
    [InlineData("JA", "JA without the ticker")]
    [InlineData("ja", "JA without the ticker")]
    [InlineData("y", "not a confirmation")]
    [InlineData("yes", "not a confirmation")]
    [InlineData("ERIC-B", "not a confirmation")]
    [InlineData("ERIC-B JAA", "not a confirmation")]
    [InlineData("VOLV-B JA", "another ticker")]
    [InlineData("ERIC JA", "another ticker")]
    [InlineData("ERIC-B JA JA", "another ticker")]
    public void AnythingElse_Declines_WithoutEchoingIt(string typed, string reason)
    {
        ConfirmationAnswer a = ConfirmationPrompt.Judge(typed, "ERIC B", TimeSpan.FromSeconds(5));
        Assert.Equal(ConfirmationVerdict.Declined, a.Verdict);
        Assert.Equal(reason, a.Reason);
    }

    [Theory]
    [InlineData(0.0, ConfirmationVerdict.TooFast)]
    [InlineData(0.99, ConfirmationVerdict.TooFast)]
    [InlineData(1.0, ConfirmationVerdict.Confirmed)]
    [InlineData(30.0, ConfirmationVerdict.Confirmed)]
    [InlineData(30.1, ConfirmationVerdict.Expired)]
    public void TheAnswer_MustComeBetween1sAnd30sAfterTheCard(double seconds, ConfirmationVerdict expected) =>
        Assert.Equal(expected, ConfirmationPrompt.Judge("ERIC-B JA", "ERIC B", TimeSpan.FromSeconds(seconds)).Verdict);

    [Theory]
    [InlineData("ERIC B", "ERIC-B JA")]
    [InlineData("eric-b", "ERIC-B JA")]
    [InlineData("SAAB B", "SAAB-B JA")]
    [InlineData("INVE_B", "INVE-B JA")]
    public void TheCard_SaysExactlyWhatToType(string ticker, string expected) => Assert.Equal(expected, ConfirmationPrompt.ExpectedAnswer(ticker));

    [Fact]
    public async Task ACorrectAnswer_WhileTheCardIsValid_Confirms()
    {
        Task<ConfirmationAnswer> ask = _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct);
        _time.Advance(TimeSpan.FromSeconds(5));
        Type("eric-b ja");

        ConfirmationAnswer a = await ask.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ConfirmationVerdict.Confirmed, a.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(5), a.After);
    }

    [Fact]
    public async Task NoAnswer_ExpiresAfter30s()
    {
        Task<ConfirmationAnswer> ask = _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct);
        _time.Advance(TimeSpan.FromSeconds(29.9));
        await Task.Delay(50, Ct);
        Assert.False(ask.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(0.1));
        ConfirmationAnswer a = await ask.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((ConfirmationVerdict.Expired, "no answer within 30 s"), (a.Verdict, a.Reason));
        Assert.Null(a.After);
    }

    [Fact]
    public async Task ALineTypedBeforeTheCardAppeared_IsNotItsAnswer()
    {
        Type("ERIC-B JA"); // typed ahead, while no card was shown
        _time.Advance(TimeSpan.FromSeconds(2));
        Task<ConfirmationAnswer> ask = _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct);
        await Task.Delay(50, Ct);
        Assert.False(ask.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(3));
        Type("ERIC-B JA");
        ConfirmationAnswer a = await ask.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal((ConfirmationVerdict.Confirmed, TimeSpan.FromSeconds(3)), (a.Verdict, a.After));
    }

    [Fact]
    public async Task ALateAnswerMeantForThePreviousCard_SkipsTheNextOne_RatherThanConfirmIt()
    {
        Task<ConfirmationAnswer> first = _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(ConfirmationVerdict.Expired, (await first.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Verdict);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        Task<ConfirmationAnswer> second = _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct);
        _time.Advance(TimeSpan.FromSeconds(0.3));
        Type("ERIC-B JA"); // 30.5 s after the first card, 0.3 s after the second

        ConfirmationAnswer a = await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ConfirmationVerdict.TooFast, a.Verdict);
        Assert.False(a.Confirmed);
    }

    [Fact]
    public async Task TheEndOfInput_SkipsThisCardAndEveryLaterOne()
    {
        Type(null);
        ConfirmationAnswer a = await _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ConfirmationVerdict.NoInput, a.Verdict);

        a = await _prompt.AskAsync("ERIC B", _time.GetUtcNow(), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ConfirmationVerdict.NoInput, a.Verdict);
    }

    [Fact]
    public async Task StoppingTheSession_EndsTheWait()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<ConfirmationAnswer> ask = _prompt.AskAsync("ERIC B", _time.GetUtcNow(), stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task TheConsoleCard_ShowsTheCard_ThenTheVerdict()
    {
        var output = new StringWriter();
        var console = new ConsoleOrderConfirmation(output, _prompt, _time);
        Task<ConfirmationAnswer> ask = console.ConfirmAsync(OrderCardTests.Card(), Ct);
        _time.Advance(TimeSpan.FromSeconds(4));
        Type("VOLV-B JA");

        ConfirmationAnswer a = await ask.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ConfirmationVerdict.Declined, a.Verdict);
        string text = output.ToString();
        Assert.Contains(" ORDER 1 · CONFIRM MODE", text, StringComparison.Ordinal);
        Assert.Contains(" Type  ERIC-B JA  within 30 s to send.", text, StringComparison.Ordinal);
        Assert.Contains(" Skipped: another ticker. Nothing was sent.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VOLV", text, StringComparison.Ordinal);
    }

    // Types a line at the current fake time and waits until the prompt's reader has taken (and stamped) it.
    private void Type(string? line)
    {
        int before = _prompt.LinesReceived;
        _input.Type(line);
        Assert.True(SpinWait.SpinUntil(() => _prompt.LinesReceived > before, TimeSpan.FromSeconds(10)), "the prompt never read the line");
    }
}

/// <summary>Phase 7 step 3: every field of the order card (ADR 0003 §5).</summary>
public sealed class OrderCardTests
{
    internal static readonly InstrumentSpec Spec =
        OrderPreparationTests.Spec() with { TickTableVerified = true, Isin = "SE0000108656", MarketPlace = "XSTO" };

    internal static OrderCard Card(FeeComparison? fees = null, bool simulated = false)
    {
        var intent = new OrderIntent(RiskEngineTests.Eric, "ERIC B", OrderSide.Buy, 10, 100.37m, "ma-cross(fast=20, slow=100): target 20 %", 100.62m, RiskEngineTests.Now, "test");
        PreparedOrder order = OrderPreparation.Prepare(intent, Spec);
        RiskContext ctx = RiskEngineTests.Baseline(TradingMode.Confirm) with
        {
            Verified = new VerifiedConstants(true, true, true),
            Preflight = new BrokerPreflight(true, []),
        };
        RiskReport risk = new PreTradeRiskEngine(RiskLimits.AdrDefaults).Evaluate(order, ctx);
        return OrderCard.Create(order, Spec, ctx, risk, fees ?? new FeeComparison(39m, 39m, null), "avanza-order", simulated);
    }

    [Fact]
    public void TheCard_ShowsEveryField_AndEveryCheck()
    {
        IReadOnlyList<string> lines = Card().Render(1);
        string text = string.Join('\n', lines);

        Assert.Equal(" ORDER 1 · CONFIRM MODE · a real order on your Avanza account", lines[1]);
        Assert.Contains(" Account     ***678", lines);
        Assert.Contains(" Instrument  Ericsson B   ERIC B · orderbook 5240 · SE0000108656", lines);
        Assert.Contains(" Side        BUY", lines);
        Assert.Contains(" Volume      10", lines);
        Assert.Contains(" Limit       100.30 SEK    (rounded down from 100.37 to the 0.10 tick)", lines);
        Assert.Contains(" Value       1,003.00 SEK", lines);
        Assert.Contains(" Fee         Avanza 39.00 SEK · model 39.00 SEK", lines);
        Assert.Contains(" Reason      ma-cross(fast=20, slow=100): target 20 %", lines);
        Assert.Contains(" Decided     10:00:00 at 100.62", lines); // Stockholm time
        Assert.Contains(" Market      bid 100.40 × 500 · ask 100.60 × 700 · last 100.50 · 1 s old", lines);
        Assert.Contains(" Risk checks: 21 of 21 pass", lines);
        Assert.Equal(21, lines.Count(l => l.StartsWith("   R", StringComparison.Ordinal) && l.EndsWith("ok", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.StartsWith("   R21 broker preflight", StringComparison.Ordinal) && l.Contains("all valid  (limit all valid)", StringComparison.Ordinal));
        Assert.Contains(" Type  ERIC-B JA  within 30 s to send. Anything else skips this order.", lines);
        Assert.DoesNotContain("12345678", text, StringComparison.Ordinal); // the account is masked
    }

    [Fact]
    public void AFeeDifference_IsFlagged_AndARehearsalSaysNothingIsSent()
    {
        var fees = new FeeComparison(0m, 39m, "Avanza's fee 39.00 SEK differs from the model's 0.00 by +39.00: check the courtage class.");
        IReadOnlyList<string> lines = Card(fees, simulated: true).Render();

        Assert.Equal(" CONFIRM MODE · rehearsal on the avanza-order channel: nothing is sent to Avanza", lines[1]);
        Assert.Contains(" Fee         Avanza 39.00 SEK · model 0.00 SEK", lines);
        Assert.Contains("             ! Avanza's fee 39.00 SEK differs from the model's 0.00 by +39.00: check the courtage class.", lines);
    }
}
