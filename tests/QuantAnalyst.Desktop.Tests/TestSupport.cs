using Microsoft.Extensions.Time.Testing;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Desktop.Core.Engine;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// A throw-away repository folder for the app: the committed config files (limits, costs, calendars, holdout, paper
/// account at 5,000 SEK), an empty allowlist and the solution marker. Nothing here touches the real repository.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public static readonly DateTimeOffset Saturday = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero); // 12:00 Stockholm

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "qa-desktop-tests", Guid.NewGuid().ToString("N"));
        string config = Path.Combine(Root, "config");
        Directory.CreateDirectory(config);
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "config"), "*.json"))
        {
            File.Copy(file, Path.Combine(config, Path.GetFileName(file)));
        }

        File.WriteAllText(Path.Combine(config, "universe.json"), """{ "format": "qa-universe/1", "instruments": [] }""");
        File.WriteAllText(Path.Combine(Root, "QuantAnalyst.sln"), string.Empty);
        Workspace = new Workspace(Root);
    }

    public string Root { get; }

    public Workspace Workspace { get; }

    public FakeTimeProvider Time { get; } = new(Saturday);

    public void AllowEricB() =>
        File.WriteAllText(Path.Combine(Root, "config", "universe.json"),
            """{ "format": "qa-universe/1", "instruments": [ { "orderbook_id": "5240", "ticker": "ERIC B", "name": "Ericsson B" } ] }""");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "QuantAnalyst.sln")) && Directory.Exists(Path.Combine(d.FullName, "src")))
            {
                return d.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>
/// A scripted stand-in for the CLI: records every command line and answers with the next scripted result. It never
/// touches the network, so tests of buttons that would log in to Avanza stay offline.
/// </summary>
internal sealed class ScriptedRunner
{
    private readonly Queue<(int Code, string[] Output, string[] Errors)> _answers = new();

    public List<string[]> Calls { get; } = [];

    /// <summary>Gets the services the last command ran with (e.g. the session observer the app passed).</summary>
    public AvanzaCliServices? LastServices { get; private set; }

    public ScriptedRunner Answer(int code, string[]? output = null, string[]? errors = null)
    {
        _answers.Enqueue((code, output ?? [], errors ?? []));
        return this;
    }

    public int Run(string[] args, TextWriter output, TextWriter error, AvanzaCliServices services)
    {
        lock (Calls)
        {
            Calls.Add(args);
            LastServices = services;
        }

        (int code, string[] lines, string[] errors) = _answers.Count > 0 ? _answers.Dequeue() : (0, [], []);
        foreach (string line in lines)
        {
            output.WriteLine(line);
        }

        foreach (string line in errors)
        {
            error.WriteLine(line);
        }

        return code;
    }
}

/// <summary>User environment variables in memory: tests never touch the real ones (the live-trading account lives there).</summary>
internal sealed class FakeEnvironment : IUserEnvironment
{
    public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

    public string? Read(string name) => Values.GetValueOrDefault(name);

    public void Write(string name, string? value) => Values[name] = value;
}
