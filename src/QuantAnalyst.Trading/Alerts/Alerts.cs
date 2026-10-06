using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantAnalyst.Core.Market;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Trading.Alerts;

/// <summary>How much an alert matters (plan 25).</summary>
public enum AlertLevel
{
    /// <summary>For the record: a day's summary, a test.</summary>
    Info,

    /// <summary>Something needs a look: a halt, a failed decision or import, a failed backup.</summary>
    Warning,

    /// <summary>Trading stopped: the kill switch fired, or a session stopped on an error.</summary>
    Critical,
}

/// <summary>One alert (plan 25).</summary>
/// <param name="Kind">kill, halt, session-failed, decision-failed, import-failed, backup-failed, day-summary or test.</param>
/// <param name="Title">A short headline for the notification, e.g. "Trading stopped".</param>
/// <param name="Text">The whole message, readable on its own (the console shows it after "ALERT: ").</param>
/// <param name="Source">What raised it, e.g. "Paper session".</param>
public sealed record Alert(DateTimeOffset AtUtc, AlertLevel Level, string Kind, string Title, string Text, string Source)
{
    /// <summary>E.g. "2026-10-05 15:02 WARNING Trading halted: …".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{MarketTime.ToStockholm(AtUtc):yyyy-MM-dd HH:mm} {Level.ToString().ToUpperInvariant()} {Title}: {Text}");
}

/// <summary>Shows an alert on the owner's screen (a Windows notification). Best effort: it never throws.</summary>
public interface IAlertNotifier
{
    void Show(Alert alert);
}

/// <summary>The owner's alert settings, <c>config/alerts.json</c> (plan 25); without the file both are on.</summary>
/// <param name="Notifications">Show alerts as Windows notifications.</param>
/// <param name="DaySummary">Raise an info alert with each Paper day's result.</param>
public sealed record AlertSettings(bool Notifications, bool DaySummary)
{
    public const string FileName = "alerts.json";
    public const string Format = "qa-alerts/1";

    public static AlertSettings Default { get; } = new(true, true);

    public static AlertSettings Load(string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        string path = Path.Combine(configDirectory, FileName);
        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement r = doc.RootElement;
            if (r.GetProperty("format").GetString() != Format)
            {
                throw new TradingConfigException($"{path}: format must be {Format}.");
            }

            foreach (JsonProperty p in r.EnumerateObject())
            {
                if (p.Name is not ("format" or "notifications" or "day_summary" or "note"))
                {
                    throw new TradingConfigException($"{path}: unknown setting '{p.Name}' (notifications, day_summary).");
                }
            }

            return new AlertSettings(Flag(r, "notifications"), Flag(r, "day_summary"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TradingConfigException($"{path} is not a valid alert config: {ex.Message}", ex);
        }

        static bool Flag(JsonElement r, string name) => !r.TryGetProperty(name, out JsonElement v) || v.GetBoolean();
    }

    public void Save(string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        var json = new JsonObject
        {
            ["format"] = Format,
            ["notifications"] = Notifications,
            ["day_summary"] = DaySummary,
        };
        File.WriteAllText(Path.Combine(configDirectory, FileName), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}

/// <summary>Every alert, one JSON line each, in <c>state/alerts.jsonl</c> (plan 25): read by <c>qa alerts</c>, <c>qa status</c> and the app.</summary>
public sealed class AlertLog
{
    public const string FileName = "alerts.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // Two processes may append at once (a session and an evening import); Windows refuses a second writer for a moment.
    private static readonly TimeSpan[] Retries = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(250)];

    /// <param name="stateDirectory">The state folder, <c>state</c>.</param>
    public AlertLog(string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(stateDirectory);
        Path = System.IO.Path.Combine(stateDirectory, FileName);
    }

    public string Path { get; }

    /// <summary>Appends <paramref name="alert"/>; throws <see cref="IOException"/> when the file stays busy.</summary>
    public void Append(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        string line = JsonSerializer.Serialize(alert, Json) + "\n";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllText(Path, line);
                return;
            }
            catch (IOException) when (attempt < Retries.Length)
            {
                Thread.Sleep(Retries[attempt]);
            }
        }
    }

    /// <summary>The alerts raised at or after <paramref name="sinceUtc"/>, oldest first; a line that can't be read is skipped.</summary>
    public IReadOnlyList<Alert> Since(DateTimeOffset sinceUtc)
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        var result = new List<Alert>();
        foreach (string line in File.ReadAllLines(Path).Where(l => l.Length > 0))
        {
            try
            {
                if (JsonSerializer.Deserialize<Alert>(line, Json) is { } a && a.AtUtc >= sinceUtc)
                {
                    result.Add(a);
                }
            }
            catch (JsonException)
            {
                // A line cut short (the PC lost power mid-write) costs only itself.
            }
        }

        return result;
    }
}

/// <summary>
/// Plan 25: raises alerts. Each goes through the command's redactor (no secret, cookie, token or full account id), to
/// the console ("ALERT: …"), to <see cref="AlertLog"/>, and, when the settings allow, to the notifier. Nothing here can
/// stop a session: a log or notification that fails is a line on the console. Thread-safe (the kill switch alerts from
/// its file watcher).
/// </summary>
public sealed class Alerter
{
    /// <summary>The same alert (kind and key) again within this time is dropped: a flapping halt alerts once.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(30);

    private readonly AlertLog _log;
    private readonly IAlertNotifier? _notifier;
    private readonly Func<string, string> _redact;
    private readonly TimeProvider _time;
    private readonly TextWriter _output;
    private readonly string _source;
    private readonly Dictionary<string, DateTimeOffset> _lastByKey = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public Alerter(AlertLog log, IAlertNotifier? notifier, AlertSettings settings, Func<string, string> redact, TimeProvider time, TextWriter output, string source)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _notifier = notifier;
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _redact = redact ?? throw new ArgumentNullException(nameof(redact));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public AlertSettings Settings { get; }

    /// <summary>Raises an alert; null when the same <paramref name="key"/> of this kind was raised within <see cref="Quiet"/>.</summary>
    public Alert? Raise(AlertLevel level, string kind, string title, string text, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        Alert alert;
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (key is not null)
            {
                string k = kind + "/" + key;
                if (_lastByKey.TryGetValue(k, out DateTimeOffset last) && now - last < Quiet)
                {
                    return null;
                }

                _lastByKey[k] = now;
            }

            alert = new Alert(now, level, kind, _redact(title), _redact(text), _source);
            _output.WriteLine("ALERT: " + alert.Text);
            try
            {
                _log.Append(alert);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _output.WriteLine($"  (not written to {_log.Path}: {ex.Message})");
            }
        }

        if (Settings.Notifications)
        {
            _notifier?.Show(alert);
        }

        return alert;
    }
}
