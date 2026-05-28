using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Installation;

namespace MashiroLauncher.Cli.Commands;

public static class InstallCommand
{
    public static async Task<int> RunAsync(Downloader downloader, string[] args, CancellationToken ct)
    {
        if (args.Length < 1)
        {
            Log.Error("Usage: cli install <version-id>");
            return 1;
        }
        var versionId = args[0];

        var service = new InstallService(downloader);
        var result = await service.InstallVanillaAsync(versionId, ct);

        Log.Step("Install summary");
        Log.Detail($"version:          {result.VersionJson.Id}");
        Log.Detail($"mainClass:        {result.VersionJson.MainClass}");
        Log.Detail($"javaComponent:    {result.VersionJson.JavaVersion.Component}");
        Log.Detail($"javaMajor:        {result.VersionJson.JavaVersion.MajorVersion}");
        Log.Detail($"libraries:        {result.Libraries.Count}");
        Log.Detail($"clientJar:        {result.ClientJarPath}");
        Log.Detail($"assetIndex:       {result.AssetIndexId}");
        Log.Detail($"assetObjects:     {result.AssetCount}");
        Log.Detail($"loggingConfig:    {result.LoggingConfigPath ?? "(none)"}");
        return 0;
    }
}
