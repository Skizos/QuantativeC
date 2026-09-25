using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Analytics.Portfolio;
using QuantAnalyst.Cli.Output;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli.Commands;

/// <summary><c>qa optimize</c>: min-variance, mean-variance, risk parity or HRP weights from a price CSV.</summary>
internal static class OptimizeCommand
{
    public static Command Create()
    {
        var data = new DataOptions();
        var method = new Option<string>("--method") { Description = "minvar, mv, rp (risk parity) or hrp", DefaultValueFactory = _ => "minvar" };
        method.AcceptOnlyFromAmong("minvar", "mv", "rp", "hrp");
        var riskAversion = new Option<double>("--risk-aversion") { Description = "Risk aversion for mv", DefaultValueFactory = _ => 3.0 };
        var min = new Option<double>("--min") { Description = "Per-asset lower bound (minvar, mv)", DefaultValueFactory = _ => 0.0 };
        var max = new Option<double>("--max") { Description = "Per-asset upper bound (minvar, mv)", DefaultValueFactory = _ => 1.0 };
        var index = new Option<string?>("--index") { Description = "Column to exclude from the portfolio (e.g. an index)" };

        var command = new Command("optimize", "Portfolio weights from estimated moments (in-sample; not a backtest).");
        data.AddTo(command);
        command.Options.Add(method);
        command.Options.Add(riskAversion);
        command.Options.Add(min);
        command.Options.Add(max);
        command.Options.Add(index);

        command.SetAction(parse => QaCli.Execute(parse, output =>
        {
            var (portfolio, _) = data.Load(parse, parse.GetValue(index));
            using QeEngine engine = QeEngine.Create(QaCli.DefaultSeed);
            OptimizeReport report = PortfolioOptimization.Run(engine, new OptimizeRequest
            {
                Returns = portfolio,
                Method = parse.GetValue(method) switch
                {
                    "mv" => OptimizationMethod.MeanVariance,
                    "rp" => OptimizationMethod.RiskParity,
                    "hrp" => OptimizationMethod.Hrp,
                    _ => OptimizationMethod.MinVariance,
                },
                Covariance = DataOptions.ParseCovariance(parse.GetValue(data.Covariance)),
                EwmaLambda = parse.GetValue(data.Lambda),
                RiskAversion = parse.GetValue(riskAversion),
                MinWeight = parse.GetValue(min),
                MaxWeight = parse.GetValue(max),
                PeriodsPerYear = parse.GetValue(data.PeriodsPerYear),
            });

            if (parse.GetValue(data.Json))
            {
                output.WriteLine(JsonSerializer.Serialize(report, QaCli.Json));
                return 0;
            }

            output.WriteLine($"Optimization: {report.Method}, covariance {report.CovarianceMethod}{(report.Shrinkage > 0 ? $" (shrinkage {report.Shrinkage:F3})" : string.Empty)}");
            foreach (string line in report.Label.Describe())
            {
                output.WriteLine(line);
            }

            output.WriteLine("In-sample estimate only: not validated out of sample and not a backtest.");
            output.WriteLine();
            var table = new TextTable(("instrument", false), ("weight", true), ("risk share", true));
            foreach (WeightLine l in report.Weights)
            {
                table.Add(l.Instrument, l.Weight.ToString("P2", CultureInfo.InvariantCulture), l.RiskContribution.ToString("P2", CultureInfo.InvariantCulture));
            }

            table.Write(output);
            output.WriteLine();
            output.WriteLine($"Expected return (annualized, sample mean) {report.ExpectedReturnAnnual:P2} | volatility (annualized) {report.VolatilityAnnual:P2}");
            output.WriteLine($"Solver: {(report.Converged ? "converged" : "NOT converged")} after {report.Iterations} iterations, objective {report.Objective:G6}");
            return 0;
        }));
        return command;
    }
}
