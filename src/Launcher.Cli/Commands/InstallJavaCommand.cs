using Launcher.Core.Common;
using Launcher.Core.Java;
using Launcher.Core.Versions.Mojang;
using Launcher.Core.Versions.Rules;

namespace Launcher.Cli.Commands;

public static class InstallJavaCommand
{
    public static async Task<int> RunAsync(Downloader downloader, string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
        {
            Log.Error("Usage: cli install-java <version-id>");
            return 1;
        }
        var versionId = args[0];

        var manifestSvc = new VersionManifestService(downloader);
        var entry = await manifestSvc.FindAsync(versionId, ct);
        var fetcher = new VersionFetcher(downloader);
        var version = await fetcher.FetchAsync(entry, ct);

        var ctx = RuleContext.Detect();
        var installer = new JreInstaller(downloader);
        var javaPath = await installer.InstallAsync(version.JavaVersion.Component, ctx.Os, ctx.Arch, ct);

        Log.Info($"Java executable: {javaPath}");
        return 0;
    }
}
