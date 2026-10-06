using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Trading.Accounts;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// One of your Avanza accounts and what live trading (R1) thinks of it. <see cref="Id"/> holds the full number in
/// memory only; everything shown uses <see cref="AccountId.Masked"/> (its <c>ToString</c> is masked too).
/// </summary>
public sealed record AccountSummary(
    AccountId Id,
    string Type,
    string Name,
    string Currency,
    decimal TotalValue,
    decimal BuyingPower,
    decimal? AvailableForPurchase,
    bool Tradable,
    IReadOnlyList<string> LiveProblems,
    bool IsLiveAccount)
{
    /// <summary>Gets a value indicating whether this account may be the one live trading uses (R1: an ISK, tradable, not managed, no credit).</summary>
    public bool CanTradeLive => LiveProblems.Count == 0;
}

/// <summary>
/// Your accounts, their holdings and the live-trading account (R1), from one login (docs/plans/11-app-redesign.md).
/// Read-only: the account, trading-account and positions reads the CLI already makes. <c>qa accounts</c> builds its
/// rows with the same <see cref="Summaries"/>; the Windows app's Accounts page loads the whole overview.
/// </summary>
public sealed record AccountOverview(
    IReadOnlyList<AccountSummary> Accounts,
    PortfolioSnapshot Portfolio,
    AllowlistResult Allowlist)
{
    /// <summary>Each account with whether it may trade live, and whether it is the one <c>AVANZA__ALLOWEDACCOUNTIDS</c> names.</summary>
    public static IReadOnlyList<AccountSummary> Summaries(IReadOnlyList<Account> accounts, IReadOnlyList<TradingAccount> trading, AllowlistResult allowlist)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(trading);
        ArgumentNullException.ThrowIfNull(allowlist);
        return
        [
            .. accounts.Select(a =>
            {
                TradingAccount? t = trading.FirstOrDefault(x => x.Id == a.Id);
                IReadOnlyList<string> problems = t is null ? [$"{a.Id.Masked} is not a trading account at Avanza."] : AccountAllowlist.Problems(t);
                return new AccountSummary(a.Id, a.Type, a.Name, a.Currency, a.TotalValue, a.BuyingPower, t?.AvailableForPurchase, t?.IsTradable ?? false,
                    problems, allowlist.Allowed && allowlist.Account!.Id == a.Id);
            }),
        ];
    }

    /// <summary>Reads the overview through a logged-in gateway (the caller has made the one login).</summary>
    public static async Task<AccountOverview> ReadAsync(IBrokerGateway gateway, Func<string, string?> getVariable, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        IReadOnlyList<Account> accounts = await gateway.GetAccountsAsync(ct).ConfigureAwait(false);
        IReadOnlyList<TradingAccount> trading = await gateway.GetTradingAccountsAsync(ct).ConfigureAwait(false);
        PortfolioSnapshot portfolio = await gateway.GetPositionsAsync(null, ct).ConfigureAwait(false);
        AllowlistResult allowlist = AccountAllowlist.FromEnvironment(trading, getVariable);
        return new AccountOverview(Summaries(accounts, trading, allowlist), portfolio, allowlist);
    }

    /// <summary>
    /// For the Windows app: one login (BankID or TOTP, with the app's QR code) and the overview, with the same
    /// plumbing, lock and redaction as a command. Nothing is printed.
    /// </summary>
    internal static Task<AccountOverview> LoadAsync(AvanzaCliServices services, string stateDirectory, string login)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AvanzaCommands.QueryAsync(services, stateDirectory, login, (connection, ct) => ReadAsync(connection.Gateway, services.GetVariable, ct));
    }
}
