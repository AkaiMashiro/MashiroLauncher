using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Launching;

namespace MashiroLauncher.Cli.Commands;

public static class LaunchCommand
{
    public static async Task<int> RunAsync(Downloader downloader, string[] args, CancellationToken ct)
    {
        LaunchOptions opts;
        try { opts = LaunchOptions.Parse(args, "launch"); }
        catch (Exception ex) { Log.Error(ex.Message); return 1; }

        var pipeline = new LaunchPipeline(downloader);
        // CLI is vanilla-only for now; pass instanceName by name so a future
        // `Modloader` 3rd-positional addition doesn't silently bind to it.
        var plan = await pipeline.PrepareAsync(opts.VersionId, opts.Account, instanceName: opts.InstanceName, ct: ct);

        var launcher = new ProcessLauncher();
        Log.Step($"Launching Minecraft {opts.VersionId} as '{opts.Account.Username}'");
        var exit = await launcher.LaunchAsync(plan, ct);
        Log.Info($"Minecraft exited with code {exit}");
        return exit;
    }
}
