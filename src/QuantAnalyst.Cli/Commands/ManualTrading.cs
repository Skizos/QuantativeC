using System.Globalization;
using QuantAnalyst.Core;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// Plan 23: the owner's buys and sells by hand, shared by <c>qa paper manual|orders|release</c> and the app. Placing a
/// request only writes it to <c>state/paper/manual/</c>; the Paper session sends it through the gateway and its risk
/// checks. Nothing here talks to Avanza.
/// </summary>
internal static class ManualTrading
{
    /// <summary>
    /// Places a request for <paramref name="ticker"/> ("ERIC-B", "eric b"): a buy needs a listed share, a sell a listed or
    /// exiting one (R2 would refuse anything else), a release a manual one.
    /// </summary>
    public static ManualOrderRequest Place(
        string configDir, string stateDir, ManualAction action, string ticker, long quantity, decimal? limit, string source, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        Universe universe = Universe.Load(Path.Combine(configDir, Universe.FileName));
        string wanted = ticker.Trim().Replace('-', ' ');
        UniverseEntry? listed = universe.Entries.FirstOrDefault(e => string.Equals(e.Ticker, wanted, StringComparison.OrdinalIgnoreCase));
        UniverseEntry? exiting = universe.Exiting.FirstOrDefault(e => string.Equals(e.Ticker, wanted, StringComparison.OrdinalIgnoreCase));
        string paperDir = Path.Combine(stateDir, TradingCommands.PaperDirName);
        UniverseEntry entry = action switch
        {
            ManualAction.Buy => listed
                ?? (exiting is not null
                    ? throw new ArgumentException($"{exiting.Ticker} is off the list (exiting): it can only be sold (R2).")
                    : throw new ArgumentException($"{wanted} is not on the list: add it first ('qa universe add', or Find a share in the app). R2 refuses a buy of any other share.")),
            ManualAction.Sell => listed ?? exiting
                ?? throw new ArgumentException($"{wanted} is not on the list (nor exiting), so the Paper session does not trade it."),
            _ => listed ?? exiting ?? throw new ArgumentException($"{wanted} is not on the list."),
        };

        if (action == ManualAction.Release && !PaperBook.ManualIn(paperDir).Contains(entry.OrderbookId)
            && !new ManualOrderInbox(paperDir, time).Pending().Any(r => r.OrderbookId == entry.OrderbookId && r.Action != ManualAction.Release))
        {
            throw new ArgumentException($"{entry.Ticker} is not manual: the strategy trades it already.");
        }

        return new ManualOrderInbox(paperDir, time).Place(action, entry.OrderbookId, entry.Ticker, quantity, limit, source);
    }

    /// <summary>What happens next, e.g. "Buy 7 ERIC B (at the ask) [M261001-7f3a] placed. The running Paper session …".</summary>
    public static string Placed(ManualOrderRequest request, string stateDir)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool running = SessionLock.Holder(stateDir) is not null;
        string when = request.Action == ManualAction.Release
            ? running ? "The running Paper session applies it within seconds." : "The next Paper session applies it before its decision."
            : running
                ? "The running Paper session sends it within seconds in the trading window (09:05–17:20 in Stockholm), through the same risk checks as the strategy's orders."
                : "No Paper session is running: the next one sends it in its trading window. A request is for one trading day.";
        return $"{request.Describe()} [{request.Id}] placed. {when}";
    }

    /// <summary>The waiting requests, today's outcomes and the manual shares, for <c>qa paper orders</c> and the app.</summary>
    public static IReadOnlyList<string> Lines(string stateDir, string configDir, TimeProvider time)
    {
        string paperDir = Path.Combine(stateDir, TradingCommands.PaperDirName);
        var inbox = new ManualOrderInbox(paperDir, time);
        DateOnly today = DateOnly.FromDateTime(Core.Market.MarketTime.ToStockholm(time.GetUtcNow()).DateTime);
        CultureInfo c = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        IReadOnlyList<ManualOrderRequest> pending = inbox.Pending();
        lines.Add(pending.Count == 0 ? "Waiting: none." : $"Waiting ({pending.Count}):");
        lines.AddRange(pending.Select(r => string.Create(c, $"  [{r.Id}] {r.Describe()}, placed {Core.Market.MarketTime.ToStockholm(r.CreatedUtc):yyyy-MM-dd HH:mm}")));
        IReadOnlyList<ManualOrderOutcome> done = inbox.Done(today);
        lines.Add(done.Count == 0 ? "Today: nothing done yet." : $"Today ({done.Count}):");
        lines.AddRange(done.Select(d => string.Create(c, $"  {Core.Market.MarketTime.ToStockholm(d.AtUtc):HH:mm} [{d.Request.Id}] {d.Request.Describe()}: {d.Outcome} ({d.Message})")));
        lines.Add(ManualShares(paperDir, configDir) is { Count: > 0 } manual
            ? $"Manual shares (the strategy leaves them): {string.Join(", ", manual)}. 'qa paper release <TICKER>' gives one back."
            : "Manual shares: none (the strategy trades every listed share).");
        return lines;
    }

    /// <summary>The tickers of the book's manual shares (an id that is no longer on the list shows as itself).</summary>
    public static IReadOnlyList<string> ManualShares(string paperDir, string configDir)
    {
        Universe universe = Universe.Load(Path.Combine(configDir, Universe.FileName));
        return [.. PaperBook.ManualIn(paperDir)
            .Select(id => universe.Entries.Concat(universe.Exiting).FirstOrDefault(e => e.OrderbookId == id)?.Ticker ?? id.Value)
            .Order(StringComparer.Ordinal)];
    }

    public static ManualAction Side(string side) => side.ToUpperInvariant() switch
    {
        "BUY" => ManualAction.Buy,
        "SELL" => ManualAction.Sell,
        _ => throw new ArgumentException($"'{side}' is not buy or sell."),
    };
}
