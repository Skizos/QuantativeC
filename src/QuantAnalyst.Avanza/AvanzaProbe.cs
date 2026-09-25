using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;

namespace QuantAnalyst.Avanza;

public enum ProbeStatus
{
    Ok,
    Drift,
    Recorded,
    HttpError,
    Skipped,
    Stopped,
}

/// <summary>One row of the probe report. <see cref="Detail"/> never contains secrets or full account ids.</summary>
public sealed record ProbeResult(string Route, ProbeStatus Status, string Detail);

/// <summary>Maps a ticker such as "ERIC-B" or "ERIC B" to an orderbook id: search, then confirm via the orderbook's ticker.</summary>
public static class TickerResolver
{
    public static string Normalize(string ticker) =>
        string.Join(' ', ticker.Trim().ToUpperInvariant().Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static async Task<InstrumentTradingParams> ResolveAsync(IBrokerGateway gateway, string ticker, CancellationToken ct, int candidates = 5)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        string wanted = Normalize(ticker);
        IReadOnlyList<InstrumentSearchHit> hits = await gateway.SearchStocksAsync(wanted, 10, ct).ConfigureAwait(false);
        foreach (InstrumentSearchHit hit in hits.Take(candidates))
        {
            InstrumentTradingParams p = await gateway.GetTradingParamsAsync(hit.OrderbookId, ct).ConfigureAwait(false);
            if (p.TickerSymbol is { } symbol && Normalize(symbol) == wanted)
            {
                return p;
            }
        }

        throw new ArgumentException(
            $"No stock with ticker '{wanted}' among the first {Math.Min(candidates, hits.Count)} search hits; pass --id <orderbookId> instead.");
    }
}

/// <summary>
/// Read-only diagnostic run for the Phase 3 stop point: one login, then every Phase 3 read, continuing past drift
/// (so one run reports every mismatch) and stopping on 401/403. Meant to run with recording on.
/// </summary>
public sealed class AvanzaProbe
{
    private readonly AvanzaAuthenticator _auth;
    private readonly AvanzaGateway _gateway;
    private readonly TimeProvider _time;

    internal AvanzaProbe(AvanzaAuthenticator auth, AvanzaGateway gateway, TimeProvider time)
    {
        _auth = auth;
        _gateway = gateway;
        _time = time;
    }

    public async Task<IReadOnlyList<ProbeResult>> RunAsync(string ticker, CancellationToken ct)
    {
        var results = new List<ProbeResult>();
        try
        {
            LoginResult login = await _auth.LoginAsync(ct).ConfigureAwait(false);
            results.Add(new("login", ProbeStatus.Ok, $"security token from {login.TokenSource}"));
        }
        catch (BrokerException ex)
        {
            results.Add(new("login", ProbeStatus.Stopped, ex.Message));
            return results;
        }

        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        OrderbookId? id = null;
        var steps = new List<(string Route, Func<Task<string>> Run)>
        {
            (AvanzaRoutes.SessionInfo.Name, async () => $"loggedIn={(await _gateway.GetSessionHealthAsync(ct).ConfigureAwait(false)).LoggedIn}"),
            (AvanzaRoutes.AccountsOverview.Name, async () => $"{(await _gateway.GetAccountsAsync(ct).ConfigureAwait(false)).Count} account(s)"),
            (AvanzaRoutes.TradingAccounts.Name, async () => $"{(await _gateway.GetTradingAccountsAsync(ct).ConfigureAwait(false)).Count} trading account(s)"),
            (AvanzaRoutes.Positions.Name, async () =>
            {
                PortfolioSnapshotSummary s = Summarize(await _gateway.GetPositionsAsync(null, ct).ConfigureAwait(false));
                return $"{s.Positions} position(s), {s.Cash} cash row(s)";
            }),
            (AvanzaRoutes.Orders.Name, async () => $"{(await _gateway.GetOpenOrdersAsync(ct).ConfigureAwait(false)).Count} open order(s)"),
            (AvanzaRoutes.Deals.Name, async () =>
            {
                byte[] raw = await _gateway.GetRawAsync(AvanzaRoutes.Deals, ct).ConfigureAwait(false);
                return $"RECORDED {raw.Length} bytes (not modelled yet)";
            }),
            (AvanzaRoutes.Transactions.Name, async () => $"{(await _gateway.GetTransactionsAsync(today.AddDays(-30), today, ct).ConfigureAwait(false)).Count} transaction(s) in 30 days"),
            (AvanzaRoutes.Search.Name + "+" + AvanzaRoutes.Orderbook.Name, async () =>
            {
                InstrumentTradingParams p = await TickerResolver.ResolveAsync(_gateway, ticker, ct).ConfigureAwait(false);
                id = p.OrderbookId;
                return $"{p.TickerSymbol} = orderbook {p.OrderbookId}, {p.TickSizes.Bands.Count} tick band(s), lot {p.TradingUnit}";
            }),
            (AvanzaRoutes.MarketData.Name, async () =>
            {
                if (id is null)
                {
                    throw new SkipException("no orderbook id (search/orderbook step failed)");
                }

                MarketSnapshot m = await _gateway.GetMarketSnapshotAsync(id.Value, ct).ConfigureAwait(false);
                return $"bid {m.Bid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} / ask {m.Ask?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {m.Depth.Count} depth level(s)";
            }),
            (AvanzaRoutes.PriceChart.Name, async () =>
            {
                if (id is null)
                {
                    throw new SkipException("no orderbook id (search/orderbook step failed)");
                }

                return $"{(await _gateway.GetPriceHistoryAsync(id.Value, ChartPeriod.OneMonth, ChartResolution.Day, ct).ConfigureAwait(false)).Count} daily bar(s)";
            }),
        };

        foreach ((string route, Func<Task<string>> run) in steps)
        {
            try
            {
                string detail = await run().ConfigureAwait(false);
                results.Add(new(route, detail.StartsWith("RECORDED", StringComparison.Ordinal) ? ProbeStatus.Recorded : ProbeStatus.Ok, detail));
            }
            catch (SchemaDriftException ex)
            {
                results.Add(new(route, ProbeStatus.Drift, ex.Message));
            }
            catch (SessionExpiredException ex)
            {
                results.Add(new(route, ProbeStatus.Stopped, ex.Message + " Stopping (no re-login)."));
                return results;
            }
            catch (SkipException ex)
            {
                results.Add(new(route, ProbeStatus.Skipped, ex.Message));
            }
            catch (Exception ex) when (ex is BrokerException or ArgumentException)
            {
                results.Add(new(route, ProbeStatus.HttpError, ex.Message));
            }
        }

        return results;
    }

    private static PortfolioSnapshotSummary Summarize(Core.Accounts.PortfolioSnapshot s) => new(s.Positions.Count, s.Cash.Count);

    private readonly record struct PortfolioSnapshotSummary(int Positions, int Cash);

    private sealed class SkipException(string message) : Exception(message);
}
