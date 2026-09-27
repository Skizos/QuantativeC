using QuantAnalyst.Core;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Data.History;
using QuantAnalyst.Data.Store;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Reports;

namespace QuantAnalyst.Desktop.Core.Charts;

/// <summary>A day's value of the paper account at the close, from its end-of-day report.</summary>
public sealed record PaperDay(DateOnly Date, decimal StartOfDay, decimal End);

/// <summary>Where the charts' points come from: the price store and the audit log, read-only.</summary>
public static class ChartSources
{
    /// <summary>A trading day's close (17:30 Stockholm) as a time, for daily points.</summary>
    public static DateTimeOffset CloseOf(DateOnly date) =>
        MarketTime.TryStockholmToUtc(date.ToDateTime(new TimeOnly(17, 30)), out DateTimeOffset utc) ? utc : new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 30)), TimeSpan.Zero);

    /// <summary>
    /// The stored daily closes of one instrument, oldest first; empty when the store or the instrument's history does
    /// not exist yet. Throws the store's own exceptions when it can't be opened (e.g. a session is writing to it).
    /// </summary>
    public static IReadOnlyList<ChartPoint> DailyCloses(string storePath, OrderbookId id)
    {
        if (!File.Exists(storePath))
        {
            return [];
        }

        using HistoryStore store = HistoryStore.Open(storePath);
        string source = AvanzaChartImporter.AvanzaPriceChart.Name;
        if (store.GetSource(source) is null)
        {
            return [];
        }

        return [.. store.GetDailyBars(id, source).Select(b => new ChartPoint(CloseOf(b.Bar.Date), (double)b.Bar.Close))];
    }

    /// <summary>
    /// The paper account's value at each Paper day's close, oldest first, from the end-of-day reports rebuilt from the
    /// audit log (the same numbers as <c>qa report eod</c>). Days without an account line (a session stopped before any
    /// value was known) are left out.
    /// </summary>
    public static IReadOnlyList<PaperDay> PaperDays(Workspace workspace, TimeProvider time) => PaperDays(Reports(workspace, time));

    /// <summary>The Paper days' values from already rebuilt reports.</summary>
    public static IReadOnlyList<PaperDay> PaperDays(IReadOnlyList<EodReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        return
        [
            .. reports.Where(r => r.Modes.Contains("Paper") && r.Account is not null)
                .Select(r => new PaperDay(r.Date, r.Account!.StartOfDayValue, r.Account.EndValue)),
        ];
    }

    /// <summary>Every day's end-of-day report, oldest first, rebuilt read-only from the audit log (as <c>qa report gate</c> does).</summary>
    public static IReadOnlyList<EodReport> Reports(Workspace workspace, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Directory.Exists(workspace.AuditDir)
            ? [.. AuditDays(workspace.AuditDir).Select(d => EodReport.Build(workspace.AuditDir, d, time))]
            : [];
    }

    /// <summary>The value history as chart points: the first day's start, then each close.</summary>
    public static IReadOnlyList<ChartPoint> PaperValuePoints(IReadOnlyList<PaperDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
        {
            return [];
        }

        var points = new List<ChartPoint> { new(CloseOf(days[0].Date).AddHours(-8.5), (double)days[0].StartOfDay) };
        points.AddRange(days.Select(d => new ChartPoint(CloseOf(d.Date), (double)d.End)));
        return points;
    }

    /// <summary>The (fast, slow) moving-average lengths of the saved strategy when it is ma-cross, else null.</summary>
    public static (int Fast, int Slow)? SavedAverages(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        try
        {
            PaperStrategy? saved = PaperConfig.Load(Path.Combine(workspace.ConfigDir, PaperConfig.FileName)).Strategy;
            return saved is { Name: "ma-cross" } s
                   && s.Parameters.TryGetValue("fast", out string? f) && int.TryParse(f, out int fast) && fast > 0
                   && s.Parameters.TryGetValue("slow", out string? sl) && int.TryParse(sl, out int slow) && slow > fast
                ? (fast, slow)
                : null;
        }
        catch (Exception ex) when (ex is Trading.Risk.TradingConfigException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<DateOnly> AuditDays(string auditDir) =>
        Directory.EnumerateFiles(auditDir, "*.jsonl")
            .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateOnly d) ? d : (DateOnly?)null)
            .OfType<DateOnly>()
            .Order();
}
