using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuantAnalyst.Avanza;
using QuantAnalyst.Avanza.Auth;
using QuantAnalyst.Avanza.Credentials;
using QuantAnalyst.Avanza.Logging;
using QuantAnalyst.Avanza.Recording;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Accounts;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Instruments;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Core.Orders;
using QuantAnalyst.Data.Calendar;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Accounts;

namespace QuantAnalyst.Cli.Commands;

/// <summary>Seams for the Avanza verbs (tests replace the connection factory; nothing else talks to the network).</summary>
internal sealed record AvanzaCliServices(
    Func<AvanzaOptions, ISecretStore, IBankIdPrompt, ILogger, Redactor, AvanzaConnection> ConnectionFactory,
    Func<string, ISecretStore> SecretStoreFactory)
{
    /// <summary>Gets the clock for long-running verbs (<c>qa paper run</c>); tests pass a fake one.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets where typed confirmations are read (the promotion command); tests pass their own.</summary>
    public TextReader Input { get; init; } = Console.In;

    /// <summary>Gets the owner's promotion key store: Windows Credential Manager (ADR 0003 §3).</summary>
    public Func<Trading.Modes.IPromotionKeyStore> PromotionKeys { get; init; } = PromotionKeyStores.Default;

    /// <summary>
    /// Gets the token that stops a running Avanza verb the way Ctrl+C does (the Windows app's Stop button; a Paper
    /// session then cancels its working orders and writes its partial report).
    /// </summary>
    public CancellationToken Cancellation { get; init; } = CancellationToken.None;

    /// <summary>
    /// Gets who shows the BankID QR code: null draws it in the terminal. Arguments: the error writer and whether the
    /// terminal is interactive. The Windows app shows it as an image.
    /// </summary>
    public Func<TextWriter, bool, IBankIdPrompt>? BankIdPrompt { get; init; }

    /// <summary>
    /// Gets how environment variables are read: the R1 account allowlist, and the Confirm startup check that refuses a
    /// process Claude Code started. Tests pass their own.
    /// </summary>
    public Func<string, string?> GetVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Gets who watches a running Paper session: null in the terminal. The Windows app draws its live charts from it
    /// (quotes, the account's value, order changes, the decision); it can't change anything (GuardedObserver).
    /// </summary>
    public Trading.Observation.ISessionObserver? SessionObserver { get; init; }

    /// <summary>
    /// Gets the order channel a Confirm session sends through: Avanza's. Tests pass a stand-in; whatever it is, the
    /// session sends only with the live authorization the Confirm startup checks issue for that very channel.
    /// </summary>
    internal Func<AvanzaConnection, IBrokerOrderChannel> OrderChannel { get; init; } = connection => connection.CreateOrderChannel();

    /// <summary>Gets where FX rates come from (ADR 0005): the Riksbank's daily fixing. Tests pass a fake.</summary>
    public Func<IFxRateSource> FxRates { get; init; } = () => Data.Fx.RiksbankFxSource.Shared;

    public static AvanzaCliServices Default { get; } = new(
        (options, secrets, prompt, logger, redactor) => AvanzaConnection.Create(options, secrets, logger, redactor, prompt),
        CreateSecretStore);

    private static ISecretStore CreateSecretStore(string kind) => kind switch
    {
        "env" => new EnvironmentSecretStore(),
        "credman" when OperatingSystem.IsWindows() => new WindowsCredentialStore(),
        "credman" => throw new ArgumentException("--secret-store credman needs Windows; use --secret-store env for development."),
        _ => throw new ArgumentException($"Unknown secret store '{kind}' (credman|env)."),
    };
}

/// <summary>
/// Read-only Avanza verbs (Phases 3–4). Every invocation is one trigger: at most one login, never retried
/// (CLAUDE.md "Absolute safety rules"). There are no order or money-transfer verbs.
/// Exit codes: 0 ok, 1 error, 3 halt (schema drift / session gone / endpoint moved), 4 login locked.
/// </summary>
internal static partial class AvanzaCommands
{
    public const int ExitHalt = 3;
    public const int ExitLocked = 4;

    private sealed class Common
    {
        public Option<string> StateDir { get; } = new("--state-dir") { Description = "Folder for state/auth.json (login lock)", DefaultValueFactory = _ => "state" };

        public Option<string> SecretStore { get; } = new("--secret-store")
        {
            Description = "Where credentials come from: credman (Windows Credential Manager) or env (development)",
            DefaultValueFactory = _ => OperatingSystem.IsWindows() ? "credman" : "env",
        };

        public Option<string> Login { get; } = CreateLoginOption();

        public Option<bool> Verbose { get; } = new("--verbose") { Description = "Debug logging to stderr (redacted)" };

        public Option<bool> Json { get; } = new("--json") { Description = "JSON output" };

        public void AddTo(Command c, bool json = true)
        {
            c.Options.Add(StateDir);
            c.Options.Add(Login);
            c.Options.Add(SecretStore);
            c.Options.Add(Verbose);
            if (json)
            {
                c.Options.Add(Json);
            }
        }
    }

    /// <summary>Environment variable that selects the default login method (bankid|totp).</summary>
    public const string LoginMethodVariable = "QA_AVANZA_LOGIN";

    private static Option<string> CreateLoginOption()
    {
        var option = new Option<string>("--login")
        {
            Description = $"bankid (default: approve with the BankID app) or totp (unattended; needs 'qa secrets set'). Default from {LoginMethodVariable}.",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable(LoginMethodVariable) is { Length: > 0 } v ? v.Trim().ToLowerInvariant() : "bankid",
        };
        option.AcceptOnlyFromAmong("bankid", "totp");
        return option;
    }

    public static IEnumerable<Command> Create(AvanzaCliServices services)
    {
        yield return Login(services);
        yield return Accounts(services);
        yield return Positions(services);
        yield return Orders(services);
        yield return Quote(services);
        yield return Stream(services);
        yield return Paper(services);
        yield return Trade(services);
        yield return Rebalance(services);
        yield return History(services);
        yield return Probe(services);
        yield return Recordings(services);
        yield return Secrets();
    }

    // ---- qa login -------------------------------------------------------------------------------------

    private static Command Login(AvanzaCliServices services)
    {
        var common = new Common();
        var clearLock = new Option<bool>("--clear-lock")
        {
            Description = "Clear a persisted login lock. Only after you have checked with BankID on avanza.se that login works.",
        };
        var command = new Command("login", "Log in once (BankID by default, or TOTP) and check the session. Read-only.");
        common.AddTo(command, json: false);
        command.Options.Add(clearLock);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            if (parse.GetValue(clearLock))
            {
                ctx.Connection.Authenticator.ClearLock();
                output.WriteLine($"Login lock cleared ({ctx.Connection.Authenticator.StateFile}). No login was attempted.");
                return 0;
            }

            LoginResult login = await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            SessionHealth health = await ctx.Connection.Gateway.GetSessionHealthAsync(ctx.Ct).ConfigureAwait(false);
            output.WriteLine($"Logged in with {(login.Method == AvanzaLoginMethod.BankId ? "BankID" : "TOTP")}; security token from the {login.TokenSource}. Session health: loggedIn={health.LoggedIn}.");
            output.WriteLine("Read-only: this build has no order or money-transfer capability.");
            return health.LoggedIn ? 0 : ExitHalt;
        }));
        return command;
    }

    // ---- qa accounts ----------------------------------------------------------------------------------

    private static Command Accounts(AvanzaCliServices services)
    {
        var common = new Common();
        var command = new Command("accounts", "List accounts with value and buying power (account ids masked).");
        common.AddTo(command);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            IReadOnlyList<Account> accounts = await ctx.Connection.Gateway.GetAccountsAsync(ctx.Ct).ConfigureAwait(false);
            IReadOnlyList<TradingAccount> trading = await ctx.Connection.Gateway.GetTradingAccountsAsync(ctx.Ct).ConfigureAwait(false);
            AllowlistResult allowlist = AccountAllowlist.FromEnvironment(trading, services.GetVariable);
            IReadOnlyList<AccountSummary> summaries = AccountOverview.Summaries(accounts, trading, allowlist);
            var rows = accounts.Zip(summaries, (a, s) =>
            {
                TradingAccount? t = trading.FirstOrDefault(x => x.Id == a.Id);
                return new
                {
                    account = a.Id.Masked,
                    a.Type,
                    a.Name,
                    a.Status,
                    a.Currency,
                    a.TotalValue,
                    a.BuyingPower,
                    AvailableForPurchase = t?.AvailableForPurchase,
                    Tradable = t?.IsTradable,
                    AllowedForLiveTrading = s.IsLiveAccount,
                };
            }).ToList();

            if (parse.GetValue(common.Json))
            {
                output.WriteLine(JsonSerializer.Serialize(rows, QaCli.Json));
                return 0;
            }

            var table = new TextTable(("account", false), ("type", false), ("name", false), ("status", false), ("total value", true),
                ("buying power", true), ("available", true), ("tradable", false));
            foreach (var r in rows)
            {
                table.Add(r.account, r.Type, r.Name, r.Status, Money(r.TotalValue, r.Currency), Money(r.BuyingPower, r.Currency),
                    r.AvailableForPurchase is { } av ? Money(av, r.Currency) : "-", r.Tradable is { } tr ? (tr ? "yes" : "no") : "-");
            }

            table.Write(output);
            output.WriteLine();
            output.WriteLine(allowlist.Describe());
            return 0;
        }));
        return command;
    }

    // ---- qa positions ---------------------------------------------------------------------------------

    private static Command Positions(AvanzaCliServices services)
    {
        var common = new Common();
        var account = new Option<string?>("--account") { Description = "Only the account whose id ends with these digits (e.g. the last 3)" };
        var command = new Command("positions", "List holdings and cash per account (account ids masked).");
        common.AddTo(command);
        command.Options.Add(account);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            PortfolioSnapshot all = await ctx.Connection.Gateway.GetPositionsAsync(null, ctx.Ct).ConfigureAwait(false);
            string? suffix = parse.GetValue(account);
            PortfolioSnapshot snapshot = suffix is null ? all : Filter(all, suffix);

            if (parse.GetValue(common.Json))
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    positions = snapshot.Positions.Select(p => new
                    {
                        account = p.Account.Masked,
                        orderbookId = p.OrderbookId?.Value,
                        p.InstrumentName,
                        p.Isin,
                        p.InstrumentType,
                        p.Currency,
                        p.Volume,
                        p.Value,
                        p.AverageAcquiredPrice,
                        p.AcquiredValue,
                        p.LastPrice,
                    }),
                    cash = snapshot.Cash.Select(c => new { account = c.Account.Masked, c.Balance, c.Currency }),
                    retrievedAtUtc = snapshot.RetrievedAtUtc,
                }, QaCli.Json));
                return 0;
            }

            var table = new TextTable(("account", false), ("instrument", false), ("isin", false), ("volume", true), ("value", true),
                ("avg price", true), ("last", true), ("ccy", false));
            foreach (Position p in snapshot.Positions)
            {
                table.Add(p.Account.Masked, p.InstrumentName, p.Isin ?? "-", Num(p.Volume), Num(p.Value), p.AverageAcquiredPrice is { } a ? Num(a) : "-",
                    p.LastPrice is { } l ? Num(l) : "-", p.Currency);
            }

            table.Write(output);
            output.WriteLine();
            var cash = new TextTable(("account", false), ("cash", true), ("ccy", false));
            foreach (CashPosition c in snapshot.Cash)
            {
                cash.Add(c.Account.Masked, Num(c.Balance), c.Currency);
            }

            cash.Write(output);
            output.WriteLine($"As of {Local(snapshot.RetrievedAtUtc)} (Europe/Stockholm). Values as reported by Avanza.");
            return 0;
        }));
        return command;
    }

    private static PortfolioSnapshot Filter(PortfolioSnapshot all, string suffix)
    {
        AccountId[] matches = [.. all.Positions.Select(p => p.Account).Concat(all.Cash.Select(c => c.Account)).Distinct().Where(a => a.EndsWith(suffix))];
        return matches.Length switch
        {
            0 => throw new ArgumentException($"No account ends with '{suffix}'."),
            > 1 => throw new ArgumentException($"'{suffix}' matches {matches.Length} accounts; give more digits."),
            _ => all with
            {
                Positions = [.. all.Positions.Where(p => p.Account == matches[0])],
                Cash = [.. all.Cash.Where(c => c.Account == matches[0])],
            },
        };
    }

    // ---- qa orders ------------------------------------------------------------------------------------

    private static Command Orders(AvanzaCliServices services)
    {
        var common = new Common();
        var command = new Command("orders", "List open orders (read-only).");
        common.AddTo(command);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            IReadOnlyList<BrokerOrder> orders = await ctx.Connection.Gateway.GetOpenOrdersAsync(ctx.Ct).ConfigureAwait(false);
            if (parse.GetValue(common.Json))
            {
                output.WriteLine(JsonSerializer.Serialize(orders.Select(o => new
                {
                    orderId = o.Id.Value,
                    account = o.Account.Masked,
                    orderbookId = o.OrderbookId.Value,
                    o.InstrumentName,
                    o.Side,
                    o.Price,
                    o.Volume,
                    o.OriginalVolume,
                    o.State,
                    o.Condition,
                    o.CreatedUtc,
                    o.ValidUntil,
                }), QaCli.Json));
                return 0;
            }

            var table = new TextTable(("order", false), ("account", false), ("instrument", false), ("side", false), ("price", true),
                ("volume", true), ("state", false), ("valid until", false));
            foreach (BrokerOrder o in orders)
            {
                table.Add(o.Id.Value, o.Account.Masked, o.InstrumentName, o.Side.ToString(), Num(o.Price), Num(o.Volume),
                    o.State, o.ValidUntil?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-");
            }

            table.Write(output);
            output.WriteLine($"{orders.Count} open order(s).");
            return 0;
        }));
        return command;
    }

    // ---- qa quote -------------------------------------------------------------------------------------

    private static Command Quote(AvanzaCliServices services)
    {
        var common = new Common();
        var ticker = new Argument<string?>("ticker") { Description = "Ticker, e.g. ERIC-B or \"ERIC B\"", Arity = ArgumentArity.ZeroOrOne };
        var id = new Option<string?>("--id") { Description = "Avanza orderbook id instead of a ticker" };
        var command = new Command("quote", "Quote, order depth and tick size for one instrument (read-only).");
        command.Arguments.Add(ticker);
        common.AddTo(command);
        command.Options.Add(id);
        command.SetAction(parse => Run(parse, services, common, record: null, async (ctx, output) =>
        {
            string? tickerText = parse.GetValue(ticker);
            string? idText = parse.GetValue(id);
            if ((tickerText is null) == (idText is null))
            {
                throw new ArgumentException("Give either a ticker or --id <orderbookId>.");
            }

            await ctx.Connection.Authenticator.LoginAsync(ctx.Ct).ConfigureAwait(false);
            InstrumentTradingParams p = idText is not null
                ? await ctx.Connection.Gateway.GetTradingParamsAsync(new OrderbookId(idText), ctx.Ct).ConfigureAwait(false)
                : await TickerResolver.ResolveAsync(ctx.Connection.Gateway, tickerText!, ctx.Ct).ConfigureAwait(false);
            MarketSnapshot m = await ctx.Connection.Gateway.GetMarketSnapshotAsync(p.OrderbookId, ctx.Ct).ConfigureAwait(false);
            decimal? reference = m.Last ?? m.Bid ?? m.Ask;
            decimal? tick = reference is { } r && p.TickSizes.Bands[0].Min <= r ? p.TickSizes.TickAt(r) : null;

            if (parse.GetValue(common.Json))
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    orderbookId = p.OrderbookId.Value,
                    p.TickerSymbol,
                    p.Name,
                    p.Isin,
                    p.MarketPlace,
                    p.Currency,
                    p.OrderbookStatus,
                    m.Bid,
                    m.Ask,
                    m.Last,
                    m.High,
                    m.Low,
                    m.ChangePercent,
                    m.TotalVolumeTraded,
                    m.TimeOfLastUtc,
                    m.UpdatedUtc,
                    tickAtLast = tick,
                    lot = p.TradingUnit,
                    p.VolumeFactor,
                    depth = m.Depth,
                    retrievedAtUtc = m.RetrievedAtUtc,
                }, QaCli.Json));
                return 0;
            }

            output.WriteLine($"{p.TickerSymbol ?? "?"}  {p.Name}  (orderbook {p.OrderbookId}, {p.Isin}, {p.MarketPlace}, {p.Currency}{(p.OrderbookStatus is { } status ? ", " + status : string.Empty)})");
            output.WriteLine($"bid {Opt(m.Bid)}  ask {Opt(m.Ask)}  last {Opt(m.Last)}  high {Opt(m.High)}  low {Opt(m.Low)}  change {Opt(m.ChangePercent)} %");
            output.WriteLine($"last trade {(m.TimeOfLastUtc is { } t ? Local(t) : "-")}  updated {(m.UpdatedUtc is { } u ? Local(u) : "-")}  volume {Num(m.TotalVolumeTraded)}");
            output.WriteLine($"tick size at last {Opt(tick)}  lot {p.TradingUnit}  volume factor {p.VolumeFactor}  ({p.TickSizes.Bands.Count} tick band(s) from Avanza)");
            if (m.Depth.Count > 0)
            {
                var depth = new TextTable(("bid vol", true), ("bid", true), ("ask", true), ("ask vol", true));
                foreach (DepthLevel d in m.Depth.Take(5))
                {
                    depth.Add(Num(d.BidVolume), Opt(d.BidPrice), Opt(d.AskPrice), Num(d.AskVolume));
                }

                depth.Write(output);
            }

            output.WriteLine($"Retrieved {Local(m.RetrievedAtUtc)} (Europe/Stockholm). Polled snapshot, not a live stream.");
            return 0;
        }));
        return command;
    }

    // ---- qa probe -------------------------------------------------------------------------------------

    private static Command Probe(AvanzaCliServices services)
    {
        var common = new Common();
        var ticker = new Option<string>("--ticker") { Description = "Instrument used for search/orderbook/marketdata/chart", DefaultValueFactory = _ => "ERIC-B" };
        var recordDir = new Option<string>("--record-dir") { Description = "Raw recordings folder (git-ignored)", DefaultValueFactory = _ => Path.Combine("recordings", "live") };
        var noRecord = new Option<bool>("--no-record") { Description = "Do not record responses" };
        var preflight = new Option<bool>("--preflight")
        {
            Description = "Also ask Avanza's two read-only pre-trade checks (validate, preliminary fee) about a hypothetical 1-share buy at the ask, to record their real answers. Nothing is placed.",
        };
        var account = new Option<string?>("--account") { Description = "With --preflight: the account, by the last digits of its id (needed when several can trade)" };
        var command = new Command("probe", "One login, then every Phase 3 read; reports OK/DRIFT per endpoint and records the raw responses.");
        common.AddTo(command, json: false);
        command.Options.Add(ticker);
        command.Options.Add(recordDir);
        command.Options.Add(noRecord);
        command.Options.Add(preflight);
        command.Options.Add(account);
        command.SetAction(parse => Run(parse, services, common, parse.GetValue(noRecord) ? null : parse.GetValue(recordDir), async (ctx, output) =>
        {
            if (parse.GetValue(account) is not null && !parse.GetValue(preflight))
            {
                throw new ArgumentException("--account is only used with --preflight.");
            }

            var options = new ProbeOptions(parse.GetValue(preflight), parse.GetValue(account));
            IReadOnlyList<ProbeResult> results = await ctx.Connection.Probe.RunAsync(parse.GetValue(ticker)!, options, ctx.Ct).ConfigureAwait(false);
            var table = new TextTable(("route", false), ("result", false), ("detail", false));
            foreach (ProbeResult r in results)
            {
                table.Add(r.Route, r.Status.ToString().ToUpperInvariant(), r.Detail);
            }

            table.Write(output);
            if (ctx.Connection.RecordingDirectory is { } dir)
            {
                output.WriteLine();
                output.WriteLine($"Raw recordings: {dir}");
                output.WriteLine($"Next: qa recordings sanitize --in \"{dir}\" --out \"recordings/fixtures/avanza/{DateTime.UtcNow:yyyy-MM-dd}\"");
            }

            return results.Any(r => r.Status is ProbeStatus.Stopped or ProbeStatus.HttpError) ? 1
                : results.Any(r => r.Status == ProbeStatus.Drift) ? ExitHalt
                : 0;
        }));
        return command;
    }

    // ---- qa recordings sanitize -----------------------------------------------------------------------

    private static Command Recordings(AvanzaCliServices services)
    {
        var input = new Option<string>("--in") { Description = "Raw recording folder, e.g. recordings/live/20260926T070000Z", Required = true };
        var output = new Option<string>("--out") { Description = "New fixture folder, e.g. recordings/fixtures/avanza/2026-09-26", Required = true };
        var keepAmounts = new Option<bool>("--keep-amounts") { Description = "Keep balances/volumes/prices on personal routes" };
        var secretStore = new Option<string>("--secret-store")
        {
            Description = "Store whose values must not appear in the output (checked in memory, never printed)",
            DefaultValueFactory = _ => OperatingSystem.IsWindows() ? "credman" : "env",
        };
        var sanitize = new Command("sanitize", "Make raw recordings safe to commit: mask ids and names, replace personal amounts, scan for leaks.");
        sanitize.Options.Add(input);
        sanitize.Options.Add(output);
        sanitize.Options.Add(keepAmounts);
        sanitize.Options.Add(secretStore);
        sanitize.SetAction(parse => QaCli.Execute(parse, w =>
        {
            var forbidden = new List<string>();
            try
            {
                AvanzaCredentials c = services.SecretStoreFactory(parse.GetValue(secretStore)!).GetAvanzaCredentials();
                forbidden.AddRange([c.Username.Reveal(), c.Password.Reveal(), c.TotpSecret.Reveal()]);
            }
            catch (Exception ex) when (ex is SecretStoreException or ArgumentException)
            {
                parse.InvocationConfiguration.Error.WriteLine($"warning: secret-store check skipped ({ex.Message})");
            }

            SanitizeReport report = RecordingSanitizer.Sanitize(parse.GetValue(input)!, parse.GetValue(output)!, new SanitizeOptions
            {
                KeepAmounts = parse.GetValue(keepAmounts),
                ForbiddenValues = forbidden,
            });
            foreach ((string rule, int count) in report.Replacements.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                w.WriteLine($"{rule}: {count}");
            }

            if (!report.Succeeded)
            {
                foreach (string p in report.Problems)
                {
                    w.WriteLine($"LEAK: {p}");
                }

                w.WriteLine("Nothing was written. Fix the cause (or report it) before sharing recordings.");
                return 2;
            }

            w.WriteLine($"Sanitized {report.Files} file(s) into {parse.GetValue(output)}. Review them before committing.");
            return 0;
        }));

        var command = new Command("recordings", "Work with recorded Avanza responses.");
        command.Subcommands.Add(sanitize);
        return command;
    }

    // ---- qa secrets ----------------------------------------------------------------------------------

    private static Command Secrets()
    {
        var set = new Command("set", "Store the Avanza username, password and TOTP secret in Windows Credential Manager (prompts, no echo).");
        set.SetAction(parse => QaCli.Execute(parse, w =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new ArgumentException("qa secrets set needs Windows Credential Manager; on other systems use QA_AVANZA_* environment variables for development.");
            }

            var store = new WindowsCredentialStore();
            string user = Prompt(w, "Avanza username");
            string password = Prompt(w, "Avanza password");
            string totp = Prompt(w, "TOTP secret (Base32, from Avanza's authenticator setup)");
            TotpSecretValidator.Validate(totp);
            WindowsCredentialStore.Write(store.LoginTarget, user, new Secret(password));
            WindowsCredentialStore.Write(store.TotpTarget, "totp", new Secret(totp));
            w.WriteLine($"Stored {store.LoginTarget} and {store.TotpTarget} (values not shown). Check with 'qa secrets check'.");
            return 0;
        }));

        var check = new Command("check", "Show which credentials are configured (never their values).");
        check.SetAction(parse => QaCli.Execute(parse, w =>
        {
            if (OperatingSystem.IsWindows())
            {
                var store = new WindowsCredentialStore();
                w.WriteLine($"{store.Name}: {store.LoginTarget} {(WindowsCredentialStore.Exists(store.LoginTarget) ? "present" : "MISSING")}, " +
                            $"{store.TotpTarget} {(WindowsCredentialStore.Exists(store.TotpTarget) ? "present" : "MISSING")}");
            }

            foreach (string v in new[] { EnvironmentSecretStore.UsernameVariable, EnvironmentSecretStore.PasswordVariable, EnvironmentSecretStore.TotpSecretVariable })
            {
                w.WriteLine($"env {v}: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)) ? "not set" : "set")}");
            }

            return 0;
        }));

        var command = new Command("secrets", "Manage where the Avanza credentials are stored.");
        command.Subcommands.Add(set);
        command.Subcommands.Add(check);
        return command;
    }

    // ---- shared plumbing ------------------------------------------------------------------------------

    /// <param name="Interactive">True when stderr is the real console (Ctrl+C handling, in-place QR redraw).</param>
    private sealed record Ctx(AvanzaConnection Connection, ILogger Logger, bool Interactive, CancellationToken Ct);

    /// <param name="live">
    /// False: output is buffered and redacted as a whole at the end. True (long-running verbs): every line is redacted
    /// and written as soon as it is complete.
    /// </param>
    private static int Run(
        ParseResult parse, AvanzaCliServices services, Common common, string? record, Func<Ctx, TextWriter, Task<int>> body, bool live = false)
    {
        TextWriter output = parse.InvocationConfiguration.Output;
        TextWriter error = parse.InvocationConfiguration.Error;
        var redactor = new Redactor();
        var logger = new RedactingLogger(error, redactor, parse.GetValue(common.Verbose) ? LogLevel.Debug : LogLevel.Warning);
        TextWriter buffer = live ? new RedactingLineWriter(output, redactor) : new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            ISecretStore secrets = services.SecretStoreFactory(parse.GetValue(common.SecretStore)!);
            var options = new AvanzaOptions
            {
                StateDirectory = parse.GetValue(common.StateDir)!,
                RecordingDirectory = record,
                LoginMethod = parse.GetValue(common.Login) == "totp" ? AvanzaLoginMethod.Totp : AvanzaLoginMethod.BankId,
            };
            bool interactive = ReferenceEquals(error, Console.Error) && !Console.IsErrorRedirected;
            IBankIdPrompt prompt = services.BankIdPrompt?.Invoke(error, interactive) ?? new ConsoleBankIdPrompt(error, interactive);
            using AvanzaConnection connection = services.ConnectionFactory(options, secrets, prompt, logger, redactor);
            int code = body(new Ctx(connection, logger, interactive, services.Cancellation), buffer).GetAwaiter().GetResult();
            Flush(buffer, output, redactor);
            return code;
        }
        catch (Exception ex) when (ex is BrokerException or SecretStoreException or ArgumentException or IOException or InvalidDataException
                                       or HistoryImportException or HistoryStoreException or CalendarConfigException
                                   || DataCommands.IsStoreFailure(ex))
        {
            Flush(buffer, output, redactor);
            if (DataCommands.IsStoreFailure(ex))
            {
                error.WriteLine(redactor.Redact($"error: {DataCommands.StoreFailureMessage(ex)}"));
                return 1;
            }

            (int code, string prefix) = ex switch
            {
                LoginLockedException => (ExitLocked, "LOCKED"),
                SchemaDriftException { HaltsTrading: true } or SessionExpiredException or EndpointGoneException => (ExitHalt, "HALT"),
                SchemaDriftException => (ExitHalt, "FEATURE DISABLED"),
                _ => (1, "error"),
            };
            error.WriteLine(redactor.Redact($"{prefix}: {ex.Message}"));
            return code;
        }
    }

    /// <summary>
    /// One read-only query for the Windows app, with a command's plumbing: the secret store, the login method, the app's
    /// BankID prompt, the login lock, redaction and cancellation. It logs in once (never retried) and runs
    /// <paramref name="body"/>; failures propagate as the broker's own exceptions for the app to explain.
    /// </summary>
    internal static async Task<T> QueryAsync<T>(AvanzaCliServices services, string stateDirectory, string login, Func<AvanzaConnection, CancellationToken, Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(body);
        var redactor = new Redactor();
        var logger = new RedactingLogger(TextWriter.Null, redactor, LogLevel.Warning);
        ISecretStore secrets = services.SecretStoreFactory(OperatingSystem.IsWindows() ? "credman" : "env");
        var options = new AvanzaOptions
        {
            StateDirectory = stateDirectory,
            LoginMethod = login == "totp" ? AvanzaLoginMethod.Totp : AvanzaLoginMethod.BankId,
        };
        IBankIdPrompt prompt = services.BankIdPrompt?.Invoke(TextWriter.Null, false) ?? new ConsoleBankIdPrompt(TextWriter.Null, false);
        using AvanzaConnection connection = services.ConnectionFactory(options, secrets, prompt, logger, redactor);
        await connection.Authenticator.LoginAsync(services.Cancellation).ConfigureAwait(false);
        return await body(connection, services.Cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// A read that needs no login (the app's share search: Avanza's own site searches logged out, and the Go SDK lists
    /// search as public; docs/research/avanza-endpoints.md). The same plumbing as <see cref="QueryAsync{T}"/>, but it
    /// never logs in: an answer of 401/403 comes back as <see cref="SessionExpiredException"/> and the caller decides
    /// whether a login is worth it. The login lock still applies.
    /// </summary>
    internal static async Task<T> PublicQueryAsync<T>(AvanzaCliServices services, string stateDirectory, Func<AvanzaConnection, CancellationToken, Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(body);
        var redactor = new Redactor();
        var logger = new RedactingLogger(TextWriter.Null, redactor, LogLevel.Warning);
        ISecretStore secrets = services.SecretStoreFactory(OperatingSystem.IsWindows() ? "credman" : "env");
        var options = new AvanzaOptions { StateDirectory = stateDirectory };
        using AvanzaConnection connection = services.ConnectionFactory(options, secrets, new ConsoleBankIdPrompt(TextWriter.Null, false), logger, redactor);
        return await body(connection, services.Cancellation).ConfigureAwait(false);
    }

    private static void Flush(TextWriter buffer, TextWriter output, Redactor redactor)
    {
        if (buffer is StringWriter sw)
        {
            output.Write(redactor.Redact(sw.ToString()));
        }
        else
        {
            buffer.Flush();
        }
    }

    private static string Prompt(TextWriter w, string label)
    {
        w.Write($"{label}: ");
        w.Flush();
        var sb = new System.Text.StringBuilder();
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine()?.Trim() ?? throw new ArgumentException($"{label}: no input.");
        }

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            sb.Append(key.KeyChar);
        }

        w.WriteLine();
        return sb.Length > 0 ? sb.ToString() : throw new ArgumentException($"{label}: empty input.");
    }

    private static string Money(decimal value, string currency) => $"{value.ToString("N2", CultureInfo.InvariantCulture)} {currency}";

    private static string Num(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Opt(decimal? value) => value is { } v ? Num(v) : "-";

    private static string Local(DateTimeOffset utc) =>
        MarketTime.ToStockholm(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
