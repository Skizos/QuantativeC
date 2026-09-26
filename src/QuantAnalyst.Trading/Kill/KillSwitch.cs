using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Halts;
using QuantAnalyst.Trading.Model;
using QuantAnalyst.Trading.Oms;

namespace QuantAnalyst.Trading.Kill;

public sealed record KillRecord(DateTimeOffset SinceUtc, string Source, string Reason);

public sealed record KillResetResult(bool Reset, string Message);

/// <summary>
/// The kill switch (ADR 0003 §7). <see cref="Trigger"/> is synchronous and cheap: it raises the sticky
/// <see cref="HaltReason.KillSwitch"/> halt, persists <c>state/killed.json</c> and alerts; the cancels run in
/// <see cref="TickAsync"/> through <see cref="OrderGateway"/> (never around it), repeated at most every 10 s while orders
/// are still working. Keeping cancels out of <see cref="Trigger"/> matters: automatic triggers fire inside a gateway call,
/// and cancelling there would wait on that same call.
/// <para>Triggers: <c>qa kill</c> (writes <c>./KILL</c>), the <c>./KILL</c> file (watched, and polled every tick),
/// 3 consecutive broker rejects, the daily loss stop, a Tier A drift or gone order endpoint, an Unknown order older than
/// 2 minutes, and a reconciliation halt older than 60 s.</para>
/// </summary>
public sealed class KillSwitch : IDisposable
{
    public const string KillFileName = "KILL";
    public const string StateFileName = "killed.json";
    public const int ConsecutiveRejectLimit = 3;

    public static readonly TimeSpan CancelRetryInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan UnknownLimit = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ReconciliationLimit = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly OrderGateway _gateway;
    private readonly HaltController _halts;
    private readonly AuditLog _audit;
    private readonly TimeProvider _time;
    private readonly IAccountState? _account;
    private readonly decimal _dailyLossStop;
    private readonly string _killFile;
    private readonly string _stateFile;
    private readonly FileSystemWatcher? _watcher;
    private readonly Lock _lock = new();
    private KillRecord? _record;
    private bool _cancelPending;
    private DateTimeOffset? _lastCancelAttempt;

    /// <param name="killFile">The <c>./KILL</c> flag file (absolute path).</param>
    /// <param name="stateDirectory">Where <c>killed.json</c> lives (<c>state/</c>).</param>
    /// <param name="account">For the daily loss stop; null disables that trigger (R19 still rejects new orders).</param>
    /// <param name="dailyLossStopPct">The R19 limit, e.g. 0.02.</param>
    public KillSwitch(OrderGateway gateway, HaltController halts, AuditLog audit, TimeProvider time, string killFile, string stateDirectory, IAccountState? account, decimal dailyLossStopPct, bool watch = true)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _halts = halts ?? throw new ArgumentNullException(nameof(halts));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _killFile = Path.GetFullPath(killFile);
        _stateFile = Path.Combine(Path.GetFullPath(stateDirectory), StateFileName);
        _account = account;
        _dailyLossStop = dailyLossStopPct;

        // Killed before a restart stays killed.
        if (File.Exists(_stateFile))
        {
            KillRecord prior = ReadState(_stateFile);
            _record = prior;
            _cancelPending = true;
            _halts.Raise(HaltReason.KillSwitch, $"{prior.Source}: {prior.Reason} (since {prior.SinceUtc:u}, from {StateFileName})");
            _audit.Append("kill-restored", new { prior.Source, prior.Reason, prior.SinceUtc });
        }

        _gateway.BrokerRejected += OnBrokerRejected;
        _halts.Raised += OnHaltRaised;
        if (watch && Directory.Exists(Path.GetDirectoryName(_killFile)))
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_killFile)!, Path.GetFileName(_killFile))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => TriggerFromFile();
            _watcher.Changed += (_, _) => TriggerFromFile();
        }

        TriggerFromFile();
    }

    /// <summary>Raised for every alert (console now; toast and e-mail in Phase 8).</summary>
    public event Action<string>? Alerted;

    public bool IsKilled
    {
        get
        {
            lock (_lock)
            {
                return _record is not null;
            }
        }
    }

    public KillRecord? Record
    {
        get
        {
            lock (_lock)
            {
                return _record;
            }
        }
    }

    public string KillFile => _killFile;

    /// <summary>What <c>qa kill</c> does: writes the flag file. A running session picks it up within a second.</summary>
    public static void Request(string killFile, string reason, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(killFile));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(killFile, string.Create(CultureInfo.InvariantCulture, $"{time.GetUtcNow():O} {reason}\n"));
    }

    /// <summary>Fires the kill switch. Idempotent: the first trigger's source and reason are kept.</summary>
    public void Trigger(string source, string reason)
    {
        KillRecord record;
        lock (_lock)
        {
            _cancelPending = true;
            if (_record is not null)
            {
                return;
            }

            record = new KillRecord(_time.GetUtcNow(), source, reason);
            _record = record;
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(record, Json));
        }

        _halts.Raise(HaltReason.KillSwitch, $"{source}: {reason}");
        _audit.Append("kill", new { source, reason, stateFile = StateFileName });
        Alert($"KILL SWITCH ({source}): {reason}. New orders are blocked; working orders are being cancelled.");
    }

    /// <summary>
    /// Runs once a second: polls the flag file, checks the automatic triggers, and, while killed, cancels working
    /// orders through the gateway (again every 10 s until none is left). Returns the number of orders still working.
    /// </summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        TriggerFromFile();
        await CheckAutomaticAsync(ct).ConfigureAwait(false);

        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_record is null || !_cancelPending || (_lastCancelAttempt is { } last && now - last < CancelRetryInterval))
            {
                return Working().Count;
            }

            _lastCancelAttempt = now;
        }

        await _gateway.CancelAllAsync("kill switch", ct).ConfigureAwait(false);
        IReadOnlyList<OmsOrder> left = Working();
        int unknown = _gateway.Oms.Open.Count(o => o.State == OmsState.Unknown);
        if (left.Count == 0)
        {
            lock (_lock)
            {
                _cancelPending = false;
            }

            _audit.Append("kill-cancels-done", new { unknown });
            if (unknown > 0)
            {
                Alert($"{unknown} order(s) have an unknown state; reconciliation must resolve them before a reset.");
            }
        }
        else
        {
            Alert($"{left.Count} order(s) still working after the kill switch's cancels; retrying in {CancelRetryInterval.TotalSeconds:0} s.");
        }

        return left.Count;
    }

    /// <summary>
    /// <c>qa kill --reset</c>: clears the kill switch only when no order is open or unknown and no reconciliation
    /// mismatch is active. Removes <c>./KILL</c> and <c>killed.json</c>.
    /// </summary>
    public KillResetResult Reset(string why)
    {
        if (!IsKilled)
        {
            return new KillResetResult(false, "The kill switch is not active.");
        }

        IReadOnlyList<OmsOrder> open = _gateway.Oms.Open;
        if (open.Count > 0)
        {
            string list = string.Join(", ", open.Select(o => $"{o.Ticker} {o.State}"));
            return new KillResetResult(false, $"Not reset: {open.Count} order(s) are still open or unknown ({list}). Cancel or reconcile them first.");
        }

        if (_halts.IsActive(HaltReason.Reconciliation))
        {
            return new KillResetResult(false, "Not reset: a reconciliation mismatch is active.");
        }

        lock (_lock)
        {
            File.Delete(_killFile);
            File.Delete(_stateFile);
            _record = null;
            _cancelPending = false;
            _lastCancelAttempt = null;
        }

        _halts.ClearKill(why);
        _audit.Append("kill-reset", new { why });
        return new KillResetResult(true, "Kill switch reset. Other halts, if any, still apply.");
    }

    public void Dispose()
    {
        _gateway.BrokerRejected -= OnBrokerRejected;
        _halts.Raised -= OnHaltRaised;
        _watcher?.Dispose();
    }

    private IReadOnlyList<OmsOrder> Working() =>
        [.. _gateway.Oms.Open.Where(o => o.State is OmsState.Working or OmsState.PartiallyFilled)];

    private async Task CheckAutomaticAsync(CancellationToken ct)
    {
        if (IsKilled)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        OmsOrder? stuck = _gateway.Oms.Open.FirstOrDefault(o => o.State == OmsState.Unknown && now - o.UpdatedUtc > UnknownLimit);
        if (stuck is not null)
        {
            Trigger("automatic", $"order {stuck.ClientOrderId} ({stuck.Ticker}) has been Unknown for more than {UnknownLimit.TotalMinutes:0} minutes");
            return;
        }

        HaltState? mismatch = _halts.Active.FirstOrDefault(h => h.Reason == HaltReason.Reconciliation && now - h.SinceUtc > ReconciliationLimit);
        if (mismatch is not null)
        {
            Trigger("automatic", $"reconciliation mismatch for more than {ReconciliationLimit.TotalSeconds:0} s: {mismatch.Detail}");
            return;
        }

        if (_account is not null)
        {
            AccountSnapshot a = await _account.GetAsync(ct).ConfigureAwait(false);
            if (a.StartOfDayValue > 0 && (a.AccountValue - a.StartOfDayValue) / a.StartOfDayValue <= -_dailyLossStop)
            {
                Trigger("automatic", string.Create(CultureInfo.InvariantCulture, $"daily loss stop: {a.AccountValue:N0} SEK vs {a.StartOfDayValue:N0} at the start of the day (limit -{_dailyLossStop:P1})"));
            }
        }
    }

    private void TriggerFromFile()
    {
        if (!IsKilled && File.Exists(_killFile))
        {
            string text;
            try
            {
                text = File.ReadAllText(_killFile).Trim();
            }
            catch (IOException)
            {
                text = string.Empty; // being written; the reason is not needed to stop
            }

            Trigger("file " + KillFileName, text.Length == 0 ? "flag file present" : text);
        }
    }

    private void OnBrokerRejected(int streak, string message)
    {
        if (streak >= ConsecutiveRejectLimit)
        {
            Trigger("automatic", $"{streak} consecutive broker rejects (last: {message})");
        }
    }

    private void OnHaltRaised(HaltState halt)
    {
        if (halt.Reason is HaltReason.SchemaDrift or HaltReason.EndpointGone)
        {
            Trigger("automatic", $"{halt.Reason}: {halt.Detail}");
        }
    }

    private void Alert(string message)
    {
        _audit.Append("alert", new { message });
        Alerted?.Invoke(message);
    }

    private static KillRecord ReadState(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<KillRecord>(File.ReadAllText(path), Json) ?? throw new JsonException("empty");
        }
        catch (JsonException)
        {
            // Unreadable still means killed: fail safe.
            return new KillRecord(File.GetLastWriteTimeUtc(path), "unknown", $"{StateFileName} is unreadable");
        }
    }
}
