using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Launching;

namespace MashiroLauncher.Cli.Commands;

public static class PrintCommand
{
    public static async Task<int> RunAsync(Downloader downloader, string[] args, CancellationToken ct)
    {
        LaunchOptions opts;
        try { opts = LaunchOptions.Parse(args, "print-command"); }
        catch (Exception ex) { Log.Error(ex.Message); return 1; }

        var pipeline = new LaunchPipeline(downloader);
        // Named arg so an additional positional param on PrepareAsync (e.g.
        // Modloader) can't silently swallow our string.
        var plan = await pipeline.PrepareAsync(opts.VersionId, opts.Account, instanceName: opts.InstanceName, ct: ct);

        Console.WriteLine();
        Console.WriteLine($"java     : {plan.JavaExecutable}");
        Console.WriteLine($"workdir  : {plan.GameDirectory}");
        Console.WriteLine($"mainClass: {plan.MainClass}");
        Console.WriteLine();
        Console.WriteLine("command:");
        Console.WriteLine($"  {Quote(plan.JavaExecutable)} \\");
        foreach (var arg in plan.JvmArgs) Console.WriteLine($"    {Quote(arg)} \\");
        Console.WriteLine($"    {plan.MainClass} \\");
        for (var i = 0; i < plan.GameArgs.Count; i++)
        {
            var tail = i == plan.GameArgs.Count - 1 ? "" : " \\";
            Console.WriteLine($"    {Quote(plan.GameArgs[i])}{tail}");
        }
        return 0;
    }

    private static string Quote(string s) =>
        s.Length == 0 || s.Any(c => c == ' ' || c == '"' || c == '\t')
            ? "\"" + s.Replace("\"", "\\\"") + "\""
            : s;
}
