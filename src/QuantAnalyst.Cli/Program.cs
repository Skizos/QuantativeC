namespace QuantAnalyst.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Block characters (BankID QR) and Swedish names need UTF-8 on the Windows console.
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return QaCli.Run(args, Console.Out, Console.Error);
    }
}
