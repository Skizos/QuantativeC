using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;

namespace QuantAnalyst.Trading.Accounts;

/// <summary>
/// R1, the account allowlist (ADR 0003 R1, docs/plans/07-phase7-confirm.md step 2). The owner names the one account
/// live orders may go to in the <see cref="Variable"/> environment variable, which is never committed. It must name
/// exactly one account, and that account must be among the trading accounts, tradable, an ISK, not discretionary
/// (unknown counts as discretionary) and without credit (R8: no leverage). Every message masks account ids to their
/// last 3 characters.
/// </summary>
public static class AccountAllowlist
{
    public const string Variable = "AVANZA__ALLOWEDACCOUNTIDS";

    /// <summary>Avanza's <c>accountType</c> for an investeringssparkonto (seen live 2026-09-25).</summary>
    public const string IskType = "INVESTERINGSSPARKONTO";

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>Reads <see cref="Variable"/> and checks it against <paramref name="accounts"/>.</summary>
    public static AllowlistResult FromEnvironment(IReadOnlyList<TradingAccount> accounts, Func<string, string?>? getVariable = null) =>
        Resolve((getVariable ?? Environment.GetEnvironmentVariable)(Variable), accounts);

    /// <summary>The one account id in <paramref name="raw"/>, or why there isn't exactly one.</summary>
    public static bool TryParse(string? raw, out AccountId id, out string problem)
    {
        string[] ids = raw?.Split(Separators, StringSplitOptions.RemoveEmptyEntries) ?? [];
        id = default;
        switch (ids.Length)
        {
            case 0:
                problem = $"{Variable} is not set. Set it to the account id of the one ISK that may trade (step O6 in docs/plans/07-phase7-confirm.md).";
                return false;
            case > 1:
                problem = $"{Variable} names {ids.Length} accounts ({string.Join(", ", ids.Select(AccountId.Mask))}); exactly one may trade.";
                return false;
            default:
                id = new AccountId(ids[0]);
                problem = "";
                return true;
        }
    }

    /// <summary>Checks the account named in <paramref name="raw"/> against the broker's trading accounts.</summary>
    public static AllowlistResult Resolve(string? raw, IReadOnlyList<TradingAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (!TryParse(raw, out AccountId id, out string problem))
        {
            return new AllowlistResult(null, [problem]);
        }

        TradingAccount? account = accounts.FirstOrDefault(a => a.Id == id);
        if (account is null)
        {
            string known = accounts.Count == 0 ? "none" : string.Join(", ", accounts.Select(a => a.Id.Masked));
            return new AllowlistResult(null, [$"account {id.Masked} from {Variable} is not among your trading accounts ({known})."]);
        }

        return new AllowlistResult(account, Problems(account));
    }

    /// <summary>Why <paramref name="account"/> may not trade live; empty when it may. Re-checked on every refresh.</summary>
    public static IReadOnlyList<string> Problems(TradingAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        string who = $"account {account.Id.Masked}";
        var problems = new List<string>();
        if (!account.IsTradable)
        {
            problems.Add($"{who} is not tradable at Avanza.");
        }

        if (!string.Equals(account.AccountType, IskType, StringComparison.Ordinal))
        {
            problems.Add($"{who} is a {account.AccountType}, not an ISK ({IskType}); only an ISK may trade (ADR 0003 R1).");
        }

        if (account.IsDiscretionary is not false)
        {
            problems.Add(account.IsDiscretionary is null
                ? $"{who}: Avanza did not say whether it is a discretionary (managed) account; unknown counts as managed."
                : $"{who} is a discretionary (managed) account.");
        }

        if (account.HasCredit)
        {
            problems.Add($"{who} has credit; live trading needs an account without credit (R8: no leverage).");
        }

        return problems;
    }
}

/// <summary>The outcome of the R1 check: the account when it was found, and every problem (masked).</summary>
public sealed record AllowlistResult(TradingAccount? Account, IReadOnlyList<string> Problems)
{
    public bool Allowed => Account is not null && Problems.Count == 0;

    /// <summary>R1's input: the one allowed account id, or none.</summary>
    public IReadOnlySet<string> AllowedAccountIds =>
        Allowed ? new HashSet<string>(StringComparer.Ordinal) { Account!.Id.Value } : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One line for the terminal, masked.</summary>
    public string Describe() =>
        Allowed
            ? $"Live trading account (R1, {AccountAllowlist.Variable}): {Account!.Id.Masked}, ISK, tradable, not managed, no credit: OK."
            : $"Live trading account (R1, {AccountAllowlist.Variable}): refused. {string.Join(" ", Problems)}";
}
