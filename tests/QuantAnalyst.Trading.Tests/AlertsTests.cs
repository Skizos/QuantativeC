using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Trading.Alerts;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Tests;

/// <summary>Plan 25: alerts reach the console, the alert log and the notifier, redacted, without repeating themselves.</summary>
public sealed class AlertsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 13, 2, 0, TimeSpan.Zero)); // 15:02 Stockholm
    private readonly StringWriter _output = new();
    private readonly List<Alert> _shown = [];

    public void Dispose() => _dir.Dispose();

    private Alerter Alerter(AlertSettings? settings = null) =>
        new(new AlertLog(_dir.File("state")), new Notifier(_shown), settings ?? AlertSettings.Default, text => text.Replace("1234567", "***567", StringComparison.Ordinal),
            _time, _output, "Paper session");

    [Fact]
    public void AnAlert_GoesToTheConsoleTheLogAndTheNotifier_Redacted()
    {
        Alert a = Alerter().Raise(AlertLevel.Critical, "kill", "Trading stopped", "KILL SWITCH (automatic): account 1234567 lost 2 %.")!;

        Assert.Equal("ALERT: KILL SWITCH (automatic): account ***567 lost 2 %.", _output.ToString().Trim());
        Assert.Equal([a], _shown);
        Assert.Equal([a], new AlertLog(_dir.File("state")).Since(_time.GetUtcNow().AddDays(-1)));
        Assert.Equal("2026-10-05 15:02 CRITICAL Trading stopped: KILL SWITCH (automatic): account ***567 lost 2 %.", a.Describe());
        Assert.Equal("Paper session", a.Source);
    }

    [Fact]
    public void TheSameKeyedAlert_IsRaisedOnceIn30Minutes()
    {
        Alerter alerts = Alerter();
        Assert.NotNull(alerts.Raise(AlertLevel.Warning, "halt", "Trading halted: StaleData", "stale", key: "StaleData"));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(alerts.Raise(AlertLevel.Warning, "halt", "Trading halted: StaleData", "stale again", key: "StaleData"));
        Assert.NotNull(alerts.Raise(AlertLevel.Warning, "halt", "Trading halted: Reconciliation", "mismatch", key: "Reconciliation")); // another reason
        _time.Advance(TimeSpan.FromMinutes(21));
        Assert.NotNull(alerts.Raise(AlertLevel.Warning, "halt", "Trading halted: StaleData", "stale later", key: "StaleData"));
        Assert.Equal(3, _shown.Count);
    }

    [Fact]
    public void WithNotificationsOff_TheAlertIsStillLogged()
    {
        Alerter(new AlertSettings(Notifications: false, DaySummary: true)).Raise(AlertLevel.Warning, "decision-failed", "Decision failed", "no history");
        Assert.Empty(_shown);
        Assert.Single(new AlertLog(_dir.File("state")).Since(DateTimeOffset.MinValue));
        Assert.Contains("ALERT: no history", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLog_ReadsSinceATime_AndSkipsALineCutShort()
    {
        var log = new AlertLog(_dir.File("state"));
        Alerter alerts = Alerter();
        alerts.Raise(AlertLevel.Info, "test", "Test", "first");
        File.AppendAllText(log.Path, "{\"atUtc\":\"2026-10-05T13:0\n"); // the PC lost power mid-write
        _time.Advance(TimeSpan.FromDays(2));
        alerts.Raise(AlertLevel.Info, "test", "Test", "second");

        Assert.Equal(["first", "second"], log.Since(DateTimeOffset.MinValue).Select(a => a.Text));
        Assert.Equal(["second"], log.Since(_time.GetUtcNow().AddDays(-1)).Select(a => a.Text));
        Assert.Empty(new AlertLog(_dir.File("nothing")).Since(DateTimeOffset.MinValue));
    }

    [Fact]
    public void TheSettings_DefaultToOn_RoundTrip_AndRefuseUnknownKeys()
    {
        string config = _dir.File("config");
        Directory.CreateDirectory(config);
        Assert.Equal(AlertSettings.Default, AlertSettings.Load(config));
        Assert.Equal((true, true), (AlertSettings.Default.Notifications, AlertSettings.Default.DaySummary));

        new AlertSettings(false, true).Save(config);
        Assert.Equal(new AlertSettings(false, true), AlertSettings.Load(config));

        File.WriteAllText(Path.Combine(config, AlertSettings.FileName), """{ "format": "qa-alerts/1", "email": "me@example.com" }""");
        Assert.Contains("unknown setting 'email'", Assert.Throws<TradingConfigException>(() => AlertSettings.Load(config)).Message, StringComparison.Ordinal);
    }

    private sealed class Notifier(List<Alert> shown) : IAlertNotifier
    {
        public void Show(Alert alert) => shown.Add(alert);
    }
}
