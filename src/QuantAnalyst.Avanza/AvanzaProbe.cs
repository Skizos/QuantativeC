using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Http;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
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

/// <summary>
/// Opt-in extras of the probe. <see cref="Preflight"/> also asks Avanza's two read-only pre-trade checks (validate and
/// preliminary fee) about a hypothetical 1-share buy at the ask, so their real answers get recorded. Nothing is placed.
/// <see cref="AccountSuffix"/> picks the account by the end of its id when several can trade.
/// </summary>
public sealed record ProbeOptions(bool Preflight = false, string? AccountSuffix = null);

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
/// (so one run reports every mismatch) and stopping on 401/403. Meant to run with recording on. With
/// <see cref="ProbeOptions.Preflight"/> it ends with Avanza's two pre-trade checks (Phase 7 step 1); nothing is placed.
/// </summary>
public sealed class AvanzaProbe
{
    private readonly AvanzaAuthenticator _auth;
    private readonly AvanzaGateway _gateway;
    private readonly Orders.AvanzaPreflight _preflight;
    private readonly TimeProvider _time;

    internal AvanzaProbe(AvanzaAuthenticator auth, AvanzaGateway gateway, Orders.AvanzaPreflight preflight, TimeProvider time)
    {
        _auth = auth;
        _gateway = gateway;
        _preflight = preflight;
        _time = time;
    }

    public Task<IReadOnlyList<ProbeResult>> RunAsync(string ticker, CancellationToken ct) => RunAsync(ticker, new ProbeOptions(), ct);

    public async Task<IReadOnlyList<ProbeResult>> RunAsync(string ticker, ProbeOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<ProbeResult>();
        try
        {
            LoginResult login = await _auth.LoginAsync(ct).ConfigureAwait(false);
            results.Add(new("login", ProbeStatus.Ok, $"{(login.Method == AvanzaLoginMethod.BankId ? "BankID" : "TOTP")}; security token from {login.TokenSource}"));
        }
        catch (BrokerException ex)
        {
            results.Add(new("login", ProbeStatus.Stopped, ex.Message));
            return results;
        }

        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        OrderbookId? id = null;
        IReadOnlyList<TradingAccount> tradingAccounts = [];
        InstrumentTradingParams? instrument = null;
        MarketSnapshot? market = null;
        var steps = new List<(string Route, Func<Task<string>> Run)>
        {
            (AvanzaRoutes.SessionInfo.Name, async () => $"loggedIn={(await _gateway.GetSessionHealthAsync(ct).ConfigureAwait(false)).LoggedIn}"),
            (AvanzaRoutes.AccountsOverview.Name, async () => $"{(await _gateway.GetAccountsAsync(ct).ConfigureAwait(false)).Count} account(s)"),
            (AvanzaRoutes.TradingAccounts.Name, async () =>
            {
                tradingAccounts = await _gateway.GetTradingAccountsAsync(ct).ConfigureAwait(false);
                return $"{tradingAccounts.Count} trading account(s)";
            }),
            (AvanzaRoutes.Positions.Name, async () =>
            {
                PortfolioSnapshotSummary s = Summarize(await _gateway.GetPositionsAsync(null, ct).ConfigureAwait(false));
                return $"{s.Positions} position(s), {s.Cash} cash row(s)";
            }),
            (AvanzaRoutes.Orders.Name, async () => $"{(await _gateway.GetOpenOrdersAsync(ct).ConfigureAwait(false)).Count} open order(s)"),
            (AvanzaRoutes.Deals.Name, async () =>
            {
                try
                {
                    return $"{(await _gateway.GetDealsAsync(ct).ConfigureAwait(false)).Count} deal(s)";
                }
                catch (EndpointNotModelledException ex)
                {
                    return $"RECORDED ({ex.Message})";
                }
            }),
            (AvanzaRoutes.Transactions.Name, async () => $"{(await _gateway.GetTransactionsAsync(today.AddDays(-30), today, ct).ConfigureAwait(false)).Count} transaction(s) in 30 days"),
            (AvanzaRoutes.Search.Name, async () =>
            {
                // The first hit is the fallback id for marketdata/chart, so an orderbook drift doesn't hide them.
                IReadOnlyList<InstrumentSearchHit> hits = await _gateway.SearchStocksAsync(TickerResolver.Normalize(ticker), 10, ct).ConfigureAwait(false);
                id = hits.Count > 0 ? hits[0].OrderbookId : null;
                return hits.Count > 0 ? $"{hits.Count} hit(s); first: {hits[0].Name} (orderbook {hits[0].OrderbookId})" : "0 hits";
            }),
            (AvanzaRoutes.Orderbook.Name, async () =>
            {
                InstrumentTradingParams p = await TickerResolver.ResolveAsync(_gateway, ticker, ct).ConfigureAwait(false);
                id = p.OrderbookId;
                instrument = p;
                return $"{p.TickerSymbol} = orderbook {p.OrderbookId}, {p.TickSizes.Bands.Count} tick band(s), lot {p.TradingUnit}";
            }),
            (AvanzaRoutes.MarketData.Name, async () =>
            {
                if (id is null)
                {
                    throw new SkipException("no orderbook id (search found nothing)");
                }

                MarketSnapshot m = await _gateway.GetMarketSnapshotAsync(id.Value, ct).ConfigureAwait(false);
                market = m;
                return $"bid {m.Bid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} / ask {m.Ask?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, {m.Depth.Count} depth level(s)";
            }),
            (AvanzaRoutes.PriceChart.Name, async () =>
            {
                if (id is null)
                {
                    throw new SkipException("no orderbook id (search found nothing)");
                }

                PriceHistory history = await _gateway.GetPriceHistoryAsync(id.Value, ChartPeriod.OneMonth, ChartResolution.Day, ct).ConfigureAwait(false);
                return $"{history.Bars.Count} bar(s) at resolution {history.Resolution}";
            }),
        };

        if (options.Preflight)
        {
            steps.Add(("preflight", () => PreflightAsync(options, tradingAccounts, instrument, market, ct)));
        }

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
            catch (PreflightFaultException ex)
            {
                results.Add(new(route, ex.Status, ex.Message));
                if (ex.Status == ProbeStatus.Stopped)
                {
                    return results;
                }
            }
            catch (Exception ex) when (ex is BrokerException or ArgumentException)
            {
                results.Add(new(route, ProbeStatus.HttpError, ex.Message));
            }
        }

        return results;
    }

    /// <summary>A hypothetical 1-share buy at the ask (rounded to the tick), checked but never placed.</summary>
    private async Task<string> PreflightAsync(
        ProbeOptions options, IReadOnlyList<TradingAccount> accounts, InstrumentTradingParams? p, MarketSnapshot? m, CancellationToken ct)
    {
        if (p is null || m is null)
        {
            throw new SkipException("needs the orderbook and marketdata steps to succeed first");
        }

        TradingAccount[] tradable = [.. accounts.Where(a => a.IsTradable)];
        TradingAccount[] chosen = options.AccountSuffix is { Length: > 0 } suffix
            ? [.. tradable.Where(a => a.Id.Value.EndsWith(suffix.Trim(), StringComparison.Ordinal))]
            : tradable;
        if (chosen.Length != 1)
        {
            throw new SkipException(options.AccountSuffix is null
                ? $"{tradable.Length} tradable account(s); pick one with --account <last digits>"
                : $"{chosen.Length} tradable account(s) end with '{options.AccountSuffix}'; need exactly one");
        }

        decimal reference = m.Ask ?? m.Last ?? throw new SkipException("no ask or last price to check against");
        decimal limit = p.TickSizes.RoundForOrder(reference, OrderSide.Buy);
        var request = new PreflightRequest(chosen[0].Id, p.OrderbookId, p.Isin, p.Currency, p.MarketPlace, OrderSide.Buy, 1, limit);
        PreflightOutcome outcome = await _preflight.CheckAsync(request, ct).ConfigureAwait(false);
        string what = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"hypothetical BUY 1 {p.TickerSymbol} @ {limit} on {chosen[0].Id.Masked}; nothing placed");
        if (outcome.Fault != BrokerFault.None || outcome.Validation is null)
        {
            ProbeStatus status = outcome.Fault switch
            {
                BrokerFault.SchemaDrift => ProbeStatus.Drift,
                BrokerFault.SessionExpired => ProbeStatus.Stopped,
                _ => ProbeStatus.HttpError,
            };
            throw new PreflightFaultException(status, $"{outcome.Problem} ({what})");
        }

        string validation = outcome.Validation.AllValid
            ? $"validate: all {outcome.Validation.Checks.Count} valid"
            : $"validate: NOT valid: {string.Join(", ", outcome.Validation.Failures)}";
        string fee = outcome.Fee is { } f
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"fee: commission {f.Commission:0.00}, total fees {f.AllFees:0.00}, total {f.TotalSum:0.00} {f.Currency}")
            : $"fee: {outcome.Problem}";
        return $"{validation}; {fee} ({what})";
    }

    private static PortfolioSnapshotSummary Summarize(Core.Accounts.PortfolioSnapshot s) => new(s.Positions.Count, s.Cash.Count);

    private readonly record struct PortfolioSnapshotSummary(int Positions, int Cash);

    private sealed class SkipException(string message) : Exception(message);

    private sealed class PreflightFaultException(ProbeStatus status, string message) : Exception(message)
    {
        public ProbeStatus Status { get; } = status;
    }
}
