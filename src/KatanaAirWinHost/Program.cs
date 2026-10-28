using System.Text;
using KatanaAirWinHost.Cli;
using KatanaAirWinHost.Host;

ConsoleHost.Attach();

// UTF-8 console output; the default code page is GBK on Chinese Windows.
// Throws when output is redirected and there is no console handle.
try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (Exception)
{
    // No console handle.
}

try
{
    if (!CliOptions.TryParse(args, out var options, out var parseError))
    {
        Console.Error.WriteLine(parseError);
        Console.Error.WriteLine();
        Console.Error.WriteLine(CliOptions.HelpText);
        return 2;
    }

    switch (options.Mode)
    {
        case CliOptions.ModeHelp:
            Console.WriteLine(CliOptions.HelpText);
            return 0;

        case CliOptions.ModeApp:
            return AppHost.Run(options);

        default:
            return await Diagnostics.RunAsync(options);
    }
}
catch (Exception ex)
{
    Dialogs.Error("Katana Air Win Host", $"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
