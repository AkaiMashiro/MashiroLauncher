using Launcher.Cli.Commands;
using Launcher.Core.Common;

namespace Launcher.Cli;

public static class CliRoot
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Log.Warn("Cancellation requested...");
        };

        using var http = BuildHttpClient();
        var downloader = new Downloader(http);

        var command = args[0];
        var rest = args[1..];
        try
        {
            return command switch
            {
                "list-versions" => await ListVersionsCommand.RunAsync(downloader, rest, cts.Token),
                "install"       => await InstallCommand.RunAsync(downloader, rest, cts.Token),
                "install-java"  => await InstallJavaCommand.RunAsync(downloader, rest, cts.Token),
                "print-command" => await PrintCommand.RunAsync(downloader, rest, cts.Token),
                "launch"        => await LaunchCommand.RunAsync(downloader, rest, cts.Token),
                "help" or "--help" or "-h" => Help(),
                _ => UnknownCommand(command),
            };
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Aborted.");
            return 130;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 1;
        }
    }

    private static HttpClient BuildHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MashiroLauncher/0.1");
        return http;
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Log.Error($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Mashiro Launcher CLI

            Usage:
              cli list-versions [--type release|snapshot|all] [--limit N]
              cli install <version-id>
              cli install-java <version-id>
              cli print-command <version-id> --offline --name <name> [--instance <name>]
              cli launch <version-id> --offline --name <name> [--instance <name>]
              cli help
            """);
    }
}
