using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Risk;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli.Commands;

/// <summary><c>qa risk</c>: historical, parametric and Monte Carlo VaR/ES plus stress scenarios.</summary>
internal static class RiskCommand
{
    public static Command Create()
    {
        var data = new DataOptions();
        var weights = new Option<string?>("--weights") { Description = "Comma-separated weights aligned with the instruments (default equal)" };
        var confidence = new Option<double>("--confidence") { Description = "VaR/ES confidence in (0.5, 1)", DefaultValueFactory = _ => 0.99 };
        var paths = new Option<long>("--paths") { Description = "Monte Carlo paths", DefaultValueFactory = _ => 100_000 };
        var seed = new Option<ulong>("--seed") { Description = $"Seed (default {QaCli.DefaultSeed})" };
        var value = new Option<double>("--value") { Description = "Portfolio value in SEK", DefaultValueFactory = _ => 1_000_000 };
        var index = new Option<string?>("--index") { Description = "Index column for beta-scaled equity stress, e.g. OMXS30" };
        var fx = new Option<string?>("--fx-exposed") { Description = "Comma-separated instruments priced in a foreign currency" };

        var command = new Command("risk", "Portfolio VaR/ES (historical, parametric, Monte Carlo) and stress scenarios from a price CSV.");
        data.AddTo(command);
        command.Options.Add(weights);
        command.Options.Add(confidence);
        command.Options.Add(paths);
        command.Options.Add(seed);
        command.Options.Add(value);
        command.Options.Add(index);
        command.Options.Add(fx);

        command.SetAction(parse => QaCli.Execute(parse, output =>
        {
            string? indexName = parse.GetValue(index);
            var (portfolio, indexReturns) = data.Load(parse, indexName);
            string? weightText = parse.GetValue(weights);
            ulong usedSeed = QaCli.SeedOrDefault(parse.GetValue(seed));
            using QeEngine engine = QeEngine.Create(usedSeed);
            RiskReport report = RiskAnalysis.Run(engine, new RiskRequest
            {
                Returns = portfolio,
                Weights = weightText is null ? null : QaCli.ParseDoubles(weightText, "--weights"),
                Confidence = parse.GetValue(confidence),
                Covariance = DataOptions.ParseCovariance(parse.GetValue(data.Covariance)),
                EwmaLambda = parse.GetValue(data.Lambda),
                MonteCarloPaths = parse.GetValue(paths),
                Seed = usedSeed,
                PortfolioValue = parse.GetValue(value),
                IndexReturns = indexReturns,
                IndexName = indexName ?? "index",
                FxExposed = QaCli.SplitList(parse.GetValue(fx)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                PeriodsPerYear = parse.GetValue(data.PeriodsPerYear),
            });

            if (parse.GetValue(data.Json))
            {
                output.WriteLine(JsonSerializer.Serialize(report, QaCli.Json));
                return 0;
            }

            Write(output, report, parse.GetValue(value));
            return 0;
        }));
        return command;
    }

    private static void Write(TextWriter w, RiskReport r, double value)
    {
        w.WriteLine($"Portfolio risk: {r.Instruments.Count} instruments, confidence {r.Confidence:P1}, horizon 1 period");
        foreach (string line in r.Label.Describe())
        {
            w.WriteLine(line);
        }

        w.WriteLine("Weights: " + string.Join(", ", r.Instruments.Select((n, i) => $"{n} {r.Weights[i]:P2}")));
        w.WriteLine($"Covariance: {r.CovarianceMethod}{(r.Shrinkage > 0 ? $" (shrinkage {r.Shrinkage:F3})" : string.Empty)} | annualized volatility {r.AnnualizedVolatility:P2}");
        w.WriteLine();

        var table = new TextTable(("method", false), ("VaR", true), ("ES", true), ("VaR SEK", true), ("ES SEK", true), ("n", true), ("seed", true));
        foreach (VarEsLine l in r.VarEs)
        {
            table.Add(l.Method, l.VarFraction.ToString("P2", CultureInfo.InvariantCulture), l.EsFraction.ToString("P2", CultureInfo.InvariantCulture),
                l.VarAmount.ToString("N0", CultureInfo.InvariantCulture), l.EsAmount.ToString("N0", CultureInfo.InvariantCulture),
                l.Observations == 0 ? "-" : l.Observations.ToString(CultureInfo.InvariantCulture), l.Seed?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }

        table.Write(w);
        w.WriteLine();
        w.WriteLine($"Stress scenarios (portfolio value {value.ToString("N0", CultureInfo.InvariantCulture)} SEK)");
        var stress = new TextTable(("scenario", false), ("P&L SEK", true), ("P&L", true));
        foreach (StressLine s in r.Stress)
        {
            stress.Add(s.Scenario, s.PnlAmount.ToString("N0", CultureInfo.InvariantCulture), s.PnlFraction.ToString("P2", CultureInfo.InvariantCulture));
        }

        stress.Write(w);
        if (r.Betas is { } betas)
        {
            w.WriteLine("Betas: " + string.Join(", ", r.Instruments.Select((n, i) => $"{n} {betas[i]:F2}")));
        }

        foreach (string note in r.Notes)
        {
            w.WriteLine($"Note: {note}");
        }
    }
}
