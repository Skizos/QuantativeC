using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Avanza;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.Live;

namespace QuantAnalyst.Cli.Commands;

internal static partial class AvanzaCommands
{
    public const int MaxStreamInstruments = 5;

    // ---- qa stream ------------------------------------------------------------------------------------

    private static Command Stream(AvanzaCliServices services)
    {
        var common = new Common();
        var tickers = new Argument<string[]>("tickers") { Description = $"1–{MaxStreamInstruments} tickers, e.g. ERIC-B VOLV-B", Arity = ArgumentArity.OneOrMore };
        var duration = new Option<double>("--duration") { Description = "Seconds to stream (Ctrl+C stops earlier)", DefaultValueFactory = _ => 60 };
        var poll = new Option<double>("--poll") { Description = "Market-data poll interval in seconds (last trade, volume)", DefaultValueFactory = _ => 5 };
        var recordDir = new Option<string>("--record-dir") { Description = "Raw recordings folder (git-ignored)", DefaultValueFactory = _ => Path.Combine("recordings", "live") };
        var noRecord = new Option<bool>("--no-record") { Description = "Do not record the stream and responses" };
        var command = new Command(
            "stream",
            "Live quotes: pushed order depth plus polled last trade, with a STALE flag (no update for 10 s or stream down). Read-only; records by default.");
        command.Arguments.Add(tickers);
        common.AddTo(command, json: false);
        command.Options.Add(duration);
        command.Options.Add(poll);
        command.Options.Add(recordDir);
        command.Options.Add(noRecord);
        command.SetAction(parse => Run(parse, services, common, parse.GetValue(noRecord) ? null : parse.GetValue(recordDir), async (ctx, output) =>
        {
            string[] names = parse.GetValue(tickers)!;
            double seconds = parse.GetValue(duration);
            double pollSeconds = parse.GetValue(poll);
            if (names.Length is < 1 or > MaxStreamInstruments)
            {
                throw new ArgumentException($"Give 1–{MaxStreamInstruments} tickers (each is one push stream plus one poll; ADR 0002 §3 load budget).");
            }

            if (seconds is <= 0 or > 8 * 3600 || pollSeconds is < 1 or > 60)
            {
                throw new ArgumentException("--duration must be in (0, 28800] seconds and --poll in [1, 60] seconds.");
            }

            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            var instruments = new List<InstrumentTradingParams>();
            foreach (string name in names)
            {
                instruments.Add(await TickerResolver.ResolveAsync(ctx.Connection.Gateway, name, ctx.Ct).ConfigureAwait(false));
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Streaming {string.Join(", ", instruments.Select(p => $"{p.TickerSymbol} ({p.OrderbookId})"))} for {seconds:0.#} s: depth pushed by Avanza, last trade polled every {pollSeconds:0.#} s."));
            output.WriteLine("A quote is STALE when neither has updated for 10 s or the depth stream is down. Times are Europe/Stockholm.");

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
            stop.CancelAfter(TimeSpan.FromSeconds(seconds));
            ConsoleCancelEventHandler? onCancel = null;
            if (ctx.Interactive)
            {
                onCancel = (_, e) =>
                {
                    e.Cancel = true;
                    stop.Cancel();
                };
                Console.CancelKeyPress += onCancel;
            }

            var options = new QuoteComposerOptions { PollInterval = TimeSpan.FromSeconds(pollSeconds) };
            var composers = instruments.Select(p => new QuoteComposer(ctx.Connection.Gateway, p.OrderbookId, options, TimeProvider.System, ctx.Logger)).ToList();
            var stats = instruments.Select(p => new StreamStats(p.TickerSymbol ?? p.OrderbookId.Value)).ToList();
            try
            {
                var subscriptions = composers.Select(c => c.Quotes.Subscribe(capacity: 256)).ToList();
                var printers = subscriptions.Select((s, i) => PrintQuotesAsync(s, stats[i], output)).ToList();
                var runs = composers.Select(c => StopAllOnFailure(c.RunAsync(stop.Token), stop)).ToList();
                try
                {
                    await Task.WhenAll(runs).ConfigureAwait(false);
                }
                finally
                {
                    await Task.WhenAll(printers).ConfigureAwait(false);
                    foreach (Broadcaster<Quote>.Subscription s in subscriptions)
                    {
                        s.Dispose();
                    }
                }
            }
            finally
            {
                if (onCancel is not null)
                {
                    Console.CancelKeyPress -= onCancel;
                }
            }

            output.WriteLine();
            foreach (StreamStats s in stats)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{s.Ticker}: {s.Updates} quote update(s), {s.FromStream} with bid/ask from the depth stream, went stale {s.StaleTransitions} time(s)."));
            }

            if (ctx.Connection.RecordingDirectory is { } dir)
            {
                output.WriteLine($"Raw recordings: {dir}");
                output.WriteLine($"Next: qa recordings sanitize --in \"{dir}\" --out \"recordings/fixtures/avanza/{DateTime.UtcNow:yyyy-MM-dd}-stream\"");
            }

            return 0;
        }, live: true));
        return command;
    }

    private sealed class StreamStats(string ticker)
    {
        public string Ticker { get; } = ticker;

        public int Updates { get; set; }

        public int FromStream { get; set; }

        public int StaleTransitions { get; set; }
    }

    private static async Task StopAllOnFailure(Task run, CancellationTokenSource stop)
    {
        try
        {
            await run.ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Prints a line whenever the visible quote changes (price, size, last or the stale flag).</summary>
    private static async Task PrintQuotesAsync(Broadcaster<Quote>.Subscription subscription, StreamStats stats, TextWriter output)
    {
        string? previous = null;
        bool? wasStale = null;
        try
        {
            await foreach (Quote q in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                string state = q.IsStale ? $"STALE ({q.StaleReason})" : $"{q.BidAskSource.ToString().ToLowerInvariant()}";
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{stats.Ticker,-8} bid {Opt(q.Bid)} x {Num(q.BidVolume)}  ask {Opt(q.Ask)} x {Num(q.AskVolume)}  last {Opt(q.Last)}  {state}");
                if (q.IsStale && wasStale == false)
                {
                    stats.StaleTransitions++;
                }

                wasStale = q.IsStale;
                if (line == previous)
                {
                    continue;
                }

                previous = line;
                stats.Updates++;
                if (q.BidAskSource == QuoteSource.Stream)
                {
                    stats.FromStream++;
                }

                output.WriteLine($"{MarketTime.ToStockholm(q.ComposedAtUtc):HH:mm:ss.f}  {line}");
            }
        }
        catch (Exception) when (subscription.Reader.Completion.IsFaulted)
        {
            // The composer's own failure is reported by its run task (HALT); the printer just stops.
        }
    }
}
