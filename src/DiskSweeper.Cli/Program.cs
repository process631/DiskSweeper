using System.Text;
using DiskSweeper.Cli;

Console.OutputEncoding = Encoding.UTF8;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (UsageException ex)
{
    ConsoleOut.WriteLine(ex.Message, ConsoleColor.Red);
    Console.WriteLine("Run 'dsweep --help' to see the options.");
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.Usage);
    return 0;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return options.Command == "finds"
        ? await FindsCommand.RunAsync(options, cancel.Token)
        : await ScanCommand.RunAsync(options, cancel.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    ConsoleOut.WriteLine("Cancelled.", ConsoleColor.Yellow);
    return 130;
}
