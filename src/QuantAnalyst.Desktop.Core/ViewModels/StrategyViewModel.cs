using System.Collections.ObjectModel;
using QuantAnalyst.Analytics.Backtesting;
using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.Mvvm;
using QuantAnalyst.Trading.Paper;
using QuantAnalyst.Trading.Risk;

namespace QuantAnalyst.Desktop.Core.ViewModels;

/// <summary>One parameter field of a strategy form. An empty optional field uses the strategy's default.</summary>
public sealed class ParameterField(StrategyParameter parameter) : ObservableObject
{
    private string _value = parameter.Default ?? string.Empty;

    public string Key => parameter.Key;

    public string Description => parameter.Description;

    public bool Required => parameter.Default is null;

    /// <summary>Gets the hint under the field: "required" or "default 5".</summary>
    public string Hint => parameter.Default is { } d ? $"default {d}" : "required";

    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }
}

/// <summary>A strategy of the catalog with a field per parameter.</summary>
public sealed class StrategyOption(string name)
{
    public string Name { get; } = name;

    public string Summary { get; } = StrategyCatalog.Summary(name);

    public IReadOnlyList<ParameterField> Fields { get; } = [.. StrategyCatalog.ParametersOf(name).Select(p => new ParameterField(p))];

    /// <summary>The filled-in fields as key=value pairs; an empty optional field is left out (its default applies).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Values() =>
        [.. Fields.Where(f => f.Value.Trim().Length > 0).Select(f => new KeyValuePair<string, string>(f.Key, f.Value.Trim()))];

    public IEnumerable<string> MissingRequired() => Fields.Where(f => f.Required && f.Value.Trim().Length == 0).Select(f => f.Key);
}

/// <summary>
/// Pick a strategy and its parameters, see how it did on your instruments (<c>qa backtest run</c> on the allowlist,
/// logged to the trial ledger like every run), and save it as the one Paper trades (<c>qa paper strategy</c>).
/// </summary>
public sealed class StrategyViewModel : PageViewModel
{
    private readonly Workspace _workspace;
    private StrategyOption _selected;
    private string _saved = "none";

    public StrategyViewModel(Workspace workspace, QaEngine engine)
        : base(PageKind.Strategy, "Strategy", "Try a strategy on your instruments' history, then save the one Paper should trade.", engine)
    {
        _workspace = workspace;
        Options = [.. StrategyCatalog.Names.Select(n => new StrategyOption(n))];
        _selected = Options.First(o => o.Name == "ma-cross");
        BacktestCommand = new AsyncCommand(BacktestAsync, () => !IsBusy, ex => Say(ex.Message, isError: true));
        SaveCommand = new AsyncCommand(SaveAsync, () => !IsBusy, ex => Say(ex.Message, isError: true));
    }

    public IReadOnlyList<StrategyOption> Options { get; }

    public StrategyOption Selected
    {
        get => _selected;
        set => Set(ref _selected, value ?? _selected);
    }

    /// <summary>Gets the strategy Paper trades now, e.g. "ma-cross(fast=20, slow=100)", or "none".</summary>
    public string Saved
    {
        get => _saved;
        private set => Set(ref _saved, value);
    }

    /// <summary>Gets the last backtest's output, line by line.</summary>
    public ObservableCollection<string> BacktestOutput { get; } = [];

    public AsyncCommand BacktestCommand { get; }

    public AsyncCommand SaveCommand { get; }

    public override Task RefreshAsync()
    {
        try
        {
            PaperStrategy? saved = PaperConfig.Load(Path.Combine(_workspace.ConfigDir, PaperConfig.FileName)).Strategy;
            if (saved is null)
            {
                Saved = "none: Paper does not know what to trade yet";
                return Task.CompletedTask;
            }

            Saved = StrategyCatalog.Create(saved.Name, saved.Parameters).Spec.Describe();
            if (Options.FirstOrDefault(o => o.Name == saved.Name) is { } option)
            {
                foreach (ParameterField field in option.Fields)
                {
                    if (saved.Parameters.TryGetValue(field.Key, out string? value))
                    {
                        field.Value = value;
                    }
                }

                Selected = option;
            }
        }
        catch (Exception ex) when (ex is TradingConfigException or ArgumentException or IOException)
        {
            Saved = "unreadable";
            Say(ex.Message, isError: true);
        }

        return Task.CompletedTask;
    }

    protected override void OnBusyChanged()
    {
        BacktestCommand.Refresh();
        SaveCommand.Refresh();
    }

    private bool Complete()
    {
        string[] missing = [.. Selected.MissingRequired()];
        if (missing.Length > 0)
        {
            Say($"Fill in {string.Join(" and ", missing)} first.", isError: true);
            return false;
        }

        return true;
    }

    private async Task BacktestAsync()
    {
        if (!Complete())
        {
            return;
        }

        Say($"Backtesting {Selected.Name} on your instruments …");
        BacktestOutput.Clear();
        CommandResult result = await Engine.RunAsync($"Backtest {Selected.Name}", CommandLines.Backtest(_workspace, Selected.Name, Selected.Values()));
        foreach (OutputLine line in result.Lines)
        {
            BacktestOutput.Add(line.Text);
        }

        Say(result.ExitCode switch
        {
            0 => "Done. The run is in the trial ledger. Past results are no promise; the Deflated Sharpe accounts for how many variants you tried.",
            2 => "The run was refused and logged (see the output).",
            _ => $"The backtest did not run: {Why(result)}",
        }, result.ExitCode != 0);
    }

    private async Task SaveAsync()
    {
        if (!Complete())
        {
            return;
        }

        CommandResult result = await Engine.RunAsync($"Save {Selected.Name}", CommandLines.SaveStrategy(_workspace, Selected.Name, Selected.Values()));
        if (!result.Succeeded)
        {
            Say($"Not saved: {Why(result)}", isError: true);
            return;
        }

        await RefreshAsync();
        string? note = result.Lines.FirstOrDefault(l => l.Text.StartsWith("Note:", StringComparison.Ordinal))?.Text;
        Say($"Saved: Paper now trades {Saved}." + (note is null ? string.Empty : " It has not been backtested on your instruments yet."));
    }
}
