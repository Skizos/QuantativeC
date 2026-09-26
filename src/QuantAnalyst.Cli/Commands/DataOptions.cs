using System.CommandLine;
using QuantAnalyst.Analytics.Data;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli.Commands;

/// <summary>Options shared by commands that read a price CSV.</summary>
internal sealed class DataOptions
{
    public Option<string> Prices { get; } = new("--prices") { Description = "CSV with header date,NAME1,NAME2,... (see docs/cli.md)", Required = true };

    public Option<string?> Instruments { get; } = new("--instruments") { Description = "Comma-separated subset (default: all except --index)" };

    public Option<string> Returns { get; } = new("--returns") { Description = "simple or log", DefaultValueFactory = _ => "simple" };

    public Option<string> Covariance { get; } = new("--cov") { Description = "sample, ewma or lw (Ledoit-Wolf)", DefaultValueFactory = _ => "lw" };

    public Option<double> Lambda { get; } = new("--lambda") { Description = "EWMA decay", DefaultValueFactory = _ => 0.94 };

    public Option<int> PeriodsPerYear { get; } = new("--periods-per-year") { Description = "Annualization factor", DefaultValueFactory = _ => 252 };

    public Option<bool> Json { get; } = new("--json") { Description = "Print JSON" };

    public DataOptions()
    {
        Returns.AcceptOnlyFromAmong("simple", "log");
        Covariance.AcceptOnlyFromAmong("sample", "ewma", "lw");
    }

    public void AddTo(Command command)
    {
        command.Options.Add(Prices);
        command.Options.Add(Instruments);
        command.Options.Add(Returns);
        command.Options.Add(Covariance);
        command.Options.Add(Lambda);
        command.Options.Add(PeriodsPerYear);
        command.Options.Add(Json);
    }

    public static CovarianceMethod ParseCovariance(string? text) => text switch
    {
        "sample" => CovarianceMethod.Sample,
        "ewma" => CovarianceMethod.Ewma,
        _ => CovarianceMethod.LedoitWolf,
    };

    /// <summary>Loads prices, computes returns, and splits off the optional index column.</summary>
    public (ReturnsTable Portfolio, double[]? Index) Load(ParseResult parse, string? indexName)
    {
        PriceTable prices = CsvPriceLoader.Load(parse.GetValue(Prices)!);
        ReturnsTable all = prices.ToReturns(parse.GetValue(Returns) == "log" ? ReturnKind.Log : ReturnKind.Simple);
        IReadOnlyList<string> selected = QaCli.SplitList(parse.GetValue(Instruments));
        if (selected.Count == 0)
        {
            selected = all.Instruments.Where(i => !string.Equals(i, indexName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        double[]? index = string.IsNullOrWhiteSpace(indexName) ? null : all.Column(indexName);
        return (all.Select(selected), index);
    }
}
