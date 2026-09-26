using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli.Commands;

/// <summary><c>qa price</c>: Black-Scholes (price + Greeks), CRR lattice, Monte Carlo, or implied vol.</summary>
internal static class PriceCommand
{
    public static Command Create()
    {
        var spot = new Option<double>("--spot") { Description = "Spot price (> 0)", Required = true };
        var strike = new Option<double>("--strike") { Description = "Strike (> 0)", Required = true };
        var expiry = new Option<double>("--expiry") { Description = "Time to expiry in years", Required = true };
        var vol = new Option<double?>("--vol") { Description = "Annualized volatility, e.g. 0.2 (not used with --implied-from)" };
        var rate = new Option<double>("--rate") { Description = "Continuously compounded risk-free rate", DefaultValueFactory = _ => 0.0 };
        var dividend = new Option<double>("--dividend") { Description = "Continuous dividend yield", DefaultValueFactory = _ => 0.0 };
        var type = new Option<string>("--type") { Description = "call or put", DefaultValueFactory = _ => "call" };
        type.AcceptOnlyFromAmong("call", "put");
        var model = new Option<string>("--model") { Description = "bs (price + Greeks), crr (binomial) or mc (Monte Carlo)", DefaultValueFactory = _ => "bs" };
        model.AcceptOnlyFromAmong("bs", "crr", "mc");
        var steps = new Option<int>("--steps") { Description = "CRR steps", DefaultValueFactory = _ => 500 };
        var american = new Option<bool>("--american") { Description = "American exercise (crr)" };
        var paths = new Option<long>("--paths") { Description = "Monte Carlo paths", DefaultValueFactory = _ => 100_000 };
        var seed = new Option<ulong>("--seed") { Description = $"Seed (default {QaCli.DefaultSeed})" };
        var antithetic = new Option<bool>("--antithetic") { Description = "Antithetic variates (mc)" };
        var control = new Option<bool>("--control-variate") { Description = "Control variate on the discounted terminal price (mc)" };
        var sobol = new Option<bool>("--sobol") { Description = "Randomized Sobol QMC (mc)" };
        var impliedFrom = new Option<double?>("--implied-from") { Description = "Solve implied volatility from this option price" };
        var json = new Option<bool>("--json") { Description = "Print JSON" };

        var command = new Command("price", "Price a European/American option and compute Greeks or implied volatility.")
        {
            spot, strike, expiry, vol, rate, dividend, type, model, steps, american, paths, seed, antithetic, control, sobol, impliedFrom, json,
        };
        command.SetAction(parse => QaCli.Execute(parse, output =>
        {
            OptionType optionType = parse.GetValue(type) == "put" ? OptionType.Put : OptionType.Call;
            double s = parse.GetValue(spot);
            double k = parse.GetValue(strike);
            double t = parse.GetValue(expiry);
            double r = parse.GetValue(rate);
            double q = parse.GetValue(dividend);
            ulong usedSeed = QaCli.SeedOrDefault(parse.GetValue(seed));
            using QeEngine engine = QeEngine.Create(usedSeed);

            if (parse.GetValue(impliedFrom) is double target)
            {
                ImpliedVolInput[] ivIn = [new(s, k, r, q, t, target, optionType)];
                var ivOut = new ImpliedVolOutput[1];
                engine.ImpliedVolatility(ivIn, ivOut);
                if (ivOut[0].Status != QeStatus.Ok)
                {
                    throw new ArgumentException($"no implied volatility for price {target.ToString(CultureInfo.InvariantCulture)} (outside no-arbitrage bounds?)");
                }

                return Write(output, parse.GetValue(json), new { model = "implied-vol", type = optionType.ToString(), s, k, r, q, t, price = target, impliedVolatility = ivOut[0].Volatility, iterations = ivOut[0].Iterations },
                    w => w.WriteLine($"implied volatility {ivOut[0].Volatility:F6} ({ivOut[0].Volatility:P2}) from price {target} after {ivOut[0].Iterations} Brent iterations"));
            }

            double sigma = parse.GetValue(vol) ?? throw new ArgumentException("--vol is required unless --implied-from is given");
            var option = new BlackScholesInput(s, k, r, q, sigma, t, optionType);
            string header = $"{optionType} S={s} K={k} r={r:P2} q={q:P2} vol={sigma:P2} T={t:F4}y";
            var bsOut = new BlackScholesOutput[1];
            engine.PriceBlackScholes([option], bsOut);

            switch (parse.GetValue(model))
            {
                case "crr":
                    {
                        ExerciseStyle style = parse.GetValue(american) ? ExerciseStyle.American : ExerciseStyle.European;
                        var latOut = new BlackScholesOutput[1];
                        engine.PriceLattice([new LatticeInput(option, parse.GetValue(steps), style)], latOut);
                        RequireOk(latOut[0].Status, "lattice");
                        return Write(output, parse.GetValue(json), new { model = "crr", exercise = style.ToString(), steps = parse.GetValue(steps), price = latOut[0].Price, blackScholesEuropean = bsOut[0].Price },
                            w =>
                            {
                                w.WriteLine($"CRR binomial ({style}, {parse.GetValue(steps)} steps)  {header}");
                                w.WriteLine($"price                    {latOut[0].Price,12:F6}");
                                w.WriteLine($"Black-Scholes European   {bsOut[0].Price,12:F6}");
                            });
                    }

                case "mc":
                    {
                        var mcOptions = new MonteCarloOptions
                        {
                            Paths = parse.GetValue(paths),
                            Antithetic = parse.GetValue(antithetic),
                            ControlVariate = parse.GetValue(control),
                            Sobol = parse.GetValue(sobol),
                        };
                        MonteCarloResult mc = engine.PriceMonteCarlo(option, mcOptions);
                        double z = mc.StdError > 0 ? (mc.Price - bsOut[0].Price) / mc.StdError : 0;
                        string estimator = string.Join(", ", new[] { mcOptions.Sobol ? "Sobol RQMC" : "pseudo-random", mcOptions.Antithetic ? "antithetic" : null, mcOptions.ControlVariate ? "control variate" : null }.Where(x => x is not null));
                        return Write(output, parse.GetValue(json), new { model = "mc", estimator, price = mc.Price, stdError = mc.StdError, paths = mc.Paths, seed = mc.Seed, blackScholes = bsOut[0].Price, zScore = z },
                            w =>
                            {
                                w.WriteLine($"Monte Carlo ({estimator})  paths={mc.Paths}  seed={mc.Seed}  {header}");
                                w.WriteLine($"price {mc.Price:F6}  SE {mc.StdError:F6}  95% CI [{mc.Price - 1.96 * mc.StdError:F6}, {mc.Price + 1.96 * mc.StdError:F6}]");
                                w.WriteLine($"Black-Scholes reference {bsOut[0].Price:F6} (difference {z:F2} SE)");
                            });
                    }

                default:
                    {
                        var greeks = new BlackScholesGreeks[1];
                        engine.ComputeGreeks([option], greeks);
                        RequireOk(greeks[0].Status, "Greeks (need vol > 0 and expiry > 0)");
                        BlackScholesGreeks g = greeks[0];
                        return Write(output, parse.GetValue(json), new { model = "bs", price = g.Price, delta = g.Delta, gamma = g.Gamma, vega = g.Vega, theta = g.Theta, rho = g.Rho },
                            w =>
                            {
                                w.WriteLine($"Black-Scholes-Merton  {header}");
                                w.WriteLine($"price  {g.Price,14:F6}");
                                w.WriteLine($"delta  {g.Delta,14:F6}");
                                w.WriteLine($"gamma  {g.Gamma,14:F6}");
                                w.WriteLine($"vega   {g.Vega,14:F6}   per 1.00 vol");
                                w.WriteLine($"theta  {g.Theta,14:F6}   per year");
                                w.WriteLine($"rho    {g.Rho,14:F6}   per 1.00 rate");
                            });
                    }
            }
        }));
        return command;
    }

    private static void RequireOk(QeStatus status, string what)
    {
        if (status != QeStatus.Ok)
        {
            throw new ArgumentException($"{what} failed: {status}");
        }
    }

    private static int Write(TextWriter output, bool json, object payload, Action<TextWriter> text)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(payload, QaCli.Json));
        }
        else
        {
            text(output);
        }

        return 0;
    }
}
