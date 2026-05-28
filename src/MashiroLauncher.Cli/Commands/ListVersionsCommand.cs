using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Versions.Mojang;

namespace MashiroLauncher.Cli.Commands;

public static class ListVersionsCommand
{
    public static async Task<int> RunAsync(Downloader downloader, string[] args, CancellationToken ct)
    {
        var type = "release";
        var limit = 10;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type" when i + 1 < args.Length:
                    type = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = int.Parse(args[++i]);
                    break;
                default:
                    Log.Error($"Unknown option: {args[i]}");
                    return 1;
            }
        }

        var service = new VersionManifestService(downloader);
        Log.Step("Fetching Mojang version manifest");
        var manifest = await service.GetAsync(ct);

        Log.Detail($"latest.release  = {manifest.Latest.Release}");
        Log.Detail($"latest.snapshot = {manifest.Latest.Snapshot}");

        var filtered = type switch
        {
            "all" => manifest.Versions,
            _ => manifest.Versions.Where(v => v.Type == type).ToList(),
        };

        Console.WriteLine();
        Console.WriteLine($"{"id",-30} {"type",-10} {"released",-25} sha1");
        Console.WriteLine(new string('-', 100));
        foreach (var v in filtered.Take(limit))
        {
            Console.WriteLine($"{v.Id,-30} {v.Type,-10} {v.ReleaseTime:yyyy-MM-dd HH:mm zzz} {v.Sha1[..12]}…");
        }
        return 0;
    }
}
