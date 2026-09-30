using System.CommandLine;
using System.Globalization;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Core;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Cli.Commands;

/// <summary>
/// Trading verbs that need no broker (Phase 6): <c>qa universe</c>, <c>qa risk-limits</c>, <c>qa audit verify</c>,
/// <c>qa kill</c>, <c>qa report</c> and the owner's <c>qa promote</c> (TradingCommands.Promotion.cs). <c>qa paper run</c>
/// lives with the Avanza verbs because it logs in.
/// </summary>
internal static partial class TradingCommands
{
    public const string DefaultAuditDir = "audit";
    public const string DefaultStateDir = "state";

    /// <summary>The Paper book's folder under the state folder.</summary>
    public const string PaperDirName = "paper";
    public const string DefaultKillFile = "KILL";

    public static IEnumerable<Command> Create(AvanzaCliServices services)
    {
        yield return UniverseCommand();
        yield return RiskLimitsCommand();
        yield return AuditCommand();
        yield return KillCommand();
        yield return ReportCommand(services.Time);
        yield return PromoteCommand(services);
    }

    public static Option<string?> ConfigDirOption() =>
        new("--config-dir") { Description = "Folder with risk-limits.json, paper.json and universe.json (default: ./config, then next to qa)" };

    /// <summary>--config-dir, else ./config, else next to qa: the first that has risk-limits.json.</summary>
    public static string ResolveConfigDir(string? explicitDir)
    {
        if (explicitDir is not null)
        {
            return explicitDir;
        }

        foreach (string candidate in new[] { "config", Path.Combine(AppContext.BaseDirectory, "config") })
        {
            if (File.Exists(Path.Combine(candidate, RiskLimits.FileName)))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"No {RiskLimits.FileName} found (./config or next to qa); pass --config-dir. Nothing trades without risk limits.");
    }

    // ---- qa universe ---------------------------------------------------------------------------------

    private static Command UniverseCommand()
    {
        var command = new Command("universe", "The instrument allowlist (risk check R2), config/universe.json, by Avanza orderbook id. Empty = every order is rejected.");

        var configDir = ConfigDirOption();
        var list = new Command("list", "Show the allowlist.");
        list.Options.Add(configDir);
        list.SetAction(parse => Execute(parse, w =>
        {
            string path = Path.Combine(ResolveConfigDir(parse.GetValue(configDir)), Universe.FileName);
            Universe u = Universe.Load(path);
            if (u.Entries.Count == 0 && u.Exiting.Count == 0)
            {
                w.WriteLine($"The allowlist is empty ({path}): every order is rejected (R2). Add names with: qa universe add ERIC-B");
                return 0;
            }

            var table = new TextTable(("ticker", false), ("orderbook", false), ("name", false));
            foreach (UniverseEntry e in u.Entries)
            {
                table.Add(e.Ticker, e.OrderbookId.Value, e.Name);
            }

            foreach (UniverseEntry e in u.Exiting)
            {
                table.Add(e.Ticker, e.OrderbookId.Value, $"{e.Name} (exiting: sells only, plan 21)");
            }

            table.Write(w);
            w.WriteLine($"{u.Entries.Count} instrument(s) in {path}{(u.Exiting.Count > 0 ? $", and {u.Exiting.Count} exiting" : string.Empty)}.");
            return 0;
        }));

        var addTickers = new Argument<string[]>("tickers") { Description = "Tickers from the instrument master, e.g. ERIC-B VOLV-B", Arity = ArgumentArity.OneOrMore };
        var addConfig = ConfigDirOption();
        var store = DataCommands.StoreOption();
        var add = new Command("add", "Add instruments by ticker, looked up offline in the instrument master (run 'qa history import <TICKER>' first). Shares in SEK, USD or CAD (USD and CAD trade on paper only, ADR 0005); at most 5 names (a Paper session streams them all).");
        add.Arguments.Add(addTickers);
        add.Options.Add(addConfig);
        add.Options.Add(store);
        add.SetAction(parse => Execute(parse, w =>
        {
            string path = Path.Combine(ResolveConfigDir(parse.GetValue(addConfig)), Universe.FileName);
            Universe u = Universe.Load(path);
            using HistoryStore history = DataCommands.OpenExisting(parse.GetValue(store)!);
            foreach (string ticker in parse.GetValue(addTickers)!)
            {
                (u, UniverseEntry entry) = Allowlist.Add(u, history, ticker);
                w.WriteLine($"added {entry.Ticker} ({entry.OrderbookId}, {entry.Name})");
            }

            u.Save(path);
            w.WriteLine($"{u.Entries.Count} instrument(s) in {path}.");
            return 0;
        }));

        var removeTickers = new Argument<string[]>("tickers") { Description = "Tickers to remove", Arity = ArgumentArity.OneOrMore };
        var removeConfig = ConfigDirOption();
        var removeState = new Option<string>("--state-dir") { Description = "State folder (the Paper book says which shares are still held)", DefaultValueFactory = _ => DefaultStateDir };
        var remove = new Command(
            "remove",
            "Remove instruments from the allowlist. A share the Paper book still holds moves to the exiting list instead: the next session sells it (sells only), then remove it again (plan 21).");
        remove.Arguments.Add(removeTickers);
        remove.Options.Add(removeConfig);
        remove.Options.Add(removeState);
        remove.SetAction(parse => Execute(parse, w =>
        {
            string path = Path.Combine(ResolveConfigDir(parse.GetValue(removeConfig)), Universe.FileName);
            IReadOnlyDictionary<OrderbookId, long> held = PaperBook.HeldIn(Path.Combine(parse.GetValue(removeState)!, PaperDirName));
            Universe u = Universe.Load(path);
            foreach (string ticker in parse.GetValue(removeTickers)!)
            {
                (u, UniverseEntry entry, bool exiting) = Allowlist.Remove(u, ticker, held);
                w.WriteLine(Allowlist.Removed(entry, exiting));
            }

            u.Save(path);
            w.WriteLine($"{u.Entries.Count} instrument(s) in {path}{(u.Exiting.Count > 0 ? $"; exiting: {string.Join(", ", u.Exiting.Select(e => e.Ticker))}" : string.Empty)}.");
            return 0;
        }));

        command.Subcommands.Add(list);
        command.Subcommands.Add(add);
        command.Subcommands.Add(remove);
        return command;
    }

    // ---- qa risk-limits ------------------------------------------------------------------------------

    private static Command RiskLimitsCommand()
    {
        var configDir = ConfigDirOption();
        var account = new Option<decimal?>("--account-value") { Description = "Account value in SEK to size the limits for (default: the paper cash in paper.json)" };
        var command = new Command("risk-limits", "Show the pre-trade limits (ADR 0003 R1–R21) and what they allow for an account of a given size. Offline.");
        command.Options.Add(configDir);
        command.Options.Add(account);
        command.SetAction(parse => Execute(parse, w =>
        {
            string dir = ResolveConfigDir(parse.GetValue(configDir));
            RiskLimits l = RiskLimits.Load(Path.Combine(dir, RiskLimits.FileName));
            decimal value = parse.GetValue(account) ?? PaperConfig.Load(Path.Combine(dir, PaperConfig.FileName)).Cash;
            decimal sized = l.SizingValue(value);
            decimal perOrder = Math.Min(l.MaxOrderValueSek, l.MaxOrderValuePctOfAccount * sized);
            CultureInfo c = CultureInfo.InvariantCulture;
            var table = new TextTable(("check", false), ("limit", false), ("for this account", false));
            table.Add("account cap", l.HasAccountCap ? string.Create(c, $"sized on at most {l.MaxAccountValueSek:N0} SEK") : "none (sized on the whole account)",
                l.Capped(value) ? string.Create(c, $"sized on {sized:N0} SEK, not {value:N0}") : string.Create(c, $"sized on {sized:N0} SEK"));
            table.Add("R6 order value", string.Create(c, $"min({l.MaxOrderValueSek:N0} SEK, {l.MaxOrderValuePctOfAccount:P0} of account)"), string.Create(c, $"{perOrder:N0} SEK per order"));
            table.Add("R7 position", string.Create(c, $"{l.MaxPositionPctOfAccount:P0} of account"), string.Create(c, $"{l.MaxPositionPctOfAccount * sized:N0} SEK per instrument"));
            table.Add("R8 gross exposure", string.Create(c, $"{l.MaxGrossExposurePct:P0} of account"), string.Create(c, $"{l.MaxGrossExposurePct * sized:N0} SEK"));
            table.Add("R10 orders per day", l.MaxOrdersPerDay.ToString(c), string.Empty);
            table.Add("R11 actions per minute", l.MaxActionsPerMinute.ToString(c), string.Empty);
            table.Add("R12 same instrument", string.Create(c, $"{l.MinIntervalSameInstrument.TotalSeconds:0} s apart"), string.Empty);
            table.Add("R14 duplicate intent", string.Create(c, $"{l.DuplicateIntentWindow.TotalSeconds:0} s window"), string.Empty);
            table.Add("R5 price collar", string.Create(c, $"±{l.PriceCollarPct:P1} of the live reference"), string.Empty);
            table.Add("R15 quote age", string.Create(c, $"≤ {l.MaxQuoteAge.TotalSeconds:0} s"), string.Empty);
            table.Add("R16 window", string.Create(c, $"{l.WindowOpen:HH\\:mm}–{l.WindowClose:HH\\:mm} (half days to {l.HalfDayWindowClose:HH\\:mm})"), "Stockholm time");
            table.Add("R19 daily loss stop", string.Create(c, $"-{l.DailyLossStopPct:P1} (kill switch)"), string.Create(c, $"{l.DailyLossLimitSek(value):N0} SEK"));
            table.Write(w);
            string capped = l.Capped(value) ? string.Create(c, $", sized on the {l.MaxAccountValueSek:N0} SEK account cap (max_account_value_sek)") : string.Empty;
            w.WriteLine(string.Create(c, $"Sized for an account of {value:N0} SEK{capped}. Always on: R1 account allowlist, R2 instrument allowlist, R3 limit orders only, R4 no short selling, R9 cash, R13 no opposite working order, R17 halts, R18 unknown orders, R20/R21 verified constants and preflight (live only)."));
            return 0;
        }));
        return command;
    }

    // ---- qa audit verify -----------------------------------------------------------------------------

    private static Command AuditCommand()
    {
        var dir = new Option<string>("--dir") { Description = "Audit folder", DefaultValueFactory = _ => DefaultAuditDir };
        var verify = new Command("verify", "Check the audit log's hash chain across all days: an edited, removed or reordered record is reported.");
        verify.Options.Add(dir);
        verify.SetAction(parse => Execute(parse, w =>
        {
            string path = parse.GetValue(dir)!;
            if (!Directory.Exists(path))
            {
                throw new ArgumentException($"No audit folder at '{path}'.");
            }

            AuditVerification v = AuditLog.Verify(path);
            if (v.Valid)
            {
                w.WriteLine($"OK: {v.Records} record(s) in {v.Files} file(s); the chain is intact.");
                return 0;
            }

            w.WriteLine($"BROKEN: {v.Problem}");
            return 2;
        }));
        var command = new Command("audit", "The trading audit log (audit/YYYY-MM-DD.jsonl).");
        command.Subcommands.Add(verify);
        return command;
    }

    // ---- qa kill -------------------------------------------------------------------------------------

    private static Command KillCommand()
    {
        var reason = new Option<string?>("--reason") { Description = "Why (kept in the flag file and the audit log)" };
        var reset = new Option<bool>("--reset") { Description = "Clear the kill switch (only while no session runs; the session cancels its orders when it stops)" };
        var status = new Option<bool>("--status") { Description = "Show whether the kill switch is active" };
        var killFile = new Option<string>("--kill-file") { Description = "The flag file", DefaultValueFactory = _ => DefaultKillFile };
        var stateDir = new Option<string>("--state-dir") { Description = "State folder (killed.json, session.lock)", DefaultValueFactory = _ => DefaultStateDir };
        var auditDir = new Option<string>("--audit-dir") { Description = "Audit folder", DefaultValueFactory = _ => DefaultAuditDir };
        var command = new Command("kill", "KILL SWITCH: stop all trading now. Writes ./KILL; a running session halts within a second and cancels every working order.");
        command.Options.Add(reason);
        command.Options.Add(reset);
        command.Options.Add(status);
        command.Options.Add(killFile);
        command.Options.Add(stateDir);
        command.Options.Add(auditDir);
        command.SetAction(parse => Execute(parse, w =>
        {
            string file = parse.GetValue(killFile)!;
            string state = parse.GetValue(stateDir)!;
            if (parse.GetValue(reset) && parse.GetValue(status))
            {
                throw new ArgumentException("Use --reset or --status, not both.");
            }

            if (parse.GetValue(status))
            {
                KillRecord? r = KillSwitch.RecordedKill(file, state);
                w.WriteLine(r is null ? "Kill switch: not active." : $"Kill switch: ACTIVE since {r.SinceUtc:u} ({r.Source}: {r.Reason}).");
                w.WriteLine(SessionLock.Holder(state) is { } holder ? $"A session is running ({holder})." : "No session is running.");
                return 0;
            }

            if (parse.GetValue(reset))
            {
                var audit = new AuditLog(parse.GetValue(auditDir)!, TimeProvider.System);
                KillResetResult result = KillSwitch.ResetOffline(file, state, audit, parse.GetValue(reason) ?? "qa kill --reset");
                w.WriteLine(result.Message);
                return result.Reset ? 0 : 1;
            }

            KillSwitch.Request(file, parse.GetValue(reason) ?? "qa kill", TimeProvider.System);
            w.WriteLine($"KILL written to {Path.GetFullPath(file)}.");
            w.WriteLine(SessionLock.Holder(state) is { } running
                ? $"The running session ({running}) halts within a second and cancels its working orders."
                : "No session is running; the next one will start halted. Clear it with: qa kill --reset");
            return 0;
        }));
        return command;
    }

    /// <summary>Trading config and state failures become "error: …" lines (exit 1).</summary>
    internal static int Execute(ParseResult parse, Func<TextWriter, int> body) => DataCommands.Execute(parse, w =>
    {
        try
        {
            return body(w);
        }
        catch (Exception ex) when (ex is TradingConfigException or PaperBookException or ModeNotAllowedException or UnauthorizedAccessException)
        {
            throw new ArgumentException(ex.Message, ex);
        }
    });
}
