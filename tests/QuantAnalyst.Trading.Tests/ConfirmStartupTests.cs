using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Core;
using QuantAnalyst.Core.Broker;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Accounts;
using QuantAnalyst.Trading.Audit;
using QuantAnalyst.Trading.Kill;
using QuantAnalyst.Trading.Modes;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>A promotion key store holding the key the test gives it.</summary>
internal sealed class MemoryKeys(byte[]? key) : IPromotionKeyStore
{
    public string Name => "test key store";

    public byte[]? Read() => key;

    public void Create(byte[] key) => throw new NotSupportedException();
}

/// <summary>A real (not simulated) channel stand-in: records what it is asked to place, and can say it is not ready.</summary>
internal sealed class RecordingLiveChannel(string? notReady = null) : IBrokerOrderChannel
{
    public string Name => "fake-avanza";

    public string? NotReadyReason => notReady;

    public List<ApprovedOrder> Placed { get; } = [];

    public Task<OrderSubmitResult> PlaceAsync(ApprovedOrder order, CancellationToken ct)
    {
        Placed.Add(order);
        return Task.FromResult(OrderSubmitResult.Accepted(new OrderId($"AVZ-{Placed.Count}")));
    }

    public Task<OrderSubmitResult> ModifyAsync(ApprovedModify modify, CancellationToken ct) => Task.FromResult(OrderSubmitResult.Rejected("not in v1"));

    public Task<OrderSubmitResult> CancelAsync(ApprovedCancel cancel, CancellationToken ct) => Task.FromResult(OrderSubmitResult.Accepted(cancel.BrokerOrderId, "cancelled"));
}

/// <summary>
/// A throw-away repository folder in which every Confirm startup check passes: a promotion to Confirm signed with the
/// test's key and backed by an unchanged evidence file, a verified calendar and courtage class, no kill switch, the
/// session lock held, an intact audit chain, the one ISK allowed, and a ready real channel.
/// </summary>
internal sealed class StartupRig : IDisposable
{
    public const string Evidence = "reports/eod/2026-09-25.json";

    private readonly TempDir _dir = new();

    public StartupRig(TimeProvider time)
    {
        Time = time;
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Root(Evidence))!);
        File.WriteAllText(Root(Evidence), "{\"date\":\"2026-09-25\",\"clean\":true}");
        var evidence = new PromotionEvidence(Evidence, Promotion.Sha256File(Root(Evidence)));
        Promotion.Append(PromotionDirectory, Promotion.Sign(
            new PromotionRecord("Confirm", "Paper", RiskEngineTests.Now.AddDays(-1), "owner", [evidence], Promotion.EvidenceHash([evidence]), ["[ok] test gate"], string.Empty),
            Key));
        new AuditLog(AuditDirectory, time).Append("test", new { note = "the chain has a record" });
        Lock = SessionLock.Acquire(StateDirectory, time);
    }

    public byte[] Key { get; } = Promotion.NewKey();

    public TimeProvider Time { get; }

    public SessionLock Lock { get; }

    public string PromotionDirectory => Root("promotion");

    public string StateDirectory => Root("state");

    public string AuditDirectory => Root("audit");

    public string KillFile => Root(KillSwitch.KillFileName);

    public string Root(string relative) => Path.Combine(_dir.Path, relative);

    public ConfirmStartupInputs Inputs(IBrokerOrderChannel channel) => new()
    {
        PromotionDirectory = PromotionDirectory,
        PromotionKeys = new MemoryKeys(Key),
        EvidenceBaseDirectory = _dir.Path,
        Calendar = OrderGatewayTests.Calendar(new DateOnly(2026, 9, 1)),
        Costs = PaperOrderChannelTests.Mini with { VerifiedOn = new DateOnly(2026, 9, 1) },
        KillFile = KillFile,
        StateDirectory = StateDirectory,
        SessionLock = Lock,
        AuditDirectory = AuditDirectory,
        Account = AccountAllowlist.Resolve("9990001", [AccountStateTests.Isk()]),
        Channel = channel,
        GetVariable = _ => null,
        Time = Time,
    };

    public LiveAuthorization Authorize(IBrokerOrderChannel channel) =>
        ConfirmStartup.Check(Inputs(channel)).Authorization ?? throw new InvalidOperationException("the rig's checks should all pass");

    public void Dispose()
    {
        Lock.Dispose();
        _dir.Dispose();
    }
}

/// <summary>
/// Phase 7 step 4: the Confirm startup gate. Every check runs and is listed; each one failing alone refuses the start;
/// only when all pass is a <see cref="LiveAuthorization"/> issued, for that channel, Confirm and the one account.
/// </summary>
public sealed class ConfirmStartupTests : IDisposable
{
    private static readonly string[] CheckNames =
    [
        "not started from Claude Code", "promotion", "verified constants (R20)", "kill switch", "trading not disabled", "one session",
        "audit chain", "account (R1)", "order channel",
    ];

    private readonly FakeTimeProvider _time = new(RiskEngineTests.Now);
    private readonly RecordingLiveChannel _channel = new();
    private readonly StartupRig _rig;

    public ConfirmStartupTests() => _rig = new StartupRig(_time);

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void WhenEveryCheckPasses_AnAuthorizationIsIssued_ForThatChannelModeAndAccount()
    {
        ConfirmStartupResult r = ConfirmStartup.Check(_rig.Inputs(_channel));

        Assert.True(r.Passed, string.Join("\n", r.Failures));
        Assert.Equal(CheckNames, r.Checks.Select(c => c.Name));
        Assert.All(r.Checks, c => Assert.True(c.Passed));
        LiveAuthorization live = r.Authorization!;
        Assert.Equal((TradingMode.Confirm, AccountStateTests.IskId, RiskEngineTests.Now), (live.Mode, live.Account, live.IssuedUtc));
        Assert.Same(_channel, live.Channel);
        Assert.Equal("[ok] promotion: Confirm allowed by 1 signed record(s), evidence unchanged", r.Checks[1].ToString());
        Assert.Equal("[ok] verified constants (R20): calendar 2026 verified 2026-09-01, courtage class test-mini verified 2026-09-01", r.Checks[2].ToString());
        Assert.Equal("[ok] account (R1): ***001, ISK, tradable, not managed, no credit", r.Checks[7].ToString());
    }

    public static TheoryData<string, string, string> Breaks() => new()
    {
        { "CLAUDECODE set", "not started from Claude Code", "CLAUDECODE is set: Claude Code started this process" },
        { "CLAUDE_CODE_ENTRYPOINT set", "not started from Claude Code", "CLAUDE_CODE_ENTRYPOINT is set" },
        { "no promotion key", "promotion", "no promotion key in test key store" },
        { "another key", "promotion", "the signature does not match" },
        { "evidence changed", "promotion", "evidence reports/eod/2026-09-25.json changed after the promotion" },
        { "never promoted", "promotion", "Mode Confirm is above the promotion state (Paper" },
        { "calendar not verified", "verified constants (R20)", "the 2026 trading calendar has no verified_on date" },
        { "calendar without this year", "verified constants (R20)", "the trading calendar has no 2026" },
        { "courtage not verified", "verified constants (R20)", "the courtage class test-mini has no verified_on date" },
        { "kill switch on", "kill switch", "on since" },
        { "trading disabled", "trading not disabled", "trading-disabled.json exists" },
        { "no session lock", "one session", "this session does not hold the session lock" },
        { "audit chain broken", "audit chain", string.Empty },
        { "account not allowed", "account (R1)", "AVANZA__ALLOWEDACCOUNTIDS is not set" },
        { "channel not ready", "order channel", "'fake-avanza' is not ready: the order format is provisional" },
        { "channel simulated", "order channel", "'fake-sim' is simulated" },
    };

    [Theory]
    [MemberData(nameof(Breaks))]
    public void EachCheckFailingAlone_RefusesTheStart_AndTheWholeListIsShown(string label, string failing, string detail)
    {
        ConfirmStartupResult r = ConfirmStartup.Check(Break(label));

        Assert.False(r.Passed);
        Assert.Null(r.Authorization);
        Assert.Equal(CheckNames, r.Checks.Select(c => c.Name)); // nothing stops the other checks
        StartupCheck failed = Assert.Single(r.Failures);
        Assert.Equal(failing, failed.Name);
        Assert.Contains(detail, failed.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Checks, c => c.Detail.Contains("9990001", StringComparison.Ordinal)); // ids masked
    }

    [Fact]
    public void SeveralFailures_AreAllListed()
    {
        ConfirmStartupResult r = ConfirmStartup.Check(_rig.Inputs(new RecordingLiveChannel("provisional")) with
        {
            GetVariable = name => name == "CLAUDECODE" ? "1" : null,
            SessionLock = null,
        });

        Assert.Equal(["not started from Claude Code", "one session", "order channel"], r.Failures.Select(f => f.Name));
    }

    private ConfirmStartupInputs Break(string label)
    {
        ConfirmStartupInputs ok = _rig.Inputs(_channel);
        switch (label)
        {
            case "CLAUDECODE set":
                return ok with { GetVariable = name => name == "CLAUDECODE" ? "1" : null };
            case "CLAUDE_CODE_ENTRYPOINT set":
                return ok with { GetVariable = name => name == "CLAUDE_CODE_ENTRYPOINT" ? "cli" : null };
            case "no promotion key":
                return ok with { PromotionKeys = new MemoryKeys(null) };
            case "another key":
                return ok with { PromotionKeys = new MemoryKeys(Promotion.NewKey()) };
            case "evidence changed":
                File.AppendAllText(_rig.Root(StartupRig.Evidence), " ");
                return ok;
            case "never promoted":
                return ok with { PromotionDirectory = _rig.Root("no-promotion") };
            case "calendar not verified":
                return ok with { Calendar = OrderGatewayTests.Calendar() };
            case "calendar without this year":
                return ok with
                {
                    Calendar = new MarketCalendar([
                        new CalendarYear("XSTO", 2027, new TimeOnly(9, 0), new TimeOnly(17, 30), new TimeOnly(9, 0), new TimeOnly(13, 0), [], [], "test", new DateOnly(2026, 9, 1)),
                    ]),
                };
            case "courtage not verified":
                return ok with { Costs = PaperOrderChannelTests.Mini };
            case "kill switch on":
                File.WriteAllText(_rig.KillFile, "2026-09-28T07:00:00Z owner test\n");
                return ok;
            case "trading disabled":
                File.WriteAllText(Path.Combine(_rig.StateDirectory, ConfirmStartup.TradingDisabledFile), "{}");
                return ok;
            case "no session lock":
                return ok with { SessionLock = null };
            case "audit chain broken":
                File.AppendAllText(Directory.GetFiles(_rig.AuditDirectory).Single(), "{\"not\":\"chained\"}\n");
                return ok;
            case "account not allowed":
                return ok with { Account = AccountAllowlist.Resolve(null, [AccountStateTests.Isk()]) };
            case "channel not ready":
                return ok with { Channel = new RecordingLiveChannel("the order format is provisional") };
            case "channel simulated":
                return ok with { Channel = new FakeSimulatedChannel() };
            default:
                throw new ArgumentOutOfRangeException(nameof(label), label, null);
        }
    }
}
