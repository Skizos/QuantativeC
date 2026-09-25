namespace QuantAnalyst.Cli;

internal static class Program
{
    public static int Main(string[] args) => QaCli.Run(args, Console.Out, Console.Error);
}
