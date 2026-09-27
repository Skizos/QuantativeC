using System.Globalization;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Modes;

/// <summary>
/// Proof that the Confirm startup checks passed (ADR 0003 §1 and §3; plan 07 step 4). Only <see cref="ConfirmStartup"/>
/// creates one: the constructor is internal, and an IL-scanning architecture test checks that nothing else calls it.
/// <see cref="OrderGateway"/> accepts a channel that is not simulated only with an authorization issued for that very
/// channel instance, for Confirm, and for the one allowed account.
/// </summary>
public sealed class LiveAuthorization
{
    internal LiveAuthorization(TradingMode mode, AccountId account, IBrokerOrderChannel channel, DateTimeOffset issuedUtc, IReadOnlyList<StartupCheck> checks)
    {
        Mode = mode;
        Account = account;
        Channel = channel;
        IssuedUtc = issuedUtc;
        Checks = checks;
    }

    public TradingMode Mode { get; }

    public AccountId Account { get; }

    /// <summary>Gets the channel the checks were run for; the gateway refuses any other.</summary>
    public IBrokerOrderChannel Channel { get; }

    public DateTimeOffset IssuedUtc { get; }

    public IReadOnlyList<StartupCheck> Checks { get; }
}

/// <summary>One startup check, as the terminal and the audit log show it.</summary>
public sealed record StartupCheck(string Name, bool Passed, string Detail)
{
    public override string ToString() => $"{(Passed ? "[ok]" : "[--]")} {Name}: {Detail}";
}

/// <summary>Every check, and the authorization when all of them passed.</summary>
public sealed record ConfirmStartupResult(IReadOnlyList<StartupCheck> Checks, LiveAuthorization? Authorization)
{
    public bool Passed => Authorization is not null;

    public IEnumerable<StartupCheck> Failures => Checks.Where(c => !c.Passed);
}

/// <summary>What the Confirm startup checks read. The CLI composes it after one login.</summary>
public sealed record ConfirmStartupInputs
{
    /// <summary>Gets the promotion folder (<c>promotion/</c>): <c>state.json</c>, else the committed template.</summary>
    public required string PromotionDirectory { get; init; }

    /// <summary>Gets the owner's promotion key store (Windows Credential Manager).</summary>
    public required IPromotionKeyStore PromotionKeys { get; init; }

    /// <summary>Gets the folder the promotion evidence paths are relative to (the repository root).</summary>
    public required string EvidenceBaseDirectory { get; init; }

    public required MarketCalendar Calendar { get; init; }

    public required CostModel Costs { get; init; }

    /// <summary>Gets the <c>./KILL</c> flag file.</summary>
    public required string KillFile { get; init; }

    /// <summary>Gets the <c>state/</c> folder (<c>killed.json</c>, <c>trading-disabled.json</c>, the session lock).</summary>
    public required string StateDirectory { get; init; }

    /// <summary>Gets the lock this session holds, or null when it holds none.</summary>
    public required SessionLock? SessionLock { get; init; }

    public required string AuditDirectory { get; init; }

    /// <summary>Gets R1 against the broker's trading accounts (<see cref="AccountAllowlist.FromEnvironment"/>).</summary>
    public required AllowlistResult Account { get; init; }

    /// <summary>Gets the channel the orders would go to.</summary>
    public required IBrokerOrderChannel Channel { get; init; }

    /// <summary>Gets how environment variables are read (the Claude Code check); the CLI passes the real environment.</summary>
    public required Func<string, string?> GetVariable { get; init; }

    public required TimeProvider Time { get; init; }
}

/// <summary>
/// The Confirm startup gate (ADR 0003 §1 and §3; plan 07 step 4). Every check runs, none stops the others, so the
/// terminal shows the whole list; only when all pass is a <see cref="LiveAuthorization"/> issued:
/// <list type="number">
/// <item>not started from Claude Code (CLAUDE.md: Claude never runs Confirm or Auto)</item>
/// <item>the promotion state verifies with the owner's key (HMAC, mode chain, evidence) and allows Confirm</item>
/// <item>R20: this year's trading calendar and the courtage class are verified</item>
/// <item>the kill switch is off, and no <c>trading-disabled.json</c> (ADR 0002 §5)</item>
/// <item>this session holds the session lock, and the audit chain is intact</item>
/// <item>R1: exactly one tradable ISK without credit, not managed</item>
/// <item>the order channel is real and says it is ready (its order format is final)</item>
/// </list>
/// </summary>
public static class ConfirmStartup
{
    /// <summary>Written by the Phase 9 canary when a read-only check fails; Confirm and Auto refuse to start while it exists.</summary>
    public const string TradingDisabledFile = "trading-disabled.json";

    /// <summary>Claude Code sets these in every shell it starts.</summary>
    public static readonly IReadOnlyList<string> ClaudeCodeVariables = ["CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT"];

    public static ConfirmStartupResult Check(ConfirmStartupInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        DateTimeOffset now = inputs.Time.GetUtcNow();
        StartupCheck[] checks =
        [
            NotClaudeCode(inputs),
            Promoted(inputs),
            Verified(inputs, now),
            KillSwitchOff(inputs),
            TradingNotDisabled(inputs),
            OneSession(inputs),
            AuditChain(inputs),
            AccountAllowed(inputs),
            ChannelReady(inputs),
        ];

        LiveAuthorization? authorization = checks.All(c => c.Passed)
            ? new LiveAuthorization(TradingMode.Confirm, inputs.Account.Account!.Id, inputs.Channel, now, checks)
            : null;
        return new ConfirmStartupResult(checks, authorization);
    }

    private static StartupCheck NotClaudeCode(ConfirmStartupInputs i)
    {
        string[] set = [.. ClaudeCodeVariables.Where(v => !string.IsNullOrEmpty(i.GetVariable(v)))];
        return set.Length == 0
            ? new StartupCheck("not started from Claude Code", true, "started by a person")
            : new StartupCheck("not started from Claude Code", false,
                $"{string.Join(" and ", set)} {(set.Length == 1 ? "is" : "are")} set: Claude Code started this process, and Claude Code never trades live (CLAUDE.md). Start it yourself, in your own terminal.");
    }

    private static StartupCheck Promoted(ConfirmStartupInputs i)
    {
        const string name = "promotion";
        byte[]? key;
        try
        {
            key = i.PromotionKeys.Read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or FormatException or ArgumentException)
        {
            return new StartupCheck(name, false, $"the promotion key could not be read from {i.PromotionKeys.Name} ({ex.Message})");
        }

        if (key is null)
        {
            return new StartupCheck(name, false, $"no promotion key in {i.PromotionKeys.Name}; the owner creates it once and then promotes (ADR 0003 §3)");
        }

        try
        {
            PromotionVerification v = Promotion.Verify(i.PromotionDirectory, key, i.EvidenceBaseDirectory);
            if (!v.Valid)
            {
                return new StartupCheck(name, false, "the promotion state does not verify: " + string.Join("; ", v.Problems));
            }

            PromotionState state = PromotionState.Load(i.PromotionDirectory);
            state.Effective(TradingMode.Confirm);
            return new StartupCheck(name, true, $"Confirm allowed by {v.Records} signed record(s), evidence unchanged");
        }
        catch (Exception ex) when (ex is ModeNotAllowedException or TradingConfigException)
        {
            return new StartupCheck(name, false, ex.Message);
        }
    }

    private static StartupCheck Verified(ConfirmStartupInputs i, DateTimeOffset now)
    {
        const string name = "verified constants (R20)";
        int year = OrderGateway.StockholmDate(now).Year;
        var problems = new List<string>();
        string calendar;
        try
        {
            calendar = i.Calendar.GetYear(year).VerifiedOn is { } on
                ? string.Create(CultureInfo.InvariantCulture, $"calendar {year} verified {on:yyyy-MM-dd}")
                : Problem($"the {year} trading calendar has no verified_on date");
        }
        catch (ArgumentOutOfRangeException)
        {
            calendar = Problem($"the trading calendar has no {year}");
        }

        string costs = i.Costs.VerifiedOn is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"courtage class {i.Costs.Name} verified {c:yyyy-MM-dd}")
            : Problem($"the courtage class {i.Costs.Name} has no verified_on date");
        return problems.Count == 0
            ? new StartupCheck(name, true, $"{calendar}, {costs}")
            : new StartupCheck(name, false, string.Join("; ", problems));

        string Problem(string text)
        {
            problems.Add(text);
            return text;
        }
    }

    private static StartupCheck KillSwitchOff(ConfirmStartupInputs i) =>
        KillSwitch.RecordedKill(i.KillFile, i.StateDirectory) is { } kill
            ? new StartupCheck("kill switch", false, string.Create(CultureInfo.InvariantCulture, $"on since {kill.SinceUtc:u} ({kill.Source}: {kill.Reason}); the owner resets it with qa kill --reset"))
            : new StartupCheck("kill switch", true, "off");

    private static StartupCheck TradingNotDisabled(ConfirmStartupInputs i) =>
        File.Exists(Path.Combine(i.StateDirectory, TradingDisabledFile))
            ? new StartupCheck("trading not disabled", false, $"{Path.Combine(i.StateDirectory, TradingDisabledFile)} exists (a read-only canary check failed, ADR 0002 §5)")
            : new StartupCheck("trading not disabled", true, $"no {TradingDisabledFile}");

    private static StartupCheck OneSession(ConfirmStartupInputs i) =>
        i.SessionLock is null
            ? new StartupCheck("one session", false, "this session does not hold the session lock")
            : new StartupCheck("one session", true, "this session holds the session lock");

    private static StartupCheck AuditChain(ConfirmStartupInputs i)
    {
        AuditVerification v = AuditLog.Verify(i.AuditDirectory);
        return v.Valid
            ? new StartupCheck("audit chain", true, $"intact ({v.Records} records, {v.Files} files)")
            : new StartupCheck("audit chain", false, v.Problem ?? "broken");
    }

    private static StartupCheck AccountAllowed(ConfirmStartupInputs i) =>
        i.Account.Allowed
            ? new StartupCheck("account (R1)", true, $"{i.Account.Account!.Id.Masked}, ISK, tradable, not managed, no credit")
            : new StartupCheck("account (R1)", false, string.Join(" ", i.Account.Problems));

    private static StartupCheck ChannelReady(ConfirmStartupInputs i) =>
        i.Channel is ISimulatedOrderChannel
            ? new StartupCheck("order channel", false, $"'{i.Channel.Name}' is simulated; a live authorization is only for the real channel")
            : i.Channel.NotReadyReason is { } why
                ? new StartupCheck("order channel", false, $"'{i.Channel.Name}' is not ready: {why}")
                : new StartupCheck("order channel", true, $"'{i.Channel.Name}', order format final");
}
