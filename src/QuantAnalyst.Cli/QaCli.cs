using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantAnalyst.Cli.Commands;
using QuantAnalyst.Native;

namespace QuantAnalyst.Cli;

/// <summary>Builds and runs the <c>qa</c> command tree. Numbers are parsed and printed with the
/// invariant culture ("0.05"), independent of the OS locale.</summary>
internal static class QaCli
{
    /// <summary>Seed used when --seed is not given; every seeded result prints the seed it used.</summary>
    public const ulong DefaultSeed = 20260925;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int Run(string[] args, TextWriter output, TextWriter error) => Run(args, output, error, AvanzaCliServices.Default);

    internal static int Run(string[] args, TextWriter output, TextWriter error, AvanzaCliServices avanza)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            RootCommand root = Build(avanza);
            ParseResult parse = root.Parse(args);
            return parse.Invoke(new InvocationConfiguration { Output = output, Error = error });
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    internal static RootCommand Build(AvanzaCliServices avanza)
    {
        var root = new RootCommand(
            "QuantAnalyst for Avanza - quant research, risk tools and read-only Avanza access. Model outputs only; not financial advice.");
        root.Subcommands.Add(PriceCommand.Create());
        root.Subcommands.Add(RiskCommand.Create());
        root.Subcommands.Add(OptimizeCommand.Create());
        foreach (Command command in AvanzaCommands.Create(avanza).Concat(DataCommands.Create()).Concat(BacktestCommands.Create()).Concat(TradingCommands.Create()))
        {
            root.Subcommands.Add(command);
        }

        return root;
    }

    /// <summary>Runs a command body, converting expected failures into "error: ..." and exit code 1.</summary>
    internal static int Execute(ParseResult parse, Func<TextWriter, int> body)
    {
        TextWriter output = parse.InvocationConfiguration.Output;
        TextWriter error = parse.InvocationConfiguration.Error;
        try
        {
            return body(output);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or QeException
                                       or NativeAbiMismatchException or DllNotFoundException or InvalidOperationException)
        {
            error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    internal static ulong SeedOrDefault(ulong seed) => seed == 0 ? DefaultSeed : seed;

    internal static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static IReadOnlyList<double> ParseDoubles(string text, string optionName) =>
        SplitList(text).Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : throw new ArgumentException($"{optionName}: '{s}' is not a number (use '.' as decimal separator).")).ToList();
}
