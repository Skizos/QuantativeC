using System.Text.Json;
using QuantAnalyst.Core;

namespace QuantAnalyst.Data.Intraday;

/// <summary>The research list's file is broken.</summary>
public sealed class ResearchListException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A share whose intraday bars are collected for research (never traded because it is here).</summary>
public sealed record ResearchEntry(OrderbookId OrderbookId, string Ticker, string Name);

/// <summary>
/// The research list (plan 17 step A2): Stockholm shares whose 1- and 5-minute bars are collected besides the
/// allowlist's, so the intraday test isn't limited to the 5 traded names. It is <c>config/research-universe.json</c>;
/// it has nothing to do with R2's allowlist and never makes a share tradable. At most <see cref="MaxNames"/>.
/// </summary>
public sealed class ResearchList
{
    public const string FileName = "research-universe.json";
    public const string Format = "qa-research-universe/1";
    public const int MaxNames = 30;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly Dictionary<OrderbookId, ResearchEntry> _byId = [];

    public ResearchList(IEnumerable<ResearchEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (ResearchEntry e in entries)
        {
            if (!_byId.TryAdd(e.OrderbookId, e))
            {
                throw new ResearchListException($"Orderbook {e.OrderbookId} ({e.Ticker}) is on the research list twice.");
            }
        }

        if (_byId.Count > MaxNames)
        {
            throw new ResearchListException($"The research list has {_byId.Count} names; at most {MaxNames} (about 60 public chart calls after each close).");
        }
    }

    public static ResearchList Empty { get; } = new([]);

    public IReadOnlyList<ResearchEntry> Entries => [.. _byId.Values.OrderBy(e => e.Ticker, StringComparer.Ordinal)];

    public bool Contains(OrderbookId id) => _byId.ContainsKey(id);

    /// <summary>Reads the file; a missing file is an empty list.</summary>
    public static ResearchList Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("format").GetString() != Format)
            {
                throw new ResearchListException($"{path}: format must be {Format}.");
            }

            return new ResearchList([.. root.GetProperty("instruments").EnumerateArray().Select(i => new ResearchEntry(
                new OrderbookId(i.GetProperty("orderbook_id").GetString()!),
                i.GetProperty("ticker").GetString()!,
                i.GetProperty("name").GetString()!))]);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new ResearchListException($"{path} is not a valid research list: {ex.Message}", ex);
        }
    }

    public ResearchList With(ResearchEntry entry) => new(_byId.Values.Where(e => e.OrderbookId != entry.OrderbookId).Append(entry));

    public ResearchList Without(OrderbookId id) => new(_byId.Values.Where(e => e.OrderbookId != id));

    public void Save(string path)
    {
        var doc = new
        {
            format = Format,
            note = "Shares whose 1- and 5-minute bars are collected for intraday research (plan 17, ADR 0006). Not the allowlist: nothing here is traded. Edit with 'qa intraday research add|remove'.",
            instruments = Entries.Select(e => new { orderbook_id = e.OrderbookId.Value, ticker = e.Ticker, name = e.Name }),
        };
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(doc, Indented) + "\n");
        File.Move(temp, path, overwrite: true);
    }
}
